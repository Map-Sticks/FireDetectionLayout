using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace FireDetectionLayout.Core.Geometry
{
    public sealed class WallPolygonizationResult
    {
        public List<Polygon2D> Compartments { get; } = new List<Polygon2D>();
        public List<Segment2D> DanglingEdges { get; } = new List<Segment2D>();

        /// <summary>Walls and downstands that reach the smoke-layer band and therefore bound smoke flow.</summary>
        public List<WallBarrier> ActiveWalls { get; } = new List<WallBarrier>();

        public int FilteredOutLowWalls { get; set; }
    }

    /// <summary>
    /// Stage 1: Builds closed physical smoke compartments from walls and ceiling-level
    /// downstands/bulkheads while ignoring low partitions and Revit room separators.
    /// </summary>
    public sealed class WallGraphPolygonizer
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        public WallPolygonizationResult BuildCompartments(
            IReadOnlyList<WallBarrier> walls,
            double levelElevationMm,
            double referenceCeilingElevationMm,
            As1670_1Rules rules,
            IReadOnlyList<CeilingSurface>? ceilings = null)
        {
            var result = new WallPolygonizationResult();
            if (walls.Count == 0)
                return result;

            double ceilingHeight = Math.Max(1000.0, referenceCeilingElevationMm - levelElevationMm);
            double smokeBandDepth = Math.Max(rules.MinBulkheadBarrierDepthMm, ceilingHeight * rules.BulkheadBarrierHeightRatio);
            double smokeBandBottomZ = referenceCeilingElevationMm - smokeBandDepth;

            var activeSegments = new List<Segment2D>();
            CeilingCoverIndex? exposedDownstandIndex = null;
            foreach (var wall in walls)
            {
                if (wall.Centerline.Length < 10.0)
                    continue;

                // Wall or downstand must extend into the upper smoke-layer band to bound smoke flow.
                bool reachesSmokeBand = wall.TopElevationMm >= smokeBandBottomZ &&
                                        wall.BaseElevationMm <= referenceCeilingElevationMm + 200.0;
                if (reachesSmokeBand)
                {
                    activeSegments.Add(wall.Centerline);
                    result.ActiveWalls.Add(wall);
                    continue;
                }

                // A deep downstand above ceiling level (beam wrap, bulkhead) is hidden by a ceiling
                // but divides the smoke layer wherever the soffit is exposed.
                bool addedExposed = false;
                if (wall.BaseElevationMm > referenceCeilingElevationMm + 200.0)
                {
                    double soffitHeight = Math.Max(1000.0, wall.TopElevationMm - levelElevationMm);
                    double barrierDepth = Math.Max(rules.MinBulkheadBarrierDepthMm, soffitHeight * rules.BulkheadBarrierHeightRatio);
                    if (wall.TopElevationMm - wall.BaseElevationMm >= barrierDepth)
                    {
                        exposedDownstandIndex ??= new CeilingCoverIndex(ceilings);
                        foreach (var exposed in exposedDownstandIndex.ExposedParts(wall))
                        {
                            activeSegments.Add(exposed);
                            result.ActiveWalls.Add(new WallBarrier
                            {
                                ElementId = wall.ElementId,
                                SourceModel = wall.SourceModel,
                                TypeName = wall.TypeName,
                                Centerline = exposed,
                                ThicknessMm = wall.ThicknessMm,
                                BaseElevationMm = wall.BaseElevationMm,
                                TopElevationMm = wall.TopElevationMm,
                                IsStructuralBeamOrBulkhead = true,
                                IsCurtainWall = wall.IsCurtainWall,
                                IsRoomBounding = wall.IsRoomBounding
                            });
                            addedExposed = true;
                        }
                    }
                }

                if (!addedExposed)
                    result.FilteredOutLowWalls++;
            }

            if (activeSegments.Count < 3)
            {
                result.DanglingEdges.AddRange(activeSegments);
                return result;
            }

            var snapped = SnapEndpoints(activeSegments, rules.WallClosureToleranceMm);
            var lines = new List<NetTopologySuite.Geometries.Geometry>(snapped.Count);
            foreach (var seg in snapped)
            {
                if (seg.Length < 1.0) continue;
                lines.Add(Factory.CreateLineString(new[]
                {
                    new Coordinate(seg.Start.X, seg.Start.Y),
                    new Coordinate(seg.End.X, seg.End.Y)
                }));
            }

            if (lines.Count == 0)
                return result;

            // UnaryUnion nodes all intersecting wall segments cleanly before polygonization.
            var noded = UnaryUnionOp.Union(lines);
            var polygonizer = new Polygonizer();
            polygonizer.Add(noded);

            foreach (var poly in polygonizer.GetPolygons().OfType<Polygon>())
            {
                double areaSqM = poly.Area / 1_000_000.0;
                if (areaSqM < rules.MinCompartmentAreaSqM)
                    continue;

                result.Compartments.Add(ToPolygon2D(poly));
            }

            foreach (var dangle in polygonizer.GetDangles().OfType<LineString>())
            {
                if (dangle.NumPoints >= 2)
                {
                    var s = dangle.StartPoint;
                    var e = dangle.EndPoint;
                    result.DanglingEdges.Add(new Segment2D(new Point2D(s.X, s.Y), new Point2D(e.X, e.Y)));
                }
            }

            return result;
        }

        /// <summary>Finds the parts of a downstand that no ceiling passes beneath.</summary>
        private sealed class CeilingCoverIndex
        {
            private readonly List<(Polygon Footprint, double UndersideMm)> _ceilings =
                new List<(Polygon Footprint, double UndersideMm)>();

            public CeilingCoverIndex(IReadOnlyList<CeilingSurface>? ceilings)
            {
                if (ceilings == null)
                    return;

                foreach (var ceiling in ceilings)
                {
                    var footprint = TryToValidNtsPolygon(ceiling.Footprint);
                    if (footprint != null)
                        _ceilings.Add((footprint, ceiling.UndersideElevationMm));
                }
            }

            private static NetTopologySuite.Geometries.Geometry? TryDifference(
                NetTopologySuite.Geometries.Geometry line, Polygon footprint)
            {
                try
                {
                    return NetTopologySuite.Operation.OverlayNG.OverlayNGRobust.Overlay(
                        line, footprint, NetTopologySuite.Operation.Overlay.SpatialFunction.Difference);
                }
                catch (TopologyException)
                {
                    return null;
                }
            }

            public IEnumerable<Segment2D> ExposedParts(WallBarrier wall)
            {
                NetTopologySuite.Geometries.Geometry line = Factory.CreateLineString(new[]
                {
                    new Coordinate(wall.Centerline.Start.X, wall.Centerline.Start.Y),
                    new Coordinate(wall.Centerline.End.X, wall.Centerline.End.Y)
                });

                foreach (var (footprint, undersideMm) in _ceilings)
                {
                    if (undersideMm > wall.BaseElevationMm + 50.0 ||
                        !footprint.EnvelopeInternal.Intersects(line.EnvelopeInternal))
                        continue;

                    // When the overlay fails, what is exposed is unknown: treat the downstand as hidden.
                    var remaining = TryDifference(line, footprint);
                    if (remaining == null || remaining.IsEmpty)
                        yield break;

                    line = remaining;
                }

                for (int i = 0; i < line.NumGeometries; i++)
                {
                    if (line.GetGeometryN(i) is LineString part && part.Length >= 300.0)
                    {
                        yield return new Segment2D(
                            new Point2D(part.StartPoint.X, part.StartPoint.Y),
                            new Point2D(part.EndPoint.X, part.EndPoint.Y));
                    }
                }
            }
        }

        private static List<Segment2D> SnapEndpoints(List<Segment2D> segments, double toleranceMm)
        {
            if (toleranceMm <= 0.0)
                return segments;

            var anchors = new List<Point2D>();
            Point2D Snap(Point2D pt)
            {
                for (int i = 0; i < anchors.Count; i++)
                {
                    if (anchors[i].DistanceTo(pt) <= toleranceMm)
                        return anchors[i];
                }
                var copy = new Point2D(pt.X, pt.Y);
                anchors.Add(copy);
                return copy;
            }

            var output = new List<Segment2D>(segments.Count);
            foreach (var seg in segments)
            {
                output.Add(new Segment2D(Snap(seg.Start), Snap(seg.End)));
            }
            return output;
        }

        public static Polygon ToNtsPolygon(Polygon2D poly)
        {
            var allRings = new List<IReadOnlyList<Point2D>>();
            if (poly.OuterRing.Count >= 3)
                allRings.Add(poly.OuterRing);
            foreach (var hole in poly.Holes)
            {
                if (hole.Count >= 3)
                    allRings.Add(hole);
            }

            if (allRings.Count == 0)
                return Factory.CreatePolygon();

            // Defensively ensure the largest-area ring is treated as the outer shell so
            // snapshots with misordered Revit CurveLoops still build valid polygons.
            int outerIdx = 0;
            double maxArea = ComputeAbsRingArea(allRings[0]);
            for (int i = 1; i < allRings.Count; i++)
            {
                double area = ComputeAbsRingArea(allRings[i]);
                if (area > maxArea)
                {
                    maxArea = area;
                    outerIdx = i;
                }
            }

            var shell = Factory.CreateLinearRing(EnsureClosedRing(allRings[outerIdx]));
            var shellPoly = Factory.CreatePolygon(shell);
            var holes = new List<LinearRing>();
            for (int i = 0; i < allRings.Count; i++)
            {
                if (i == outerIdx)
                    continue;

                var holeRing = Factory.CreateLinearRing(EnsureClosedRing(allRings[i]));
                var holePoly = Factory.CreatePolygon(holeRing);
                if (!shellPoly.IsValid || shellPoly.Covers(holePoly))
                    holes.Add(holeRing);
            }
            return Factory.CreatePolygon(shell, holes.ToArray());
        }

        private static double ComputeAbsRingArea(IReadOnlyList<Point2D> ring)
        {
            double sum = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(sum) * 0.5;
        }

        private static Coordinate[] EnsureClosedRing(IReadOnlyList<Point2D> pts)
        {
            var coords = new List<Coordinate>(pts.Count + 1);
            foreach (var p in pts)
                coords.Add(new Coordinate(p.X, p.Y));

            if (coords.Count > 0 && !coords[0].Equals2D(coords[coords.Count - 1]))
                coords.Add(new Coordinate(coords[0].X, coords[0].Y));

            return coords.ToArray();
        }

        public static Polygon? TryToValidNtsPolygon(Polygon2D? poly)
        {
            if (poly == null || poly.OuterRing.Count < 3)
                return null;

            var nts = ToNtsPolygon(poly);
            if (nts.IsEmpty)
                return null;

            if (nts.IsValid)
                return nts;

            try
            {
                var repaired = nts.Buffer(0);
                if (repaired is Polygon validPoly && !validPoly.IsEmpty && validPoly.IsValid)
                    return validPoly;
                if (repaired is MultiPolygon mp && mp.NumGeometries > 0)
                {
                    Polygon? largest = null;
                    for (int i = 0; i < mp.NumGeometries; i++)
                    {
                        if (mp.GetGeometryN(i) is Polygon p && !p.IsEmpty && p.IsValid &&
                            (largest == null || p.Area > largest.Area))
                        {
                            largest = p;
                        }
                    }
                    return largest;
                }
            }
            catch
            {
                // Fall through and return null for irredeemable geometry.
            }

            return null;
        }

        public static Polygon2D ToPolygon2D(Polygon poly)
        {
            var result = new Polygon2D();
            var exterior = poly.ExteriorRing.Coordinates;
            for (int i = 0; i < exterior.Length - 1; i++)
                result.OuterRing.Add(new Point2D(exterior[i].X, exterior[i].Y));

            foreach (var interior in poly.InteriorRings)
            {
                var hole = new List<Point2D>();
                var coords = interior.Coordinates;
                for (int i = 0; i < coords.Length - 1; i++)
                    hole.Add(new Point2D(coords[i].X, coords[i].Y));
                if (hole.Count >= 3)
                    result.Holes.Add(hole);
            }

            return result;
        }
    }
}
