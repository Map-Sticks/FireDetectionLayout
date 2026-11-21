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

                // Stage 2: Build a visibility-constrained coverage layout.
                var targets = BuildCoverageTargets(compNts, rules.SmokeDetection);
                var layoutCandidates = BuildLayoutCandidates(compNts, compPoly, ceilingAnalysis, rules);
                CoverageSelection selected;
                bool requiredMarginSpill = false;
                if (ceilingAnalysis.Kind == CeilingKind.MixedFinish)
                {
                    selected = SelectCoverageCandidates(
                        compNts,
                        targets,
                        layoutCandidates.Where(c => c.IsTile).ToList(),
                        rules.SmokeDetection);
                    if (!selected.IsComplete)
                    {
                        selected = SelectCoverageCandidates(compNts, targets, layoutCandidates, rules.SmokeDetection);
                        requiredMarginSpill = selected.Candidates.Any(c => !c.IsTile);
                    }
                }
                else
                {
                    selected = SelectCoverageCandidates(compNts, targets, layoutCandidates, rules.SmokeDetection);
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
                    compartment.Notes.Add($"Visibility-constrained coverage is incomplete ({selected.UncoveredTargetCount} uncovered sample point(s)).");
                }

                // Stage 3B: Coordinate selected positions with MEP clearances.
                var coordinatedDevices = CoordinateWithCeiling(
                    compartment,
                    selected.Candidates,
                    ceilingAnalysis,
                    snapshot.Obstacles,
                    rules);

                // Stage 4: Post-snap compliance verification & grading.
                VerifyAndGradeCompartment(compartment, compNts, targets, coordinatedDevices, rules.SmokeDetection);

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

        private static List<Point2D> BuildCoverageTargets(Polygon compartment, As1670_1Rules rules)
        {
            var targets = new List<Point2D>();
            var targetKeys = new HashSet<long>();
            AddRingTargets(compartment.ExteriorRing.Coordinates, targets, targetKeys);
            for (int i = 0; i < compartment.NumInteriorRings; i++)
                AddRingTargets(compartment.GetInteriorRingN(i).Coordinates, targets, targetKeys);

            var bounds = compartment.EnvelopeInternal;
            double step = Math.Max(500.0, Math.Min(rules.MaxDetectorSpacingMm * 0.5, rules.MaxWallDistanceMm));
            for (double x = bounds.MinX + step * 0.5; x < bounds.MaxX; x += step)
            {
                for (double y = bounds.MinY + step * 0.5; y < bounds.MaxY; y += step)
                {
                    var point = new Point(x, y);
                    if (compartment.Contains(point))
                        AddUniqueTarget(targets, targetKeys, new Point2D(x, y));
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

        private static List<LayoutCandidate> BuildLayoutCandidates(
            Polygon compartment,
            Polygon2D compartmentPolygon,
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
                    compartment, compartmentPolygon, rules.SmokeDetection, tileFootprint,
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
            Polygon2D compartmentPolygon,
            As1670_1Rules rules,
            Polygon? tileFootprint,
            bool restrictToMargin,
            List<LayoutCandidate> candidates,
            HashSet<long> candidateKeys)
        {
            var bounds = compartmentPolygon.GetBounds();
            int nx = Math.Max(1, (int)Math.Ceiling(bounds.Width / rules.MaxDetectorSpacingMm));
            int ny = Math.Max(1, (int)Math.Ceiling(bounds.Height / rules.MaxDetectorSpacingMm));
            double cellWidth = bounds.Width / nx;
            double cellHeight = bounds.Height / ny;
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    AddCandidate(compartment, candidates, new Point2D(
                        bounds.MinX + (ix + 0.5) * cellWidth,
                        bounds.MinY + (iy + 0.5) * cellHeight), false, rules, candidateKeys,
                        tileFootprint, restrictToMargin);
                }
            }

            double step = Math.Max(1000.0, rules.MaxWallDistanceMm);
            for (double x = bounds.MinX + step * 0.5; x < bounds.MaxX; x += step)
            {
                for (double y = bounds.MinY + step * 0.5; y < bounds.MaxY; y += step)
                {
                    AddCandidate(
                        compartment, candidates, new Point2D(x, y), false, rules, candidateKeys,
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

        private static CoverageSelection SelectCoverageCandidates(
            Polygon compartment,
            IReadOnlyList<Point2D> targets,
            IReadOnlyList<LayoutCandidate> candidates,
            As1670_1Rules rules)
        {
            var result = new CoverageSelection();
            var remaining = new HashSet<int>(Enumerable.Range(0, targets.Count));
            var visibility = new List<int[]>(candidates.Count);
            double cellSize = Math.Max(1.0, rules.MaxWallDistanceMm);
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
                            if (candidate.Location.DistanceTo(targets[i]) > rules.MaxWallDistanceMm)
                                continue;

                            if (VisibilityGeometry.IsVisible(compartment, candidate.Location, targets[i]))
                                visibleTargets.Add(i);
                        }
                    }
                }
                visibility.Add(visibleTargets.ToArray());
            }

            for (int i = 0; i < visibility.Count; i++)
            {
                if (visibility[i].Length == targets.Count)
                {
                    result.Candidates.Add(candidates[i]);
                    return result;
                }
            }

            if (candidates.Count <= 250 && targets.Count <= 2000)
            {
                var visibleSets = visibility.Select(v => new HashSet<int>(v)).ToList();
                for (int i = 0; i < candidates.Count; i++)
                {
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        if (Enumerable.Range(0, targets.Count).All(t =>
                                visibleSets[i].Contains(t) || visibleSets[j].Contains(t)))
                        {
                            result.Candidates.Add(candidates[i]);
                            result.Candidates.Add(candidates[j]);
                            return result;
                        }
                    }
                }
            }

            while (remaining.Count > 0)
            {
                int best = -1;
                int bestCoverage = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int count = visibility[i].Count(remaining.Contains);
                    if (count > bestCoverage || (count == bestCoverage && count > 0 &&
                        ((candidates[i].IsTile && best >= 0 && !candidates[best].IsTile) ||
                         (best >= 0 && candidates[i].IsTile == candidates[best].IsTile &&
                          candidates[i].WallClearanceMm > candidates[best].WallClearanceMm))))
                    {
                        best = i;
                        bestCoverage = count;
                    }
                }

                if (best < 0 || bestCoverage == 0)
                    break;

                result.Candidates.Add(candidates[best]);
                foreach (int target in visibility[best])
                    remaining.Remove(target);
            }

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

            double worstWallDist = MaxVisibleReach(compNts, targets, detectorPts);
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
                    detector.DistanceTo(target) > rules.MaxWallDistanceMm ||
                    !VisibilityGeometry.IsVisible(compNts, detector, target))))
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes.Add("Post-coordination visibility coverage failed; one or more sampled points are beyond wall reach or occluded.");
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
                compartment.Notes.Add($"Worst-case wall reach {worstWallDist:F0} mm exceeds limit {rules.MaxWallDistanceMm:F0} mm.");
            }

            if (maxSpacing > rules.MaxDetectorSpacingMm * 1.02)
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes.Add($"Nearest-neighbour detector spacing {maxSpacing:F0} mm exceeds limit {rules.MaxDetectorSpacingMm:F0} mm.");
            }
        }

        private static double MaxVisibleReach(Polygon compNts, IReadOnlyList<Point2D> targets, IReadOnlyList<Point2D> detectors)
        {
            double worst = 0.0;
            foreach (var target in targets)
            {
                double nearest = detectors
                    .Where(detector => VisibilityGeometry.IsVisible(compNts, detector, target))
                    .Select(detector => detector.DistanceTo(target))
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
