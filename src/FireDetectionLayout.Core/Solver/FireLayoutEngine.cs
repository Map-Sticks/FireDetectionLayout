using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;

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
                : snapshot.Metadata.LevelElevationMm + snapshot.Metadata.DefaultCeilingHeightMm;

            // Stage 1: Physical smoke compartments from walls & ceiling-level downstands.
            var polyResult = _polygonizer.BuildCompartments(
                snapshot.Walls,
                snapshot.Metadata.LevelElevationMm,
                refCeilingZ,
                rules.SmokeDetection);

            plan.UnresolvedOpenEdges.AddRange(polyResult.DanglingEdges);

            int compIndex = 1;
            foreach (var compPoly in polyResult.Compartments)
            {
                var compNts = WallGraphPolygonizer.ToNtsPolygon(compPoly);
                var compartment = new SmokeCompartment
                {
                    CompartmentId = $"COMP-{compIndex:D3}",
                    Boundary = compPoly,
                    AreaSqM = Math.Round(compNts.Area / 1_000_000.0, 2)
                };
                compIndex++;

                AttachRoomMetadata(compartment, compNts, snapshot.Rooms);

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
                var ceilingAnalysis = _ceilingClassifier.AnalyzeCompartment(
                    compPoly,
                    snapshot.Ceilings,
                    refCeilingZ,
                    rules.Ceilings);

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

                // Withhold automatic placement when ceiling is unrecognized/missing/sloped.
                if (ceilingAnalysis.Kind == CeilingKind.ExposedOrNone ||
                    ceilingAnalysis.Kind == CeilingKind.Sloped ||
                    ceilingAnalysis.Kind == CeilingKind.Unknown)
                {
                    compartment.Status = CompartmentStatus.Red;
                    compartment.IsApprovedForPlacement = false;
                    plan.Compartments.Add(compartment);
                    continue;
                }

                // Stage 2: Baseline AS 1670.1 spacing (walls-first).
                var baselinePoints = ComputeBaselinePoints(compPoly, compNts, rules.SmokeDetection);

                // Stage 3B: Coordinate with ceiling type (tile grid vs plasterboard) & MEP clearances.
                var coordinatedDevices = CoordinateWithCeiling(
                    compartment,
                    compNts,
                    baselinePoints,
                    ceilingAnalysis,
                    snapshot.Obstacles,
                    rules);

                // Stage 4: Post-snap compliance verification & grading.
                VerifyAndGradeCompartment(compartment, compNts, coordinatedDevices, rules.SmokeDetection);

                compartment.ProposedDetectorCount = coordinatedDevices.Count(d => d.Role == DeviceRole.SmokeDetector);
                compartment.ProposedSpeakerCount = coordinatedDevices.Count(d => d.Role == DeviceRole.EwisSpeaker);

                plan.Compartments.Add(compartment);
                plan.Devices.AddRange(coordinatedDevices);
            }

            BuildSummary(plan);
            return plan;
        }

        private static void AttachRoomMetadata(SmokeCompartment compartment, Polygon compNts, IReadOnlyList<RoomMetadata> rooms)
        {
            foreach (var room in rooms)
            {
                var pt = new Point(room.LocationPoint.X, room.LocationPoint.Y);
                if (compNts.Contains(pt))
                {
                    string label = string.IsNullOrWhiteSpace(room.Number)
                        ? room.Name
                        : $"{room.Number} {room.Name}".Trim();
                    if (!string.IsNullOrWhiteSpace(label))
                        compartment.RoomNames.Add(label);
                }
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

        private static List<Point2D> ComputeBaselinePoints(Polygon2D compPoly, Polygon compNts, As1670_1Rules rules)
        {
            var bounds = compPoly.GetBounds();
            double width = bounds.Width;
            double height = bounds.Height;

            int nx = Math.Max(1, (int)Math.Ceiling(width / rules.MaxDetectorSpacingMm));
            int ny = Math.Max(1, (int)Math.Ceiling(height / rules.MaxDetectorSpacingMm));

            // Keep single central detector if the whole compartment is within wall reach of the interior point.
            var interiorPt = compNts.InteriorPoint;
            var center = new Point2D(interiorPt.X, interiorPt.Y);
            if (nx == 1 && ny == 1 && MaxBoundaryDistance(compNts, new[] { center }) <= rules.MaxWallDistanceMm)
            {
                return new List<Point2D> { center };
            }

            double stepX = width / nx;
            double stepY = height / ny;

            var points = new List<Point2D>(nx * ny);
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    double x = bounds.MinX + (ix + 0.5) * stepX;
                    double y = bounds.MinY + (iy + 0.5) * stepY;
                    var candidate = new Point2D(x, y);

                    if (compNts.Contains(new Point(x, y)))
                    {
                        points.Add(candidate);
                    }
                }
            }

            if (points.Count == 0)
                points.Add(center);

            return points;
        }

        private static List<ProposedDevice> CoordinateWithCeiling(
            SmokeCompartment compartment,
            Polygon compNts,
            List<Point2D> baselinePoints,
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

            Polygon? tileFootprintNts = tileCeiling != null && tileCeiling.Footprint.OuterRing.Count >= 3
                ? WallGraphPolygonizer.ToNtsPolygon(tileCeiling.Footprint)
                : null;

            foreach (var rawPt in baselinePoints)
            {
                Point2D target = rawPt;
                bool snappedToTile = false;
                string? hostCeilingId = ceilingAnalysis.PrimaryCeiling?.ElementId;
                CeilingKind hostKind = ceilingAnalysis.Kind;
                string note = string.Empty;

                if (canSnapTile && tileCeiling?.TileGrid != null)
                {
                    var snapped = SnapToTileCenter(rawPt, tileCeiling.TileGrid);
                    var snappedNts = new Point(snapped.X, snapped.Y);
                    bool insideComp = compNts.Contains(snappedNts);
                    bool insideTile = tileFootprintNts == null || tileFootprintNts.Contains(snappedNts);

                    if (insideComp && insideTile)
                    {
                        target = snapped;
                        snappedToTile = true;
                        hostCeilingId = tileCeiling.ElementId;
                        hostKind = CeilingKind.SuspendedTile;
                        note = tileCeiling.TileGrid.IsOriginVerified
                            ? "Snapped to verified tile centre."
                            : "Snapped to pattern grid (origin unverified - confirm).";
                        if (!tileCeiling.TileGrid.IsOriginVerified)
                        {
                            compartment.Status = CompartmentStatus.Amber;
                        }
                    }
                    else if (ceilingAnalysis.Kind == CeilingKind.MixedFinish)
                    {
                        hostKind = CeilingKind.Plasterboard;
                        note = "Placed on plasterboard margin to maintain wall reach.";
                        compartment.Status = CompartmentStatus.Amber;
                    }
                }
                else if (ceilingAnalysis.Kind == CeilingKind.Plasterboard)
                {
                    note = "Aligned to plasterboard room symmetry.";
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

        private static Point2D SnapToTileCenter(Point2D pt, TileGridSpec grid)
        {
            double dx = pt.X - grid.OriginMm.X;
            double dy = pt.Y - grid.OriginMm.Y;

            double u = dx * grid.AxisU.X + dy * grid.AxisU.Y;
            double v = dx * grid.AxisV.X + dy * grid.AxisV.Y;

            double tileIndexU = Math.Floor(u / grid.PitchUMm) + 0.5;
            double tileIndexV = Math.Floor(v / grid.PitchVMm) + 0.5;

            double snappedU = tileIndexU * grid.PitchUMm;
            double snappedV = tileIndexV * grid.PitchVMm;

            return new Point2D(
                grid.OriginMm.X + snappedU * grid.AxisU.X + snappedV * grid.AxisV.X,
                grid.OriginMm.Y + snappedU * grid.AxisU.Y + snappedV * grid.AxisV.Y);
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

            double worstWallDist = MaxBoundaryDistance(compNts, detectorPts);
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

            // Check min wall clearance for every detector.
            var boundaryGeom = compNts.ExteriorRing;
            foreach (var pt in detectorPts)
            {
                double wallDist = boundaryGeom.Distance(new Point(pt.X, pt.Y));
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
                compartment.Notes.Add($"Worst-case wall reach {worstWallDist:F0} mm exceeds limit {rules.MaxWallDistanceMm:F0} mm.");
            }
        }

        private static double MaxBoundaryDistance(Polygon compNts, IReadOnlyList<Point2D> detectors)
        {
            double worst = 0.0;
            var coords = compNts.ExteriorRing.Coordinates;
            for (int i = 0; i < coords.Length - 1; i++)
            {
                var a = new Point2D(coords[i].X, coords[i].Y);
                var b = new Point2D(coords[i + 1].X, coords[i + 1].Y);
                double len = a.DistanceTo(b);
                int samples = Math.Max(1, (int)Math.Ceiling(len / 500.0));
                for (int s = 0; s <= samples; s++)
                {
                    double t = (double)s / samples;
                    var p = new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    double nearest = detectors.Min(d => d.DistanceTo(p));
                    if (nearest > worst) worst = nearest;
                }
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
