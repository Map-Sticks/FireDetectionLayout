using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace FireDetectionLayout.Core.Solver
{
    internal sealed class DiscoveredCompartment
    {
        public Polygon2D Boundary { get; set; } = new Polygon2D();
        public Polygon Geometry { get; set; } = null!;
        public string SourceKind { get; set; } = "Walls";
        public RoomMetadata? SourceRoom { get; set; }
        public string? DiscoveryNote { get; set; }
    }

    /// <summary>
    /// Stage 1: finds every space that needs a layout. Physical wall loops come first (closed
    /// rooms and wall-bounded open-plan areas), then Rooms, MEP Spaces and ceilings recover
    /// anything the wall graph could not close.
    /// </summary>
    internal static class CompartmentDiscovery
    {
        // Narrower than this, a leftover polygon is a wall-thickness sliver rather than a space.
        private const double MinHydraulicWidthMm = 350.0;

        // Stand-in thickness for walls modelled with none (curtain walls, storefronts).
        private const double NominalThinWallThicknessMm = 100.0;

        public static List<DiscoveredCompartment> Discover(
            WallPolygonizationResult wallResult,
            SceneSnapshot snapshot,
            As1670_1Rules rules,
            List<string> warnings)
        {
            var discovered = new List<DiscoveredCompartment>();

            // Area already claimed by a compartment, kept at wall centrelines so that a Room's
            // finish-face footprint never leaves a half-wall-thickness sliver behind.
            var claimed = new List<NtsGeometry>();
            var wallBands = BuildWallBands(wallResult.ActiveWalls, 0.0);

            // Inside a Room / Space / ceiling footprint even a curtain wall modelled with no
            // thickness has to be cut out, or a device could be set out on top of it.
            var interiorWallBands = BuildWallBands(wallResult.ActiveWalls, NominalThinWallThicknessMm);

            foreach (var wallPoly in wallResult.Compartments)
            {
                var centreline = WallGraphPolygonizer.TryToValidNtsPolygon(wallPoly);
                if (centreline == null || centreline.Area / 1_000_000.0 < rules.MinCompartmentAreaSqM)
                    continue;

                claimed.Add(centreline);
                foreach (var finish in TrimToWallFaces(centreline, wallBands, rules.MinCompartmentAreaSqM))
                {
                    discovered.Add(new DiscoveredCompartment
                    {
                        Boundary = WallGraphPolygonizer.ToPolygon2D(finish),
                        Geometry = finish,
                        SourceKind = "Walls"
                    });
                }
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
                    if (!claimed.Any(g => g.Covers(locPt)))
                    {
                        warnings.Add(
                            $"{kindLabel} '{display}' ({spatial.AreaSqM:F1} m²) was not enclosed by walls and has no spatial boundary footprint; verify room-bounding walls/links.");
                    }
                    continue;
                }

                foreach (var piece in SubtractClaimed(spatialPoly, claimed, rules.MinCompartmentAreaSqM))
                {
                    claimed.Add(piece);

                    // Walls standing inside the footprint (the reason its loop did not close) are
                    // still walls: no device may sit in them and they block the view past them.
                    foreach (var clear in TrimToWallFaces(piece, interiorWallBands, rules.MinCompartmentAreaSqM))
                    {
                        discovered.Add(new DiscoveredCompartment
                        {
                            Boundary = WallGraphPolygonizer.ToPolygon2D(clear),
                            Geometry = clear,
                            SourceKind = kindLabel,
                            SourceRoom = spatial,
                            DiscoveryNote = $"Boundary verified from Revit {kindLabel} '{display}' (wall centerline loop was open)."
                        });
                    }
                }
            }

            // Verify every modeled Ceiling footprint so ceilinged areas without rooms or closed walls are not missed.
            foreach (var ceiling in snapshot.Ceilings)
            {
                var ceilPoly = WallGraphPolygonizer.TryToValidNtsPolygon(ceiling.Footprint);
                if (ceilPoly == null)
                    continue;

                foreach (var piece in SubtractClaimed(ceilPoly, claimed, rules.MinCompartmentAreaSqM))
                {
                    claimed.Add(piece);
                    foreach (var clear in TrimToWallFaces(piece, interiorWallBands, rules.MinCompartmentAreaSqM))
                    {
                        discovered.Add(new DiscoveredCompartment
                        {
                            Boundary = WallGraphPolygonizer.ToPolygon2D(clear),
                            Geometry = clear,
                            SourceKind = "Ceiling",
                            DiscoveryNote = $"Recovered from ceiling '{ceiling.TypeName}' ({ceiling.ElementId}); no enclosing walls or room/space found."
                        });
                    }
                }
            }

            return discovered;
        }

        public static string FormatRoomLabel(RoomMetadata room)
        {
            return string.IsNullOrWhiteSpace(room.Number)
                ? room.Name
                : $"{room.Number} {room.Name}".Trim();
        }

        /// <summary>Names the compartment after the Rooms / MEP Spaces located inside it.</summary>
        public static void AttachRoomMetadata(
            SmokeCompartment compartment,
            Polygon geometry,
            IReadOnlyList<RoomMetadata> spaces,
            IReadOnlyList<RoomMetadata> rooms,
            RoomMetadata? explicitSource)
        {
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            void Add(RoomMetadata room)
            {
                string label = FormatRoomLabel(room);
                if (!string.IsNullOrWhiteSpace(label) && seenLabels.Add(label))
                    compartment.RoomNames.Add(label);
                if (!string.IsNullOrWhiteSpace(room.ElementId) && seenIds.Add(room.ElementId))
                    compartment.RoomElementIds.Add(room.ElementId);
            }

            if (explicitSource != null)
                Add(explicitSource);

            foreach (var spatial in spaces.Concat(rooms))
            {
                if (geometry.Covers(new Point(spatial.LocationPoint.X, spatial.LocationPoint.Y)))
                    Add(spatial);
            }

            const int maxListedNames = 3;
            if (compartment.RoomNames.Count == 0)
                compartment.DisplayName = compartment.CompartmentId;
            else if (compartment.RoomNames.Count <= maxListedNames)
                compartment.DisplayName = string.Join(" / ", compartment.RoomNames);
            else
                compartment.DisplayName =
                    string.Join(" / ", compartment.RoomNames.Take(maxListedNames)) +
                    $" (+{compartment.RoomNames.Count - maxListedNames} more)";
        }

        private static STRtree<NtsGeometry>? BuildWallBands(IReadOnlyList<WallBarrier> walls, double thinWallThicknessMm)
        {
            STRtree<NtsGeometry>? tree = null;
            var factory = new GeometryFactory();
            foreach (var wall in walls)
            {
                double thickness = wall.ThicknessMm > 1.0 ? wall.ThicknessMm : thinWallThicknessMm;
                if (thickness <= 1.0 || wall.Centerline.Length < 10.0)
                    continue;

                var line = factory.CreateLineString(new[]
                {
                    new Coordinate(wall.Centerline.Start.X, wall.Centerline.Start.Y),
                    new Coordinate(wall.Centerline.End.X, wall.Centerline.End.Y)
                });
                // Square ends fill the corner where two walls meet, so room corners stay square.
                var band = line.Buffer(thickness * 0.5, 2, EndCapStyle.Square);
                if (band.IsEmpty)
                    continue;

                tree ??= new STRtree<NtsGeometry>();
                tree.Insert(band.EnvelopeInternal, band);
            }

            return tree;
        }

        /// <summary>
        /// Wall loops are built from centrelines; trimming the wall thickness leaves the finish
        /// faces, so wall clearances and wall distances are measured from the real wall surface.
        /// </summary>
        private static List<Polygon> TrimToWallFaces(
            Polygon centreline,
            STRtree<NtsGeometry>? wallBands,
            double minAreaSqM)
        {
            if (wallBands == null)
                return new List<Polygon> { centreline };

            var nearby = wallBands.Query(centreline.EnvelopeInternal);
            if (nearby.Count == 0)
                return new List<Polygon> { centreline };

            try
            {
                var bands = nearby.Count == 1 ? nearby[0] : UnaryUnionOp.Union(nearby);
                var trimmed = OverlayNGRobust.Overlay(centreline, bands, SpatialFunction.Difference);

                // Drop the duplicate and near-collinear vertices the overlay leaves behind.
                trimmed = TopologyPreservingSimplifier.Simplify(trimmed, 2.0);
                var pieces = new List<Polygon>();
                CollectSubstantialPolygons(trimmed, minAreaSqM, pieces);
                return pieces;
            }
            catch (TopologyException)
            {
                return new List<Polygon> { centreline };
            }
        }

        private static List<Polygon> SubtractClaimed(
            Polygon candidate,
            IReadOnlyList<NtsGeometry> claimed,
            double minAreaSqM)
        {
            var overlapping = new List<NtsGeometry>();
            var env = candidate.EnvelopeInternal;
            foreach (var geom in claimed)
            {
                if (geom.EnvelopeInternal.Intersects(env))
                    overlapping.Add(geom);
            }

            NtsGeometry remaining = candidate;
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
                    // If robust overlay fails, keep the candidate only when its interior point is unclaimed.
                    var interior = candidate.InteriorPoint;
                    if (overlapping.Any(g => g.Covers(interior)))
                        return new List<Polygon>();
                }
            }

            var pieces = new List<Polygon>();
            CollectSubstantialPolygons(remaining, minAreaSqM, pieces);
            return pieces;
        }

        private static void CollectSubstantialPolygons(NtsGeometry? geom, double minAreaSqM, List<Polygon> output)
        {
            if (geom == null || geom.IsEmpty)
                return;

            if (geom is Polygon poly)
            {
                if (!poly.IsValid)
                {
                    var repaired = poly.Buffer(0);
                    if (repaired is Polygon repairedPolygon)
                        poly = repairedPolygon;
                    else
                    {
                        CollectSubstantialPolygons(repaired, minAreaSqM, output);
                        return;
                    }
                }

                if (poly.IsEmpty || poly.Area / 1_000_000.0 < minAreaSqM)
                    return;

                // Hydraulic width = 2 * Area / Perimeter (for a W x H rectangle it is <= min(W, H)).
                double perimeter = poly.Length;
                double hydraulicWidth = perimeter > 1e-3 ? 2.0 * poly.Area / perimeter : 0.0;
                if (hydraulicWidth < MinHydraulicWidthMm)
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
    }
}
