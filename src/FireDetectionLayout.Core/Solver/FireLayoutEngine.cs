using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;

namespace FireDetectionLayout.Core.Solver
{
    /// <summary>
    /// End-to-end deterministic AS 1670.1 / AS 1670.4 layout solver.
    /// Pipeline: Stage 1 (Physical wall compartments) -> Stage 2 (Baseline spacing)
    /// -> Stage 3 (Ceiling coordination & MEP clearance) -> Stage 4 (Compliance grading).
    /// </summary>
    public sealed class FireLayoutEngine
    {
        private readonly WallGraphPolygonizer _polygonizer = new WallGraphPolygonizer();
        private readonly CeilingClassifier _ceilingClassifier = new CeilingClassifier();

        public PlacementPlan Solve(SceneSnapshot snapshot, RuleProfile? profile = null)
        {
            var rules = profile ?? RuleProfile.CreateAs1670Default();
            var plan = new PlacementPlan
            {
                ProfileName = rules.ProfileName
            };

            double refCeilingZ = snapshot.Ceilings.Count > 0
                ? snapshot.Ceilings.Average(c => c.UndersideElevationMm)
                : snapshot.Soffits.Count > 0
                    ? snapshot.Soffits.Average(s => s.UndersideElevationMm)
                    : snapshot.Metadata.LevelElevationMm + snapshot.Metadata.DefaultCeilingHeightMm;

            // Stage 1: Physical smoke compartments from walls & ceiling-level downstands.
            var polyResult = _polygonizer.BuildCompartments(
                snapshot.Walls,
                snapshot.Metadata.LevelElevationMm,
                refCeilingZ,
                rules.SmokeDetection);

            plan.UnresolvedOpenEdges.AddRange(polyResult.DanglingEdges);
            if (snapshot.Diagnostics.Count > 0)
                plan.Warnings.AddRange(snapshot.Diagnostics);

            // Stage 1B: Multi-source verification — recover any Rooms, MEP Spaces, or Ceilings
            // that failed to close in the wall centerline graph (e.g. angled curtain walls,
            // wall-thickness offsets, room-separation lines, or unbounded ceiling zones).
            var discovered = DiscoverAndVerifyCompartments(
                polyResult.Compartments,
                snapshot,
                rules.SmokeDetection,
                plan.Warnings);

            int compIndex = 1;
            foreach (var entry in discovered)
            {
                var compPoly = entry.Boundary;
                var compNts = entry.Geometry;
                var compartment = new SmokeCompartment
                {
                    CompartmentId = $"COMP-{compIndex:D3}",
                    SourceKind = entry.SourceKind,
                    Boundary = compPoly,
                    AreaSqM = Math.Round(compNts.Area / 1_000_000.0, 2)
                };
                if (!string.IsNullOrWhiteSpace(entry.DiscoveryNote))
                    compartment.Notes.Add(entry.DiscoveryNote!);
                compIndex++;

                AttachRoomMetadata(compartment, compNts, snapshot.Spaces, snapshot.Rooms, entry.SourceRoom);

                // Skip excluded service/shaft rooms automatically.
                if (IsExcludedByRoomType(compartment, rules.RoomRules))
                {
                    compartment.Status = CompartmentStatus.Skipped;
                    compartment.IsApprovedForPlacement = false;
                    compartment.Notes.Add("Excluded by room-type rule.");
                    plan.Compartments.Add(compartment);
                    continue;
                }

                // Stage 3A: Classify ceiling finish inside this physical compartment.
                double fallbackElevMm = entry.SourceRoom != null && entry.SourceRoom.UnboundedHeightMm >= 1800.0
                    ? snapshot.Metadata.LevelElevationMm + entry.SourceRoom.UnboundedHeightMm
                    : refCeilingZ;
                var ceilingAnalysis = _ceilingClassifier.AnalyzeCompartment(
                    compPoly,
                    snapshot.Ceilings,
                    fallbackElevMm,
                    rules.Ceilings,
                    snapshot.Soffits);

                compartment.CeilingKind = ceilingAnalysis.Kind;
                compartment.CeilingElevationMm = Math.Round(ceilingAnalysis.DominantElevationMm, 1);
                compartment.Notes.AddRange(ceilingAnalysis.Notes);

                double localHeightMm = compartment.CeilingElevationMm - snapshot.Metadata.LevelElevationMm;
                if (localHeightMm > rules.SmokeDetection.StandardFlatCeilingMaxHeightMm)
                {
                    compartment.Status = CompartmentStatus.Amber;
                    compartment.Notes.Add($"Ceiling height ({localHeightMm:F0} mm) exceeds standard flat-ceiling limit; verify AS 1670.1 derating.");
                }

                // Flag if existing detectors are already present.
                var existingInComp = snapshot.ExistingDevices
                    .Where(d => compNts.Contains(new Point(d.Location.X, d.Location.Y)))
                    .ToList();
                if (existingInComp.Count > 0)
                {
                    compartment.Status = CompartmentStatus.Amber;
                    compartment.Notes.Add($"{existingInComp.Count} existing fire device(s) already in compartment.");
                }

                // Flag exposed soffit / no-ceiling spaces as Amber for review, but still solve and allow placement.
                if (ceilingAnalysis.Kind == CeilingKind.ExposedOrNone && compartment.Status == CompartmentStatus.Green)
                {
                    compartment.Status = CompartmentStatus.Amber;
                }

                // Withhold automatic placement when ceiling is unrecognized or sloped.
                if (ceilingAnalysis.Kind == CeilingKind.Sloped ||
                    ceilingAnalysis.Kind == CeilingKind.Unknown)
                {
                    compartment.Status = CompartmentStatus.Red;
                    compartment.IsApprovedForPlacement = false;
                    plan.Compartments.Add(compartment);
                    continue;
                }

                // Stage 2: Build a room-aligned grid-first, X/Y-reach coverage layout.
                var visibilityPoly = BuildVisibilityPolygon(compNts);
                var tile = ceilingAnalysis.PrimaryTileCeiling;
                Polygon? tileFootprint = tile != null && tile.Footprint.OuterRing.Count >= 3
                    ? WallGraphPolygonizer.ToNtsPolygon(tile.Footprint)
                    : null;
                var roomFrame = ComputeDominantRoomFrame(
                    compPoly,
                    ceilingAnalysis.Kind == CeilingKind.SuspendedTile ? tile?.TileGrid : null);
                var targets = BuildCoverageTargets(compNts, roomFrame, rules.SmokeDetection);

                CoverageSelection selected;
                bool requiredMarginSpill = false;
                if (ceilingAnalysis.Kind == CeilingKind.MixedFinish)
                {
                    selected = TryBuildRegularGridSelection(
                        compNts, visibilityPoly, roomFrame, targets, ceilingAnalysis, tileFootprint,
                        tileOnly: true, rules.SmokeDetection)
                        ?? SelectCoverageWithWallOffsetPreference(
                            visibilityPoly,
                            roomFrame,
                            targets,
                            BuildLayoutCandidates(compNts, compPoly, roomFrame, ceilingAnalysis, rules)
                                .Where(c => c.IsTile).ToList(),
                            rules.SmokeDetection);

                    if (!selected.IsComplete)
                    {
                        var allCandidates = BuildLayoutCandidates(compNts, compPoly, roomFrame, ceilingAnalysis, rules);
                        selected = TryBuildRegularGridSelection(
                            compNts, visibilityPoly, roomFrame, targets, ceilingAnalysis, tileFootprint,
                            tileOnly: false, rules.SmokeDetection)
                            ?? SelectCoverageWithWallOffsetPreference(
                                visibilityPoly, roomFrame, targets, allCandidates, rules.SmokeDetection);
                        requiredMarginSpill = selected.Candidates.Any(c => !c.IsTile);
                    }
                }
                else
                {
                    bool tileOnly = ceilingAnalysis.Kind == CeilingKind.SuspendedTile &&
                                    tile != null && tile.TileGrid != null &&
                                    tile.TileGrid.PitchUMm > 0 && tile.TileGrid.PitchVMm > 0;
                    selected = TryBuildRegularGridSelection(
                        compNts, visibilityPoly, roomFrame, targets, ceilingAnalysis, tileFootprint,
                        tileOnly, rules.SmokeDetection)
                        ?? SelectCoverageWithWallOffsetPreference(
                            visibilityPoly,
                            roomFrame,
                            targets,
                            BuildLayoutCandidates(compNts, compPoly, roomFrame, ceilingAnalysis, rules),
                            rules.SmokeDetection);
                }

                if (requiredMarginSpill)
                {
                    compartment.Status = CompartmentStatus.Amber;
                    compartment.Notes.Add("Tile-only placement cannot cover the full compartment; plasterboard margin spillover is required.");
                }

                if (!selected.IsComplete)
                {
                    compartment.Status = CompartmentStatus.Red;
                    compartment.IsApprovedForPlacement = false;
                    compartment.Notes.Add($"Coverage is incomplete ({selected.UncoveredTargetCount} uncovered sample point(s)).");
                }

                // Stage 3B: Coordinate selected positions with MEP clearances.
                var coordinatedDevices = CoordinateWithCeiling(
                    compartment,
                    selected.Candidates,
                    ceilingAnalysis,
                    snapshot.Obstacles,
                    rules);

                // Stage 4: Post-snap compliance verification & grading.
                VerifyAndGradeCompartment(compartment, compNts, visibilityPoly, roomFrame, targets, coordinatedDevices, rules.SmokeDetection);

                compartment.ProposedDetectorCount = coordinatedDevices.Count(d => d.Role == DeviceRole.SmokeDetector);
                compartment.ProposedSpeakerCount = coordinatedDevices.Count(d => d.Role == DeviceRole.EwisSpeaker);

                plan.Compartments.Add(compartment);
                plan.Devices.AddRange(coordinatedDevices);
            }

            BuildSummary(plan);
            return plan;
        }

        private sealed class DiscoveredCompartment
        {
            public Polygon2D Boundary { get; set; } = new Polygon2D();
            public Polygon Geometry { get; set; } = null!;
            public string SourceKind { get; set; } = "Walls";
            public RoomMetadata? SourceRoom { get; set; }
            public string? DiscoveryNote { get; set; }
        }

        private static List<DiscoveredCompartment> DiscoverAndVerifyCompartments(
            IReadOnlyList<Polygon2D> wallCompartments,
            SceneSnapshot snapshot,
            As1670_1Rules rules,
            List<string> warnings)
        {
            var discovered = new List<DiscoveredCompartment>();
            var discoveredGeoms = new List<NetTopologySuite.Geometries.Geometry>();

            foreach (var wallPoly in wallCompartments)
            {
                var nts = WallGraphPolygonizer.TryToValidNtsPolygon(wallPoly);
                if (nts == null || nts.Area / 1_000_000.0 < rules.MinCompartmentAreaSqM)
                    continue;

                discovered.Add(new DiscoveredCompartment
                {
                    Boundary = WallGraphPolygonizer.ToPolygon2D(nts),
                    Geometry = nts,
                    SourceKind = "Walls"
                });
                discoveredGeoms.Add(nts);
            }

            // Verify every MEP Space (preferred) and Architectural Room against discovered compartments.
            var spatialElements = new List<RoomMetadata>(snapshot.Spaces.Count + snapshot.Rooms.Count);
            spatialElements.AddRange(snapshot.Spaces);
            spatialElements.AddRange(snapshot.Rooms);

            foreach (var spatial in spatialElements)
            {
                string kindLabel = spatial.IsSpace ? "Space" : "Room";
                string display = FormatRoomLabel(spatial);

                var spatialPoly = WallGraphPolygonizer.TryToValidNtsPolygon(spatial.Footprint);
                if (spatialPoly == null)
                {
                    // Legacy snapshot or unenclosed spatial element: verify LocationPoint coverage.
                    var locPt = new Point(spatial.LocationPoint.X, spatial.LocationPoint.Y);
                    bool covered = discovered.Any(d => d.Geometry.Covers(locPt));
                    if (!covered)
                    {
                        warnings.Add(
                            $"{kindLabel} '{display}' ({spatial.AreaSqM:F1} m²) was not enclosed by walls and has no spatial boundary footprint; verify room-bounding walls/links.");
                    }
                    continue;
                }

                var uncoveredPieces = SubtractExistingGeometry(spatialPoly, discoveredGeoms, rules.MinCompartmentAreaSqM);
                foreach (var piece in uncoveredPieces)
                {
                    discovered.Add(new DiscoveredCompartment
                    {
                        Boundary = WallGraphPolygonizer.ToPolygon2D(piece),
                        Geometry = piece,
                        SourceKind = kindLabel,
                        SourceRoom = spatial,
                        DiscoveryNote = $"Boundary verified from Revit {kindLabel} '{display}' (wall centerline loop was open)."
                    });
                    discoveredGeoms.Add(piece);
                }
            }

            // Verify every modeled Ceiling footprint so ceilinged areas without rooms or closed walls are not missed.
            foreach (var ceiling in snapshot.Ceilings)
            {
                var ceilPoly = WallGraphPolygonizer.TryToValidNtsPolygon(ceiling.Footprint);
                if (ceilPoly == null)
                    continue;

                var uncoveredPieces = SubtractExistingGeometry(ceilPoly, discoveredGeoms, rules.MinCompartmentAreaSqM);
                foreach (var piece in uncoveredPieces)
                {
                    discovered.Add(new DiscoveredCompartment
                    {
                        Boundary = WallGraphPolygonizer.ToPolygon2D(piece),
                        Geometry = piece,
                        SourceKind = "Ceiling",
                        DiscoveryNote = $"Recovered from ceiling '{ceiling.TypeName}' ({ceiling.ElementId}); no enclosing walls or room/space found."
                    });
                    discoveredGeoms.Add(piece);
                }
            }

            return discovered;
        }

        private static List<Polygon> SubtractExistingGeometry(
            Polygon candidate,
            IReadOnlyList<NetTopologySuite.Geometries.Geometry> existing,
            double minAreaSqM)
        {
            var overlapping = new List<NetTopologySuite.Geometries.Geometry>();
            var env = candidate.EnvelopeInternal;
            foreach (var geom in existing)
            {
                if (geom.EnvelopeInternal.Intersects(env))
                    overlapping.Add(geom);
            }

            NetTopologySuite.Geometries.Geometry remaining = candidate;
            if (overlapping.Count > 0)
            {
                try
                {
                    var union = overlapping.Count == 1
                        ? overlapping[0]
                        : UnaryUnionOp.Union(overlapping);
                    remaining = OverlayNGRobust.Overlay(candidate, union, SpatialFunction.Difference);
                }
                catch (TopologyException)
                {
                    // If robust overlay fails, fall back to keeping candidate only when its interior point is uncovered.
                    var interior = candidate.InteriorPoint;
                    if (overlapping.Any(g => g.Covers(interior)))
                        return new List<Polygon>();
                }
            }

            var pieces = new List<Polygon>();
            CollectSubstantialPolygons(remaining, minAreaSqM, pieces);
            return pieces;
        }

        private static void CollectSubstantialPolygons(
            NetTopologySuite.Geometries.Geometry geom,
            double minAreaSqM,
            List<Polygon> output)
        {
            if (geom == null || geom.IsEmpty)
                return;

            if (geom is Polygon poly)
            {
                if (!poly.IsValid)
                {
                    var repaired = poly.Buffer(0);
                    if (repaired is Polygon rp)
                        poly = rp;
                    else
                    {
                        CollectSubstantialPolygons(repaired, minAreaSqM, output);
                        return;
                    }
                }

                double areaSqM = poly.Area / 1_000_000.0;
                if (areaSqM < minAreaSqM)
                    return;

                // Reject thin wall-thickness slivers (difference between wall centerline & room finish face):
                // hydraulic half-width = 2 * Area / Perimeter (for a rectangle W x H, <= min(W, H)).
                double perimeter = poly.Length;
                double hydraulicWidth = perimeter > 1e-3 ? (2.0 * poly.Area / perimeter) : 0.0;
                if (hydraulicWidth < 350.0)
                    return;

                output.Add(poly);
                return;
            }

            if (geom is GeometryCollection collection)
            {
                for (int i = 0; i < collection.NumGeometries; i++)
                    CollectSubstantialPolygons(collection.GetGeometryN(i), minAreaSqM, output);
            }
        }

        private static string FormatRoomLabel(RoomMetadata room)
        {
            return string.IsNullOrWhiteSpace(room.Number)
                ? room.Name
                : $"{room.Number} {room.Name}".Trim();
        }

        private static void AttachRoomMetadata(
            SmokeCompartment compartment,
            Polygon compNts,
            IReadOnlyList<RoomMetadata> spaces,
            IReadOnlyList<RoomMetadata> rooms,
            RoomMetadata? explicitSource)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddLabel(RoomMetadata rm)
            {
                string label = FormatRoomLabel(rm);
                if (!string.IsNullOrWhiteSpace(label) && seen.Add(label))
                    compartment.RoomNames.Add(label);
            }

            if (explicitSource != null)
                AddLabel(explicitSource);

            foreach (var space in spaces)
            {
                var pt = new Point(space.LocationPoint.X, space.LocationPoint.Y);
                if (compNts.Contains(pt))
                    AddLabel(space);
            }

            foreach (var room in rooms)
            {
                var pt = new Point(room.LocationPoint.X, room.LocationPoint.Y);
                if (compNts.Contains(pt))
                    AddLabel(room);
            }

            compartment.DisplayName = compartment.RoomNames.Count > 0
                ? string.Join(" / ", compartment.RoomNames)
                : compartment.CompartmentId;
        }

        private static bool IsExcludedByRoomType(SmokeCompartment compartment, RoomTypeRules roomRules)
        {
            if (compartment.RoomNames.Count == 0)
                return false;

            string combined = string.Join(" ", compartment.RoomNames).ToLowerInvariant();
            return roomRules.ExcludeKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && combined.Contains(k.ToLowerInvariant()));
        }

        private sealed class LayoutCandidate
        {
            public Point2D Location { get; set; } = new Point2D();
            public bool IsTile { get; set; }
            public double WallClearanceMm { get; set; }
        }

        private sealed class CoverageSelection
        {
            public List<LayoutCandidate> Candidates { get; } = new List<LayoutCandidate>();
            public int UncoveredTargetCount { get; set; }
            public bool IsComplete => UncoveredTargetCount == 0;
        }

        private static List<Point2D> BuildCoverageTargets(Polygon compartment, RoomFrame frame, As1670_1Rules rules)
        {
            var targets = new List<Point2D>();
            var targetKeys = new HashSet<long>();

            // Sample interior points on a room-aligned grid rather than every exterior corner/nib,
            // so tiny doorway jogs or column notches do not force extra detectors into small rooms.
            double step = Math.Max(500.0, Math.Min(1500.0, rules.MaxWallDistanceMm * 0.3));
            double edgeInset = Math.Min(350.0, Math.Max(100.0, rules.HardMinWallClearanceMm * 0.8));

            int nu = Math.Max(1, (int)Math.Ceiling(frame.SpanU / step));
            int nv = Math.Max(1, (int)Math.Ceiling(frame.SpanV / step));
            double du = frame.SpanU / nu;
            double dv = frame.SpanV / nv;

            for (int iu = 0; iu < nu; iu++)
            {
                double u = frame.MinU + (iu + 0.5) * du;
                for (int iv = 0; iv < nv; iv++)
                {
                    double v = frame.MinV + (iv + 0.5) * dv;
                    var candidate = frame.ToWorld(u, v);
                    var pt = new Point(candidate.X, candidate.Y);
                    if (compartment.Contains(pt) && compartment.Boundary.Distance(pt) >= edgeInset)
                        AddUniqueTarget(targets, targetKeys, candidate);
                }
            }

            // Fallback for narrow corridors or small closets where all points are within edgeInset of a wall.
            if (targets.Count == 0)
            {
                for (int iu = 0; iu < nu; iu++)
                {
                    double u = frame.MinU + (iu + 0.5) * du;
                    for (int iv = 0; iv < nv; iv++)
                    {
                        double v = frame.MinV + (iv + 0.5) * dv;
                        var candidate = frame.ToWorld(u, v);
                        if (compartment.Contains(new Point(candidate.X, candidate.Y)))
                            AddUniqueTarget(targets, targetKeys, candidate);
                    }
                }
            }

            var interior = compartment.InteriorPoint;
            AddUniqueTarget(targets, targetKeys, new Point2D(interior.X, interior.Y));
            return targets;
        }

        private static void AddRingTargets(
            Coordinate[] coordinates,
            List<Point2D> targets,
            HashSet<long> targetKeys)
        {
            for (int i = 0; i < coordinates.Length - 1; i++)
            {
                var a = coordinates[i];
                var b = coordinates[i + 1];
                double length = a.Distance(b);
                int samples = Math.Max(1, (int)Math.Ceiling(length / 500.0));
                for (int sample = 0; sample <= samples; sample++)
                {
                    double t = (double)sample / samples;
                    AddUniqueTarget(targets, targetKeys, new Point2D(
                        a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t));
                }
            }
        }

        private static void AddUniqueTarget(List<Point2D> targets, HashSet<long> targetKeys, Point2D candidate)
        {
            if (targetKeys.Add(GetPointKey(candidate)))
                targets.Add(candidate);
        }

        private sealed class RoomFrame
        {
            public Point2D AxisU { get; set; } = new Point2D(1, 0);
            public Point2D AxisV { get; set; } = new Point2D(0, 1);
            public double MinU { get; set; }
            public double MaxU { get; set; }
            public double MinV { get; set; }
            public double MaxV { get; set; }
            public double SpanU => MaxU - MinU;
            public double SpanV => MaxV - MinV;

            public Point2D ToWorld(double u, double v) =>
                new Point2D(u * AxisU.X + v * AxisV.X, u * AxisU.Y + v * AxisV.Y);

            public double AxialReach(Point2D a, Point2D b)
            {
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double du = Math.Abs(dx * AxisU.X + dy * AxisU.Y);
                double dv = Math.Abs(dx * AxisV.X + dy * AxisV.Y);
                return Math.Max(du, dv);
            }
        }

        private static RoomFrame ComputeDominantRoomFrame(Polygon2D compartmentPolygon, TileGridSpec? preferredGrid)
        {
            Point2D axisU = new Point2D(1, 0);
            Point2D axisV = new Point2D(0, 1);

            if (preferredGrid != null &&
                (Math.Abs(preferredGrid.AxisU.X) > 1e-6 || Math.Abs(preferredGrid.AxisU.Y) > 1e-6))
            {
                double len = Math.Sqrt(preferredGrid.AxisU.X * preferredGrid.AxisU.X + preferredGrid.AxisU.Y * preferredGrid.AxisU.Y);
                axisU = new Point2D(preferredGrid.AxisU.X / len, preferredGrid.AxisU.Y / len);
                axisV = new Point2D(-axisU.Y, axisU.X);
            }
            else if (compartmentPolygon.OuterRing.Count >= 3)
            {
                var ring = compartmentPolygon.OuterRing;
                var binWeights = new double[90];
                var edges = new List<(double Angle, double Length)>();

                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (length < 100.0)
                        continue;

                    double angle = Math.Atan2(dy, dx);
                    while (angle < 0) angle += Math.PI * 0.5;
                    while (angle >= Math.PI * 0.5) angle -= Math.PI * 0.5;

                    int bin = Math.Min(89, (int)Math.Floor(angle * 180.0 / Math.PI));
                    binWeights[bin] += length;
                    edges.Add((angle, length));
                }

                int bestBin = 0;
                double bestWeight = -1.0;
                for (int b = 0; b < 90; b++)
                {
                    double neighborhood = binWeights[(b + 89) % 90] + binWeights[b] + binWeights[(b + 1) % 90];
                    if (neighborhood > bestWeight)
                    {
                        bestWeight = neighborhood;
                        bestBin = b;
                    }
                }

                if (bestWeight > 0)
                {
                    double targetRad = (bestBin + 0.5) * Math.PI / 180.0;
                    double sumSin = 0, sumCos = 0;
                    foreach (var edge in edges)
                    {
                        double diff = Math.Abs(edge.Angle - targetRad);
                        diff = Math.Min(diff, Math.PI * 0.5 - diff);
                        if (diff <= 3.0 * Math.PI / 180.0)
                        {
                            // Double-angle (4x for 90-degree periodicity) circular mean.
                            sumSin += edge.Length * Math.Sin(4.0 * edge.Angle);
                            sumCos += edge.Length * Math.Cos(4.0 * edge.Angle);
                        }
                    }

                    double dominantAngle = (Math.Abs(sumSin) > 1e-9 || Math.Abs(sumCos) > 1e-9)
                        ? 0.25 * Math.Atan2(sumSin, sumCos)
                        : targetRad;
                    if (dominantAngle < 0) dominantAngle += Math.PI * 0.5;

                    // Snap near-axis-aligned rooms (within 0.25 deg) cleanly to world axes.
                    if (dominantAngle < 0.25 * Math.PI / 180.0 || Math.Abs(dominantAngle - Math.PI * 0.5) < 0.25 * Math.PI / 180.0)
                        dominantAngle = 0.0;

                    axisU = new Point2D(Math.Cos(dominantAngle), Math.Sin(dominantAngle));
                    axisV = new Point2D(-axisU.Y, axisU.X);
                }
            }

            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            foreach (var pt in compartmentPolygon.OuterRing)
            {
                double u = pt.X * axisU.X + pt.Y * axisU.Y;
                double v = pt.X * axisV.X + pt.Y * axisV.Y;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            return new RoomFrame
            {
                AxisU = axisU,
                AxisV = axisV,
                MinU = minU,
                MaxU = maxU,
                MinV = minV,
                MaxV = maxV
            };
        }

        private static Polygon BuildVisibilityPolygon(Polygon compartment)
        {
            if (compartment.NumInteriorRings == 0)
                return compartment;

            // Small interior holes (< 1 m^2 and < 1000 mm wide, e.g. structural columns/risers)
            // do not obstruct smoke travel, whereas full rooms/cores do.
            var blockingHoles = new List<LinearRing>();
            var factory = compartment.Factory;
            for (int i = 0; i < compartment.NumInteriorRings; i++)
            {
                var ring = (LinearRing)compartment.GetInteriorRingN(i);
                var holePoly = factory.CreatePolygon(ring);
                var env = holePoly.EnvelopeInternal;
                if (holePoly.Area >= 1_000_000.0 || Math.Max(env.Width, env.Height) >= 1000.0)
                    blockingHoles.Add(ring);
            }

            if (blockingHoles.Count == compartment.NumInteriorRings)
                return compartment;

            return factory.CreatePolygon((LinearRing)compartment.ExteriorRing, blockingHoles.ToArray());
        }

        private static CoverageSelection? TryBuildRegularGridSelection(
            Polygon compartment,
            Polygon visibilityPoly,
            RoomFrame frame,
            IReadOnlyList<Point2D> targets,
            CompartmentCeilingAnalysis ceiling,
            Polygon? tileFootprint,
            bool tileOnly,
            As1670_1Rules rules)
        {
            if (frame.SpanU <= 1.0 || frame.SpanV <= 1.0)
                return null;

            var tileGrid = ceiling.PrimaryTileCeiling?.TileGrid;
            bool hasTileGrid = tileGrid != null && tileGrid.PitchUMm > 0 && tileGrid.PitchVMm > 0;
            bool isMixed = ceiling.Kind == CeilingKind.MixedFinish;
            if (tileOnly && !hasTileGrid)
                return null;

            double minReachU = Math.Max(1000.0, Math.Min(rules.MaxWallDistanceMm * 2.0, rules.MaxDetectorSpacingMm));
            int minNu = Math.Max(1, (int)Math.Ceiling(frame.SpanU / minReachU));
            int minNv = Math.Max(1, (int)Math.Ceiling(frame.SpanV / minReachU));
            int maxNu = Math.Min(12, minNu + 1);
            int maxNv = Math.Min(12, minNv + 1);
            int maxAllowedGridCount = Math.Max(minNu * minNv + 1, (int)Math.Ceiling(minNu * minNv * 1.5));

            var gridSizes = new List<(int Nu, int Nv)>();
            for (int nu = 1; nu <= maxNu; nu++)
            {
                for (int nv = 1; nv <= maxNv; nv++)
                {
                    if (nu * nv <= 48)
                        gridSizes.Add((nu, nv));
                }
            }
            gridSizes.Sort((a, b) => (a.Nu * a.Nv).CompareTo(b.Nu * b.Nv));

            double minReasonableSpacing = Math.Min(
                rules.MaxDetectorSpacingMm * 0.25,
                Math.Max(rules.MinWallClearanceMm * 2.0, 1000.0));

            CoverageSelection? bestSelection = null;
            int bestCount = int.MaxValue;
            bool bestMeetsWallOffset = false;
            double bestMinSpacing = -1.0;
            double bestMinWallClearance = -1.0;
            double bestWorstReach = double.PositiveInfinity;

            foreach (var (nu, nv) in gridSizes)
            {
                if (bestSelection != null && nu * nv > bestCount * 2)
                    break;

                // Never subdivide an axis so tightly that detectors bunch closer than minReasonableSpacing.
                if (nu > 1 && frame.SpanU / nu < minReasonableSpacing)
                    continue;
                if (nv > 1 && frame.SpanV / nv < minReasonableSpacing)
                    continue;

                var gridCandidates = new List<LayoutCandidate>();
                var keys = new HashSet<long>();
                double cellSpanU = frame.SpanU / nu;
                double cellSpanV = frame.SpanV / nv;

                for (int iu = 0; iu < nu; iu++)
                {
                    double u = ComputeGridCoordinate(frame.MinU, frame.MaxU, nu, iu, rules.MinWallClearanceMm);
                    double cellMinU = frame.MinU + iu * cellSpanU;
                    double cellMaxU = cellMinU + cellSpanU;

                    for (int iv = 0; iv < nv; iv++)
                    {
                        double v = ComputeGridCoordinate(frame.MinV, frame.MaxV, nv, iv, rules.MinWallClearanceMm);
                        double cellMinV = frame.MinV + iv * cellSpanV;
                        double cellMaxV = cellMinV + cellSpanV;

                        var rawPt = frame.ToWorld(u, v);
                        var resolvedPt = ResolveGridPointInCell(
                            compartment, frame, rawPt, cellMinU, cellMaxU, cellMinV, cellMaxV, rules);
                        if (resolvedPt == null)
                            continue;

                        if (tileOnly)
                        {
                            var snapped = FindNearestValidTileCenter(
                                compartment, tileFootprint, tileGrid!, rules, resolvedPt, requirePreferredClearance: true)
                                ?? FindNearestValidTileCenter(
                                    compartment, tileFootprint, tileGrid!, rules, resolvedPt, requirePreferredClearance: false);
                            if (snapped != null && keys.Add(GetPointKey(snapped)))
                            {
                                gridCandidates.Add(new LayoutCandidate
                                {
                                    Location = snapped,
                                    IsTile = true,
                                    WallClearanceMm = compartment.Boundary.Distance(new Point(snapped.X, snapped.Y))
                                });
                            }
                        }
                        else if (isMixed && hasTileGrid && tileFootprint != null &&
                                 tileFootprint.Contains(new Point(resolvedPt.X, resolvedPt.Y)))
                        {
                            var snapped = FindNearestValidTileCenter(
                                compartment, tileFootprint, tileGrid!, rules, resolvedPt, requirePreferredClearance: true)
                                ?? FindNearestValidTileCenter(
                                    compartment, tileFootprint, tileGrid!, rules, resolvedPt, requirePreferredClearance: false);
                            if (snapped != null && keys.Add(GetPointKey(snapped)))
                            {
                                gridCandidates.Add(new LayoutCandidate
                                {
                                    Location = snapped,
                                    IsTile = true,
                                    WallClearanceMm = compartment.Boundary.Distance(new Point(snapped.X, snapped.Y))
                                });
                            }
                        }
                        else
                        {
                            var pt = new Point(resolvedPt.X, resolvedPt.Y);
                            if (!compartment.Contains(pt))
                                continue;

                            double clearance = compartment.Boundary.Distance(pt);
                            if (clearance < rules.HardMinWallClearanceMm)
                                continue;

                            if (keys.Add(GetPointKey(resolvedPt)))
                            {
                                gridCandidates.Add(new LayoutCandidate
                                {
                                    Location = resolvedPt,
                                    IsTile = false,
                                    WallClearanceMm = clearance
                                });
                            }
                        }
                    }
                }

                if (gridCandidates.Count == 0 || gridCandidates.Count > bestCount || gridCandidates.Count > maxAllowedGridCount)
                    continue;

                if (!IsCompleteAxialCoverage(visibilityPoly, frame, targets, gridCandidates, rules, out double worstReach))
                    continue;

                if (!MeetsMaxNearestNeighborSpacing(gridCandidates, rules))
                    continue;

                int count = gridCandidates.Count;
                bool meetsWallOffset = gridCandidates.All(c => c.WallClearanceMm >= rules.MinWallClearanceMm - 1e-6);
                double minSpacing = ComputeMinPairwiseDistance(gridCandidates);
                double minWallClearance = gridCandidates.Min(c => c.WallClearanceMm);

                bool isBetter = false;
                if (bestSelection == null || count < bestCount)
                {
                    isBetter = true;
                }
                else if (count == bestCount)
                {
                    if (meetsWallOffset != bestMeetsWallOffset)
                        isBetter = meetsWallOffset;
                    else if (Math.Abs(minSpacing - bestMinSpacing) > 50.0)
                        isBetter = minSpacing > bestMinSpacing;
                    else if (Math.Abs(minWallClearance - bestMinWallClearance) > 25.0)
                        isBetter = minWallClearance > bestMinWallClearance;
                    else
                        isBetter = worstReach < bestWorstReach;
                }

                if (isBetter)
                {
                    var selection = new CoverageSelection { UncoveredTargetCount = 0 };
                    selection.Candidates.AddRange(gridCandidates);
                    bestSelection = selection;
                    bestCount = count;
                    bestMeetsWallOffset = meetsWallOffset;
                    bestMinSpacing = minSpacing;
                    bestMinWallClearance = minWallClearance;
                    bestWorstReach = worstReach;
                }
            }

            return bestSelection;
        }

        private static Point2D? ResolveGridPointInCell(
            Polygon compartment,
            RoomFrame frame,
            Point2D rawPt,
            double cellMinU,
            double cellMaxU,
            double cellMinV,
            double cellMaxV,
            As1670_1Rules rules)
        {
            var pt = new Point(rawPt.X, rawPt.Y);
            if (compartment.Contains(pt) && compartment.Boundary.Distance(pt) >= rules.MinWallClearanceMm)
                return rawPt;

            // Clip the grid cell against the compartment so corridors with side alcoves or
            // L-shaped rooms center the detector within the actual occupied portion of the cell.
            var c0 = frame.ToWorld(cellMinU, cellMinV);
            var c1 = frame.ToWorld(cellMaxU, cellMinV);
            var c2 = frame.ToWorld(cellMaxU, cellMaxV);
            var c3 = frame.ToWorld(cellMinU, cellMaxV);
            var cellPoly = new GeometryFactory().CreatePolygon(new[]
            {
                new Coordinate(c0.X, c0.Y),
                new Coordinate(c1.X, c1.Y),
                new Coordinate(c2.X, c2.Y),
                new Coordinate(c3.X, c3.Y),
                new Coordinate(c0.X, c0.Y)
            });

            var clip = compartment.Intersection(cellPoly);
            if (clip.IsEmpty || clip.Area < 250000.0) // < 0.25 m^2 sliver
            {
                return compartment.Contains(pt) && compartment.Boundary.Distance(pt) >= rules.HardMinWallClearanceMm
                    ? rawPt
                    : null;
            }

            // Compute the local (U, V) midpoint of the clipped cell to keep corridor centerlines crisp.
            double localMinU = double.PositiveInfinity, localMaxU = double.NegativeInfinity;
            double localMinV = double.PositiveInfinity, localMaxV = double.NegativeInfinity;
            foreach (var coord in clip.Coordinates)
            {
                double cu = coord.X * frame.AxisU.X + coord.Y * frame.AxisU.Y;
                double cv = coord.X * frame.AxisV.X + coord.Y * frame.AxisV.Y;
                if (cu < localMinU) localMinU = cu;
                if (cu > localMaxU) localMaxU = cu;
                if (cv < localMinV) localMinV = cv;
                if (cv > localMaxV) localMaxV = cv;
            }

            var localMid = frame.ToWorld((localMinU + localMaxU) * 0.5, (localMinV + localMaxV) * 0.5);
            var localMidPt = new Point(localMid.X, localMid.Y);
            if (compartment.Contains(localMidPt) && compartment.Boundary.Distance(localMidPt) >= rules.HardMinWallClearanceMm)
                return localMid;

            var interior = clip.InteriorPoint;
            if (compartment.Contains(interior) && compartment.Boundary.Distance(interior) >= rules.HardMinWallClearanceMm)
                return new Point2D(interior.X, interior.Y);

            return compartment.Contains(pt) && compartment.Boundary.Distance(pt) >= rules.HardMinWallClearanceMm
                ? rawPt
                : null;
        }

        private static double ComputeGridCoordinate(double min, double max, int count, int index, double minWallOffsetMm)
        {
            double span = max - min;
            if (count <= 1)
                return min + span * 0.5;

            double standardOffset = 0.5 * (span / count);
            if (standardOffset >= minWallOffsetMm || span <= 2.0 * minWallOffsetMm)
                return min + (index + 0.5) * (span / count);

            double insetMin = min + minWallOffsetMm;
            double insetSpan = span - 2.0 * minWallOffsetMm;
            return insetMin + index * (insetSpan / (count - 1));
        }

        private static Point2D? FindNearestValidTileCenter(
            Polygon compartment,
            Polygon? tileFootprint,
            TileGridSpec grid,
            As1670_1Rules rules,
            Point2D target,
            bool requirePreferredClearance)
        {
            double minClearance = requirePreferredClearance ? rules.MinWallClearanceMm : rules.HardMinWallClearanceMm;
            double dx = target.X - grid.OriginMm.X;
            double dy = target.Y - grid.OriginMm.Y;
            double u = dx * grid.AxisU.X + dy * grid.AxisU.Y;
            double v = dx * grid.AxisV.X + dy * grid.AxisV.Y;
            int baseIndexU = (int)Math.Floor(u / grid.PitchUMm);
            int baseIndexV = (int)Math.Floor(v / grid.PitchVMm);

            Point2D? bestCenter = null;
            double bestDistance = double.PositiveInfinity;
            for (int indexU = baseIndexU - 2; indexU <= baseIndexU + 2; indexU++)
            {
                for (int indexV = baseIndexV - 2; indexV <= baseIndexV + 2; indexV++)
                {
                    var center = GetTileCenter(indexU, indexV, grid);
                    var pt = new Point(center.X, center.Y);
                    if (!compartment.Contains(pt) || compartment.Boundary.Distance(pt) < minClearance)
                        continue;
                    if (tileFootprint != null && !tileFootprint.Contains(pt))
                        continue;

                    double distance = center.DistanceTo(target);
                    if (distance < bestDistance)
                    {
                        bestCenter = center;
                        bestDistance = distance;
                    }
                }
            }

            return bestCenter;
        }

        private static bool IsCompleteAxialCoverage(
            Polygon compartment,
            RoomFrame frame,
            IReadOnlyList<Point2D> targets,
            IReadOnlyList<LayoutCandidate> candidates,
            As1670_1Rules rules,
            out double worstReachMm)
        {
            worstReachMm = 0.0;
            foreach (var target in targets)
            {
                double nearest = double.PositiveInfinity;
                for (int i = 0; i < candidates.Count; i++)
                {
                    double reach = frame.AxialReach(candidates[i].Location, target);
                    if (reach > rules.MaxWallDistanceMm || reach >= nearest)
                        continue;
                    if (VisibilityGeometry.IsVisible(compartment, candidates[i].Location, target))
                        nearest = reach;
                }

                if (double.IsPositiveInfinity(nearest))
                    return false;

                if (nearest > worstReachMm)
                    worstReachMm = nearest;
            }

            return true;
        }

        private static bool MeetsMaxNearestNeighborSpacing(
            IReadOnlyList<LayoutCandidate> candidates,
            As1670_1Rules rules)
        {
            if (candidates.Count < 2)
                return true;

            double limit = rules.MaxDetectorSpacingMm * 1.001;
            for (int i = 0; i < candidates.Count; i++)
            {
                double nearest = double.PositiveInfinity;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i == j) continue;
                    double d = candidates[i].Location.DistanceTo(candidates[j].Location);
                    if (d < nearest) nearest = d;
                }
                if (nearest > limit)
                    return false;
            }

            return true;
        }

        private static double ComputeMinPairwiseDistance(IReadOnlyList<LayoutCandidate> candidates)
        {
            if (candidates.Count < 2)
                return double.PositiveInfinity;

            double min = double.PositiveInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    double d = candidates[i].Location.DistanceTo(candidates[j].Location);
                    if (d < min) min = d;
                }
            }

            return min;
        }

        private static List<LayoutCandidate> BuildLayoutCandidates(
            Polygon compartment,
            Polygon2D compartmentPolygon,
            RoomFrame roomFrame,
            CompartmentCeilingAnalysis ceiling,
            RuleProfile rules)
        {
            var result = new List<LayoutCandidate>();
            var candidateKeys = new HashSet<long>();
            var tile = ceiling.PrimaryTileCeiling;
            Polygon? tileFootprint = tile != null && tile.Footprint.OuterRing.Count >= 3
                ? WallGraphPolygonizer.ToNtsPolygon(tile.Footprint)
                : null;

            if (tile != null && tile.TileGrid != null &&
                tile.TileGrid.PitchUMm > 0 && tile.TileGrid.PitchVMm > 0)
            {
                AddTileCenterCandidates(
                    compartment, compartmentPolygon, tileFootprint, tile.TileGrid,
                    rules.SmokeDetection, result, candidateKeys);
            }

            bool mixed = ceiling.Kind == CeilingKind.MixedFinish;
            bool tileOnly = ceiling.Kind == CeilingKind.SuspendedTile &&
                            tile != null && tile.TileGrid != null &&
                            tile.TileGrid.PitchUMm > 0 && tile.TileGrid.PitchVMm > 0;
            if (!tileOnly)
                AddGeneralCandidates(
                    compartment, roomFrame, rules.SmokeDetection, tileFootprint,
                    mixed, result, candidateKeys);

            if (result.Count == 0 && !tileOnly && (!mixed || tileFootprint == null))
            {
                var interior = compartment.InteriorPoint;
                AddCandidate(
                    compartment, result, new Point2D(interior.X, interior.Y), false,
                    rules.SmokeDetection, candidateKeys);
            }

            result.Sort((left, right) => right.WallClearanceMm.CompareTo(left.WallClearanceMm));
            return result;
        }

        private static void AddTileCenterCandidates(
            Polygon compartment,
            Polygon2D compartmentPolygon,
            Polygon? tileFootprint,
            TileGridSpec grid,
            As1670_1Rules rules,
            List<LayoutCandidate> candidates,
            HashSet<long> candidateKeys)
        {
            var bounds = compartment.EnvelopeInternal;
            var corners = new[]
            {
                new Point2D(bounds.MinX, bounds.MinY),
                new Point2D(bounds.MinX, bounds.MaxY),
                new Point2D(bounds.MaxX, bounds.MinY),
                new Point2D(bounds.MaxX, bounds.MaxY)
            };
            double minU = double.PositiveInfinity, minV = double.PositiveInfinity;
            double maxU = double.NegativeInfinity, maxV = double.NegativeInfinity;
            foreach (var corner in corners)
            {
                double dx = corner.X - grid.OriginMm.X;
                double dy = corner.Y - grid.OriginMm.Y;
                double u = dx * grid.AxisU.X + dy * grid.AxisU.Y;
                double v = dx * grid.AxisV.X + dy * grid.AxisV.Y;
                minU = Math.Min(minU, u);
                minV = Math.Min(minV, v);
                maxU = Math.Max(maxU, u);
                maxV = Math.Max(maxV, v);
            }

            int minIndexU = (int)Math.Floor(minU / grid.PitchUMm);
            int minIndexV = (int)Math.Floor(minV / grid.PitchVMm);
            int maxIndexU = (int)Math.Ceiling(maxU / grid.PitchUMm);
            int maxIndexV = (int)Math.Ceiling(maxV / grid.PitchVMm);
            for (int indexU = minIndexU; indexU <= maxIndexU; indexU++)
            {
                for (int indexV = minIndexV; indexV <= maxIndexV; indexV++)
                {
                    var point = GetTileCenter(indexU, indexV, grid);
                    if (tileFootprint == null || tileFootprint.Contains(new Point(point.X, point.Y)))
                        AddCandidate(compartment, candidates, point, true, rules, candidateKeys);
                }
            }

            var polygonBounds = compartmentPolygon.GetBounds();
            int nx = Math.Max(1, (int)Math.Ceiling(polygonBounds.Width / rules.MaxDetectorSpacingMm));
            int ny = Math.Max(1, (int)Math.Ceiling(polygonBounds.Height / rules.MaxDetectorSpacingMm));
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    AddSnappedTileCandidate(compartment, tileFootprint, grid, rules, candidates, candidateKeys,
                        new Point2D(
                            polygonBounds.MinX + (ix + 0.5) * polygonBounds.Width / nx,
                            polygonBounds.MinY + (iy + 0.5) * polygonBounds.Height / ny));
                }
            }

            double candidateStep = Math.Max(1000.0, rules.MaxWallDistanceMm);
            for (double x = polygonBounds.MinX + candidateStep * 0.5; x < polygonBounds.MaxX; x += candidateStep)
            {
                for (double y = polygonBounds.MinY + candidateStep * 0.5; y < polygonBounds.MaxY; y += candidateStep)
                    AddSnappedTileCandidate(
                        compartment, tileFootprint, grid, rules, candidates, candidateKeys, new Point2D(x, y));
            }
        }

        private static void AddSnappedTileCandidate(
            Polygon compartment,
            Polygon? tileFootprint,
            TileGridSpec grid,
            As1670_1Rules rules,
            List<LayoutCandidate> candidates,
            HashSet<long> candidateKeys,
            Point2D point)
        {
            double dx = point.X - grid.OriginMm.X;
            double dy = point.Y - grid.OriginMm.Y;
            double u = dx * grid.AxisU.X + dy * grid.AxisU.Y;
            double v = dx * grid.AxisV.X + dy * grid.AxisV.Y;
            int baseIndexU = (int)Math.Floor(u / grid.PitchUMm);
            int baseIndexV = (int)Math.Floor(v / grid.PitchVMm);
            Point2D? bestCenter = null;
            double bestDistance = double.PositiveInfinity;
            for (int indexU = baseIndexU - 1; indexU <= baseIndexU + 1; indexU++)
            {
                for (int indexV = baseIndexV - 1; indexV <= baseIndexV + 1; indexV++)
                {
                    var center = GetTileCenter(indexU, indexV, grid);
                    if (tileFootprint != null && !tileFootprint.Contains(new Point(center.X, center.Y)))
                        continue;

                    double distance = center.DistanceTo(point);
                    if (distance < bestDistance)
                    {
                        bestCenter = center;
                        bestDistance = distance;
                    }
                }
            }

            if (bestCenter != null)
                AddCandidate(compartment, candidates, bestCenter, true, rules, candidateKeys);
        }

        private static Point2D GetTileCenter(int indexU, int indexV, TileGridSpec grid)
        {
            double u = (indexU + 0.5) * grid.PitchUMm;
            double v = (indexV + 0.5) * grid.PitchVMm;
            return new Point2D(
                grid.OriginMm.X + u * grid.AxisU.X + v * grid.AxisV.X,
                grid.OriginMm.Y + u * grid.AxisU.Y + v * grid.AxisV.Y);
        }

        private static void AddGeneralCandidates(
            Polygon compartment,
            RoomFrame frame,
            As1670_1Rules rules,
            Polygon? tileFootprint,
            bool restrictToMargin,
            List<LayoutCandidate> candidates,
            HashSet<long> candidateKeys)
        {
            int nu = Math.Max(1, (int)Math.Ceiling(frame.SpanU / Math.Max(1000.0, rules.MaxWallDistanceMm)));
            int nv = Math.Max(1, (int)Math.Ceiling(frame.SpanV / Math.Max(1000.0, rules.MaxWallDistanceMm)));
            for (int iu = 0; iu < nu; iu++)
            {
                double u = ComputeGridCoordinate(frame.MinU, frame.MaxU, nu, iu, rules.MinWallClearanceMm);
                for (int iv = 0; iv < nv; iv++)
                {
                    double v = ComputeGridCoordinate(frame.MinV, frame.MaxV, nv, iv, rules.MinWallClearanceMm);
                    AddCandidate(compartment, candidates, frame.ToWorld(u, v), false, rules, candidateKeys,
                        tileFootprint, restrictToMargin);
                }
            }

            // Finer room-aligned sub-grid for concave wings, corridors, and irregular margins.
            double fineStep = Math.Max(1000.0, Math.Min(2500.0, rules.MaxWallDistanceMm * 0.5));
            int fineNu = Math.Min(24, Math.Max(1, (int)Math.Ceiling(frame.SpanU / fineStep)));
            int fineNv = Math.Min(24, Math.Max(1, (int)Math.Ceiling(frame.SpanV / fineStep)));
            for (int iu = 0; iu < fineNu; iu++)
            {
                double u = ComputeGridCoordinate(frame.MinU, frame.MaxU, fineNu, iu, rules.MinWallClearanceMm);
                for (int iv = 0; iv < fineNv; iv++)
                {
                    double v = ComputeGridCoordinate(frame.MinV, frame.MaxV, fineNv, iv, rules.MinWallClearanceMm);
                    AddCandidate(compartment, candidates, frame.ToWorld(u, v), false, rules, candidateKeys,
                        tileFootprint, restrictToMargin);
                }
            }

            var interior = compartment.InteriorPoint;
            AddCandidate(
                compartment, candidates, new Point2D(interior.X, interior.Y), false, rules, candidateKeys,
                tileFootprint, restrictToMargin);
        }

        private static void AddCandidate(
            Polygon compartment,
            List<LayoutCandidate> candidates,
            Point2D location,
            bool isTile,
            As1670_1Rules rules,
            HashSet<long> candidateKeys,
            Polygon? tileFootprint = null,
            bool restrictToMargin = false)
        {
            var point = new Point(location.X, location.Y);
            if (!compartment.Contains(point) ||
                compartment.Boundary.Distance(point) < rules.HardMinWallClearanceMm)
                return;

            bool insideTile = tileFootprint != null && tileFootprint.Contains(point);
            if (restrictToMargin && insideTile)
                return;
            if (!candidateKeys.Add(GetPointKey(location)))
                return;

            candidates.Add(new LayoutCandidate
            {
                Location = location,
                IsTile = isTile,
                WallClearanceMm = compartment.Boundary.Distance(point)
            });
        }

        private static long GetPointKey(Point2D point)
        {
            long x = (long)Math.Round(point.X);
            long y = (long)Math.Round(point.Y);
            return PackCoordinates(x, y);
        }

        private static long GetGridKey(Point2D point, double cellSize) =>
            GetGridKey((long)Math.Floor(point.X / cellSize), (long)Math.Floor(point.Y / cellSize));

        private static long GetGridKey(long x, long y) => PackCoordinates(x, y);

        private static long PackCoordinates(long x, long y) =>
            (x << 32) ^ (y & 0xFFFFFFFFL);

        private static CoverageSelection SelectCoverageWithWallOffsetPreference(
            Polygon compartment,
            RoomFrame frame,
            IReadOnlyList<Point2D> targets,
            IReadOnlyList<LayoutCandidate> candidates,
            As1670_1Rules rules)
        {
            var preferredCandidates = candidates
                .Where(c => c.WallClearanceMm >= rules.MinWallClearanceMm - 1e-6)
                .ToList();

            if (preferredCandidates.Count > 0)
            {
                var preferredSelection = SelectCoverageCandidates(compartment, frame, targets, preferredCandidates, rules);
                if (preferredSelection.IsComplete)
                    return preferredSelection;
            }

            return SelectCoverageCandidates(compartment, frame, targets, candidates, rules);
        }

        private static CoverageSelection SelectCoverageCandidates(
            Polygon compartment,
            RoomFrame frame,
            IReadOnlyList<Point2D> targets,
            IReadOnlyList<LayoutCandidate> candidates,
            As1670_1Rules rules)
        {
            var result = new CoverageSelection();
            var remaining = new HashSet<int>(Enumerable.Range(0, targets.Count));
            var visibility = new List<int[]>(candidates.Count);
            double cellSize = Math.Max(1.0, rules.MaxWallDistanceMm * 1.5);
            var targetCells = new Dictionary<long, List<int>>();
            for (int i = 0; i < targets.Count; i++)
            {
                long key = GetGridKey(targets[i], cellSize);
                if (!targetCells.TryGetValue(key, out var cellTargets))
                {
                    cellTargets = new List<int>();
                    targetCells.Add(key, cellTargets);
                }

                cellTargets.Add(i);
            }

            foreach (var candidate in candidates)
            {
                var visibleTargets = new List<int>();
                long cellX = (long)Math.Floor(candidate.Location.X / cellSize);
                long cellY = (long)Math.Floor(candidate.Location.Y / cellSize);
                for (long x = cellX - 1; x <= cellX + 1; x++)
                {
                    for (long y = cellY - 1; y <= cellY + 1; y++)
                    {
                        if (!targetCells.TryGetValue(GetGridKey(x, y), out var cellTargets))
                            continue;

                        foreach (int i in cellTargets)
                        {
                            if (frame.AxialReach(candidate.Location, targets[i]) > rules.MaxWallDistanceMm)
                                continue;

                            if (VisibilityGeometry.IsVisible(compartment, candidate.Location, targets[i]))
                                visibleTargets.Add(i);
                        }
                    }
                }
                visibility.Add(visibleTargets.ToArray());
            }

            int bestSingle = -1;
            for (int i = 0; i < visibility.Count; i++)
            {
                if (visibility[i].Length != targets.Count)
                    continue;

                if (bestSingle < 0 ||
                    (candidates[i].IsTile && !candidates[bestSingle].IsTile) ||
                    (candidates[i].IsTile == candidates[bestSingle].IsTile &&
                     candidates[i].WallClearanceMm > candidates[bestSingle].WallClearanceMm))
                {
                    bestSingle = i;
                }
            }

            if (bestSingle >= 0)
            {
                result.Candidates.Add(candidates[bestSingle]);
                return result;
            }

            if (candidates.Count <= 250 && targets.Count <= 2000)
            {
                var visibleSets = visibility.Select(v => new HashSet<int>(v)).ToList();
                int bestI = -1, bestJ = -1;
                bool bestWithinSpacing = false;
                double bestPairDist = -1.0;
                double bestPairWallClearance = -1.0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        if (!Enumerable.Range(0, targets.Count).All(t =>
                                visibleSets[i].Contains(t) || visibleSets[j].Contains(t)))
                            continue;

                        double dist = candidates[i].Location.DistanceTo(candidates[j].Location);
                        bool withinSpacing = dist <= rules.MaxDetectorSpacingMm * 1.001;
                        double minWall = Math.Min(candidates[i].WallClearanceMm, candidates[j].WallClearanceMm);

                        bool better = bestI < 0
                            || (withinSpacing && !bestWithinSpacing)
                            || (withinSpacing == bestWithinSpacing && dist > bestPairDist + 50.0)
                            || (withinSpacing == bestWithinSpacing && Math.Abs(dist - bestPairDist) <= 50.0 && minWall > bestPairWallClearance);

                        if (better)
                        {
                            bestI = i;
                            bestJ = j;
                            bestWithinSpacing = withinSpacing;
                            bestPairDist = dist;
                            bestPairWallClearance = minWall;
                        }
                    }
                }

                if (bestI >= 0)
                {
                    result.Candidates.Add(candidates[bestI]);
                    result.Candidates.Add(candidates[bestJ]);
                    return result;
                }
            }

            var selectedIndices = new List<int>();
            while (remaining.Count > 0)
            {
                int best = -1;
                double bestScore = double.NegativeInfinity;
                int bestCoverage = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    int count = visibility[i].Count(remaining.Contains);
                    if (count == 0)
                        continue;

                    double minDistToSelected = selectedIndices.Count == 0
                        ? rules.MaxDetectorSpacingMm
                        : selectedIndices.Min(idx => candidates[idx].Location.DistanceTo(candidates[i].Location));

                    // Prefer candidates that cover many targets AND stay well-separated from already-placed detectors.
                    double separationFactor = Math.Min(1.0, minDistToSelected / Math.Max(1000.0, rules.MaxDetectorSpacingMm * 0.5));
                    double wallBonus = Math.Min(candidates[i].WallClearanceMm, rules.MaxWallDistanceMm) / Math.Max(1.0, rules.MaxWallDistanceMm);
                    double tileBonus = candidates[i].IsTile ? 0.25 : 0.0;
                    double score = count * (0.75 + 0.25 * separationFactor) + wallBonus * 0.5 + tileBonus;

                    if (score > bestScore || (Math.Abs(score - bestScore) < 1e-6 && count > bestCoverage))
                    {
                        best = i;
                        bestScore = score;
                        bestCoverage = count;
                    }
                }

                if (best < 0 || bestCoverage == 0)
                    break;

                selectedIndices.Add(best);
                foreach (int target in visibility[best])
                    remaining.Remove(target);
            }

            // Prune any redundant early picks if the remaining picks still cover 100% of targets.
            if (remaining.Count == 0 && selectedIndices.Count > 2)
            {
                for (int p = selectedIndices.Count - 1; p >= 0; p--)
                {
                    var reduced = selectedIndices.Where((_, idx) => idx != p).ToList();
                    var covered = new HashSet<int>();
                    foreach (int idx in reduced)
                    {
                        foreach (int t in visibility[idx])
                            covered.Add(t);
                    }

                    if (covered.Count == targets.Count &&
                        MeetsMaxNearestNeighborSpacing(reduced.Select(idx => candidates[idx]).ToList(), rules))
                    {
                        selectedIndices = reduced;
                    }
                }
            }

            foreach (int idx in selectedIndices)
                result.Candidates.Add(candidates[idx]);

            result.UncoveredTargetCount = remaining.Count;
            return result;
        }

        private static List<ProposedDevice> CoordinateWithCeiling(
            SmokeCompartment compartment,
            IReadOnlyList<LayoutCandidate> selectedCandidates,
            CompartmentCeilingAnalysis ceilingAnalysis,
            IReadOnlyList<MepObstacle> obstacles,
            RuleProfile rules)
        {
            var devices = new List<ProposedDevice>();
            var tileCeiling = ceilingAnalysis.PrimaryTileCeiling;
            bool canSnapTile = (ceilingAnalysis.Kind == CeilingKind.SuspendedTile ||
                                ceilingAnalysis.Kind == CeilingKind.MixedFinish)
                               && tileCeiling?.TileGrid != null
                               && tileCeiling.TileGrid.PitchUMm > 0
                               && tileCeiling.TileGrid.PitchVMm > 0;

            foreach (var selected in selectedCandidates)
            {
                Point2D target = selected.Location;
                bool snappedToTile = selected.IsTile;
                string? hostCeilingId = ceilingAnalysis.PrimaryCeiling?.ElementId;
                CeilingKind hostKind = ceilingAnalysis.Kind;
                string note = string.Empty;

                if (selected.IsTile && canSnapTile && tileCeiling?.TileGrid != null)
                {
                    hostCeilingId = tileCeiling.ElementId;
                    hostKind = CeilingKind.SuspendedTile;
                    note = tileCeiling.TileGrid.IsOriginVerified
                        ? "Selected full-tile centre."
                        : "Selected pattern-grid centre (origin unverified - confirm).";
                    if (!tileCeiling.TileGrid.IsOriginVerified)
                    {
                        compartment.Status = CompartmentStatus.Amber;
                    }
                }
                else if (ceilingAnalysis.Kind == CeilingKind.MixedFinish && !selected.IsTile)
                {
                    hostKind = CeilingKind.Plasterboard;
                    note = "Placed on plasterboard margin to maintain full-compartment visibility reach.";
                    compartment.Status = CompartmentStatus.Amber;
                }
                else if (ceilingAnalysis.Kind == CeilingKind.Plasterboard)
                {
                    note = "Aligned to plasterboard room symmetry.";
                }
                else if (ceilingAnalysis.Kind == CeilingKind.ExposedOrNone)
                {
                    hostCeilingId = ceilingAnalysis.PrimarySoffit?.ElementId;
                    hostKind = CeilingKind.ExposedOrNone;
                    note = ceilingAnalysis.PrimarySoffit != null
                        ? "Placed on exposed soffit (no ceiling) - confirm mounting."
                        : "Placed at fallback elevation (no ceiling/soffit) - confirm mounting.";
                }

                // Flag MEP clearance conflicts (diffusers / luminaires / sprinklers).
                bool hasObstacleConflict = CheckObstacleConflict(target, obstacles, rules.MepClearances, out string obstacleNote);
                if (hasObstacleConflict)
                {
                    compartment.Status = CompartmentStatus.Amber;
                    note = string.IsNullOrEmpty(note) ? obstacleNote : $"{note} {obstacleNote}";
                }

                devices.Add(new ProposedDevice
                {
                    CompartmentId = compartment.CompartmentId,
                    Role = DeviceRole.SmokeDetector,
                    Location = target,
                    ElevationMm = compartment.CeilingElevationMm,
                    HostCeilingElementId = hostCeilingId,
                    HostCeilingKind = hostKind,
                    WasSnappedToTile = snappedToTile,
                    WasNudgedForObstacle = hasObstacleConflict,
                    Notes = note
                });
            }

            return devices;
        }

        private static bool CheckObstacleConflict(
            Point2D pt,
            IReadOnlyList<MepObstacle> obstacles,
            MepClearanceRules clearances,
            out string note)
        {
            foreach (var obs in obstacles)
            {
                double minClearance = obs.Category switch
                {
                    MepObstacleCategory.SupplyAirDiffuser => clearances.SupplyDiffuserClearanceMm,
                    MepObstacleCategory.ReturnAirGrille => clearances.ReturnGrilleClearanceMm,
                    MepObstacleCategory.LightingFixture => clearances.LuminaireClearanceMm,
                    MepObstacleCategory.Sprinkler => clearances.SprinklerClearanceMm,
                    _ => 300.0
                };

                if (pt.DistanceTo(obs.Center) < minClearance)
                {
                    note = $"Within {minClearance:F0} mm of {obs.Category} ({obs.TypeName}).";
                    return true;
                }
            }

            note = string.Empty;
            return false;
        }

        private static void VerifyAndGradeCompartment(
            SmokeCompartment compartment,
            Polygon compNts,
            Polygon visibilityPoly,
            RoomFrame roomFrame,
            IReadOnlyList<Point2D> targets,
            List<ProposedDevice> devices,
            As1670_1Rules rules)
        {
            var detectorPts = devices
                .Where(d => d.Role == DeviceRole.SmokeDetector)
                .Select(d => d.Location)
                .ToList();

            if (detectorPts.Count == 0)
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes.Add("No valid detector positions found.");
                return;
            }

            double worstWallDist = MaxVisibleAxialReach(visibilityPoly, roomFrame, targets, detectorPts);
            compartment.WorstCaseWallDistanceMm = Math.Round(worstWallDist, 0);

            double maxSpacing = 0.0;
            for (int i = 0; i < detectorPts.Count; i++)
            {
                double nearest = double.PositiveInfinity;
                for (int j = 0; j < detectorPts.Count; j++)
                {
                    if (i == j) continue;
                    double d = detectorPts[i].DistanceTo(detectorPts[j]);
                    if (d < nearest) nearest = d;
                }
                if (nearest < double.PositiveInfinity && nearest > maxSpacing)
                    maxSpacing = nearest;
            }
            compartment.MaxDeviceSpacingMm = Math.Round(maxSpacing, 0);

            if (targets.Any(target => detectorPts.All(detector =>
                    roomFrame.AxialReach(detector, target) > rules.MaxWallDistanceMm ||
                    !VisibilityGeometry.IsVisible(visibilityPoly, detector, target))))
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes.Add("Post-coordination coverage failed; one or more sampled points are beyond X/Y wall reach or occluded.");
            }

            // Check min wall clearance for every detector.
            foreach (var pt in detectorPts)
            {
                double wallDist = compNts.Boundary.Distance(new Point(pt.X, pt.Y));
                if (wallDist < rules.HardMinWallClearanceMm)
                {
                    compartment.Status = CompartmentStatus.Red;
                    compartment.IsApprovedForPlacement = false;
                    compartment.Notes.Add($"Detector is {wallDist:F0} mm from wall (below hard min {rules.HardMinWallClearanceMm:F0} mm).");
                }
                else if (wallDist < rules.MinWallClearanceMm && compartment.Status == CompartmentStatus.Green)
                {
                    compartment.Status = CompartmentStatus.Amber;
                    compartment.Notes.Add($"Detector is {wallDist:F0} mm from wall (below preferred {rules.MinWallClearanceMm:F0} mm).");
                }
            }

            if (worstWallDist > rules.MaxWallDistanceMm * 1.02)
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes.Add($"Worst-case X/Y wall reach {worstWallDist:F0} mm exceeds limit {rules.MaxWallDistanceMm:F0} mm.");
            }

            if (maxSpacing > rules.MaxDetectorSpacingMm * 1.02)
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes.Add($"Nearest-neighbour detector spacing {maxSpacing:F0} mm exceeds limit {rules.MaxDetectorSpacingMm:F0} mm.");
            }
        }

        private static double MaxVisibleAxialReach(
            Polygon compNts,
            RoomFrame roomFrame,
            IReadOnlyList<Point2D> targets,
            IReadOnlyList<Point2D> detectors)
        {
            double worst = 0.0;
            foreach (var target in targets)
            {
                double nearest = detectors
                    .Where(detector => VisibilityGeometry.IsVisible(compNts, detector, target))
                    .Select(detector => roomFrame.AxialReach(detector, target))
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();
                if (nearest > worst) worst = nearest;
            }
            return worst;
        }

        private static void BuildSummary(PlacementPlan plan)
        {
            plan.Summary = new PlacementSummary
            {
                TotalCompartments = plan.Compartments.Count,
                GreenCount = plan.Compartments.Count(c => c.Status == CompartmentStatus.Green),
                AmberCount = plan.Compartments.Count(c => c.Status == CompartmentStatus.Amber),
                RedCount = plan.Compartments.Count(c => c.Status == CompartmentStatus.Red),
                SkippedCount = plan.Compartments.Count(c => c.Status == CompartmentStatus.Skipped),
                TotalSmokeDetectors = plan.Devices.Count(d => d.Role == DeviceRole.SmokeDetector),
                TotalEwisSpeakers = plan.Devices.Count(d => d.Role == DeviceRole.EwisSpeaker),
                UnresolvedOpenEdgeCount = plan.UnresolvedOpenEdges.Count
            };
        }
    }
}
