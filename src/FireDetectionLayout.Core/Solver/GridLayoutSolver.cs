using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

namespace FireDetectionLayout.Core.Solver
{
    internal enum NodeContent
    {
        DetectorOnly,
        SpeakerOnly,
        Pair
    }

    internal enum SchemeMode
    {
        /// <summary>No tile grid: devices sit on the symmetric room grid.</summary>
        Free,

        /// <summary>Every device sits on a tile module centre.</summary>
        TileOnly,

        /// <summary>Tile centres inside the tile field, room grid on the surrounding margin.</summary>
        Mixed
    }

    /// <summary>
    /// How the device(s) of one grid node are arranged around the node: a single centred device,
    /// or a detector and speaker set out side by side.
    /// </summary>
    internal sealed class DevicePattern
    {
        public string Name = string.Empty;
        public NodeContent Content;

        /// <summary>Unit direction along which the pair is set out.</summary>
        public Point2D Axis = new Point2D(1, 0);

        public double DetectorOffsetMm;
        public double SpeakerOffsetMm;

        // Tile lattice phase of the node: 0.5 = tile centre, 0 = joint between two tiles.
        public double PhaseU = 0.5;
        public double PhaseV = 0.5;

        public bool HasDetector => Content != NodeContent.SpeakerOnly;
        public bool HasSpeaker => Content != NodeContent.DetectorOnly;

        public double MaxOffsetMm => Content == NodeContent.Pair
            ? Math.Max(Math.Abs(DetectorOffsetMm), Math.Abs(SpeakerOffsetMm))
            : 0.0;

        /// <summary>How far the pattern reaches from its node along a given unit direction.</summary>
        public double ExtentAlong(Point2D direction) =>
            MaxOffsetMm * Math.Abs(Axis.X * direction.X + Axis.Y * direction.Y);

        public Point2D Offset(Point2D anchor, double offsetMm) =>
            new Point2D(anchor.X + Axis.X * offsetMm, anchor.Y + Axis.Y * offsetMm);
    }

    internal sealed class PlacementScheme
    {
        public string Name = string.Empty;
        public int Priority;
        public SchemeMode Mode;
        public DevicePattern FreePattern = new DevicePattern();
        public Dictionary<TileZone, DevicePattern> TilePatterns = new Dictionary<TileZone, DevicePattern>();
    }

    /// <summary>One grid node resolved to real device positions.</summary>
    internal sealed class NodePlacement
    {
        public Point2D Anchor = new Point2D();
        public Point2D? Detector;
        public Point2D? Speaker;
        public TileZone? Zone;
        public bool IsFullTile;
        public double WallClearanceMm;
        public bool MeetsWallOffset;

        /// <summary>A device of this node lands on a light fitting, diffuser or grille.</summary>
        public bool HasFixtureClash;

        public bool IsTile => Zone != null;
    }

    internal sealed class LayoutOption
    {
        public List<NodePlacement> Nodes { get; } = new List<NodePlacement>();
        public string Description = string.Empty;
        public int PatternPriority;
        public bool IsRegularGrid;
        public int GridU;
        public int GridV;

        /// <summary>Nodes added between grid lines to reach behind cores and into odd corners.</summary>
        public int InfillCount;

        /// <summary>Number of rectangular bays the space was split into (1 = one grid for the whole space).</summary>
        public int BayCount = 1;

        public int UncoveredTargetCount;
        public bool MeetsWallOffset;
        public bool MeetsSpacing = true;
        public double MinSpacingMm;
        public double MinWallClearanceMm;
        public double WorstReachMm;
        public bool UsesMarginSpill;

        public bool IsComplete => UncoveredTargetCount == 0 && Nodes.Count > 0;

        public string Signature()
        {
            var keys = new List<string>(Nodes.Count * 2);
            foreach (var node in Nodes)
            {
                if (node.Detector != null)
                    keys.Add($"D{Math.Round(node.Detector.X)}:{Math.Round(node.Detector.Y)}");
                if (node.Speaker != null)
                    keys.Add($"S{Math.Round(node.Speaker.X)}:{Math.Round(node.Speaker.Y)}");
            }

            keys.Sort(StringComparer.Ordinal);
            return string.Join("|", keys);
        }
    }

    internal sealed class SolveRequest
    {
        public CompartmentContext Ctx = null!;
        public NodeContent Content;
        public DeviceSpacing DetectorSpec = new DeviceSpacing();
        public DeviceSpacing SpeakerSpec = new DeviceSpacing();
        public double PairSeparationMm = 600.0;
        public bool UseTileGrid = true;
        public bool PreferFullTiles = true;

        public bool HasDetector => Content != NodeContent.SpeakerOnly;
        public bool HasSpeaker => Content != NodeContent.DetectorOnly;
        public int RoleCount => Content == NodeContent.Pair ? 2 : 1;

        /// <summary>Most stringent requirements across the device kinds being laid out together.</summary>
        public DeviceSpacing Governing
        {
            get
            {
                if (Content == NodeContent.Pair)
                    return DeviceSpacing.MostStringent(DetectorSpec, SpeakerSpec);
                return Content == NodeContent.SpeakerOnly ? SpeakerSpec : DetectorSpec;
            }
        }

        public SolveRequest ForContext(CompartmentContext ctx) => new SolveRequest
        {
            Ctx = ctx,
            Content = Content,
            DetectorSpec = DetectorSpec,
            SpeakerSpec = SpeakerSpec,
            PairSeparationMm = PairSeparationMm,
            UseTileGrid = UseTileGrid,
            PreferFullTiles = PreferFullTiles
        };
    }

    /// <summary>
    /// Grid-first layout solver. Every candidate layout is built from regular room-aligned grids
    /// whose nodes are resolved to tile centres (or left on the symmetric room grid for flush
    /// ceilings):
    ///   1. one grid across the whole space;
    ///   2. that grid plus a few infill nodes on its half-lines, to reach behind cores;
    ///   3. one grid per rectangular bay of an L / T / U shaped space or branching corridor.
    /// A coverage-driven fallback is used only for shapes none of these can cover.
    /// </summary>
    internal static class GridLayoutSolver
    {
        public const double ReachToleranceMm = 1.0;

        /// <summary>
        /// Each sampled ceiling patch is checked at five probes: its centre and the far end of the
        /// patch in each of the four frame directions (the wall face for a perimeter patch).
        /// </summary>
        public const int ProbeCount = 5;

        private const int MaxAxisCount = 60;
        private const int MaxGridNodes = 1500;
        private const int MaxOptions = 8;
        private const int MaxBays = 12;

        // A tile this long hosts two devices, each centred in its own half.
        private const double TwoDeviceTileLengthMm = 1190.0;

        // Wall returns shorter than this (column casings, door nibs) do not split a space into bays.
        private const double MinBayEdgeMm = 1000.0;
        private const double BayCornerToleranceMm = 120.0;

        public static List<LayoutOption> BuildOptions(SolveRequest req)
        {
            var ctx = req.Ctx;
            bool hasTiles = req.UseTileGrid && ctx.TileZones.Count > 0;
            var stages = hasTiles
                ? new[] { SchemeMode.TileOnly, SchemeMode.Mixed }
                : new[] { SchemeMode.Free };

            var options = new List<LayoutOption>();
            LayoutOption? bestIncomplete = null;
            List<List<Polygon>>? bayLayouts = null;

            foreach (var mode in stages)
            {
                var schemes = BuildSchemes(req, mode);
                foreach (var scheme in schemes)
                    options.AddRange(EnumerateGridOptions(req, scheme, allowInfill: true));

                bayLayouts ??= BuildBayLayouts(ctx);
                foreach (var bays in bayLayouts)
                {
                    for (int index = 0; index < schemes.Count; index++)
                    {
                        LayoutOption? option;
                        try
                        {
                            option = BuildBayOption(req, mode, index, bays);
                        }
                        catch (TopologyException)
                        {
                            // One awkward bay must not cost the space its other layouts.
                            option = null;
                        }

                        if (option != null)
                            options.Add(option);
                    }
                }

                if (options.Count > 0)
                    break;

                var greedy = BuildGreedyOption(req, schemes[0]);
                if (greedy.IsComplete)
                {
                    options.Add(greedy);
                    break;
                }

                if (greedy.Nodes.Count > 0 &&
                    (bestIncomplete == null || greedy.UncoveredTargetCount < bestIncomplete.UncoveredTargetCount))
                    bestIncomplete = greedy;
            }

            if (options.Count == 0)
            {
                if (bestIncomplete != null)
                    options.Add(bestIncomplete);
                return options;
            }

            var distinct = new List<LayoutOption>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in Rank(options))
            {
                if (seen.Add(option.Signature()))
                    distinct.Add(option);
                if (distinct.Count >= MaxOptions)
                    break;
            }

            return distinct;
        }

        /// <summary>
        /// Best first: compliant wall offsets, then fewest devices (infill counts double so a clean
        /// grid wins a tie), then the tidier arrangement.
        /// </summary>
        private static IEnumerable<LayoutOption> Rank(IEnumerable<LayoutOption> options) => options
            .OrderBy(o => o.MeetsWallOffset ? 0 : 1)
            .ThenBy(o => o.Nodes.Count + o.InfillCount)
            .ThenBy(o => o.UsesMarginSpill ? 1 : 0)
            .ThenBy(o => o.Nodes.Count(n => n.HasFixtureClash))
            .ThenBy(o => o.BayCount)
            .ThenBy(o => o.PatternPriority)
            .ThenByDescending(o => Math.Round(o.MinSpacingMm / 50.0))
            .ThenByDescending(o => Math.Round(o.MinWallClearanceMm / 25.0))
            .ThenBy(o => o.WorstReachMm)
            .ThenBy(o => o.GridU);

        // ------------------------------------------------------------------ patterns

        private static List<PlacementScheme> BuildSchemes(SolveRequest req, SchemeMode mode)
        {
            var freePatterns = BuildFreePatterns(req);
            var schemes = new List<PlacementScheme>();

            if (mode == SchemeMode.Free)
            {
                for (int i = 0; i < freePatterns.Count; i++)
                {
                    schemes.Add(new PlacementScheme
                    {
                        Name = freePatterns[i].Name,
                        Priority = i,
                        Mode = mode,
                        FreePattern = freePatterns[i]
                    });
                }

                return schemes;
            }

            var perZone = req.Ctx.TileZones.ToDictionary(zone => zone, zone => BuildTilePatterns(req, zone));
            int count = perZone.Values.Max(list => list.Count);
            var primaryZone = req.Ctx.TileZones[0];
            for (int i = 0; i < count; i++)
            {
                var scheme = new PlacementScheme
                {
                    Priority = i,
                    Mode = mode,
                    FreePattern = freePatterns[0]
                };
                foreach (var entry in perZone)
                    scheme.TilePatterns[entry.Key] = entry.Value[Math.Min(i, entry.Value.Count - 1)];
                scheme.Name = scheme.TilePatterns[primaryZone].Name;
                schemes.Add(scheme);
            }

            return schemes;
        }

        private static List<DevicePattern> BuildFreePatterns(SolveRequest req)
        {
            if (req.Content != NodeContent.Pair)
            {
                return new List<DevicePattern>
                {
                    new DevicePattern { Name = "centred", Content = req.Content }
                };
            }

            var frame = req.Ctx.Frame;
            double half = req.PairSeparationMm * 0.5;
            return new List<DevicePattern>
            {
                new DevicePattern
                {
                    Name = "pair along room", Content = NodeContent.Pair, Axis = frame.LongAxis,
                    DetectorOffsetMm = -half, SpeakerOffsetMm = half
                },
                new DevicePattern
                {
                    Name = "detector on grid, speaker beside", Content = NodeContent.Pair, Axis = frame.LongAxis,
                    DetectorOffsetMm = 0.0, SpeakerOffsetMm = req.PairSeparationMm
                },
                new DevicePattern
                {
                    Name = "pair across room", Content = NodeContent.Pair, Axis = frame.ShortAxis,
                    DetectorOffsetMm = -half, SpeakerOffsetMm = half
                },
                new DevicePattern
                {
                    Name = "pair along room (mirrored)", Content = NodeContent.Pair, Axis = frame.LongAxis,
                    DetectorOffsetMm = half, SpeakerOffsetMm = -half
                }
            };
        }

        /// <summary>
        /// Tile arrangements for one grid, best first. A single device takes the tile centre. A pair
        /// shares one tile when the tile is long enough for two devices (1200x600, 1200x300), and
        /// otherwise occupies neighbouring tiles (600x600, 300x300), each device on a tile centre.
        /// </summary>
        private static List<DevicePattern> BuildTilePatterns(SolveRequest req, TileZone zone)
        {
            if (req.Content != NodeContent.Pair)
            {
                return new List<DevicePattern>
                {
                    new DevicePattern { Name = "tile centre", Content = req.Content }
                };
            }

            bool uHostsTwo = zone.PitchU >= TwoDeviceTileLengthMm;
            bool vHostsTwo = zone.PitchV >= TwoDeviceTileLengthMm;
            var longAxis = req.Ctx.Frame.LongAxis;
            bool uFirst = uHostsTwo != vHostsTwo
                ? uHostsTwo
                : Math.Abs(zone.AxisU.X * longAxis.X + zone.AxisU.Y * longAxis.Y) >=
                  Math.Abs(zone.AxisV.X * longAxis.X + zone.AxisV.Y * longAxis.Y);

            var patterns = new List<DevicePattern>();
            foreach (bool alongU in uFirst ? new[] { true, false } : new[] { false, true })
            {
                double pitch = alongU ? zone.PitchU : zone.PitchV;
                double separation;
                double phase;
                string name;
                if (pitch >= TwoDeviceTileLengthMm)
                {
                    separation = pitch * 0.5;
                    phase = 0.5;
                    name = "pair in one tile";
                }
                else
                {
                    int tilesApart = Math.Max(1, (int)Math.Round(req.PairSeparationMm / pitch));
                    separation = tilesApart * pitch;
                    phase = tilesApart % 2 == 1 ? 0.0 : 0.5;
                    name = tilesApart == 1 ? "pair in adjacent tiles" : $"pair {tilesApart} tiles apart";
                }

                patterns.Add(new DevicePattern
                {
                    Name = name,
                    Content = NodeContent.Pair,
                    Axis = alongU ? zone.AxisU : zone.AxisV,
                    DetectorOffsetMm = -separation * 0.5,
                    SpeakerOffsetMm = separation * 0.5,
                    PhaseU = alongU ? phase : 0.5,
                    PhaseV = alongU ? 0.5 : phase
                });
            }

            foreach (var pattern in patterns.ToList())
            {
                patterns.Add(new DevicePattern
                {
                    Name = pattern.Name + " (mirrored)",
                    Content = NodeContent.Pair,
                    Axis = pattern.Axis,
                    DetectorOffsetMm = -pattern.DetectorOffsetMm,
                    SpeakerOffsetMm = -pattern.SpeakerOffsetMm,
                    PhaseU = pattern.PhaseU,
                    PhaseV = pattern.PhaseV
                });
            }

            return patterns;
        }

        // ------------------------------------------------------------------ node placement

        /// <summary>Resolves an ideal grid position to the nearest valid device position(s).</summary>
        public static NodePlacement? Place(SolveRequest req, PlacementScheme scheme, Point2D target)
        {
            var ctx = req.Ctx;
            if (scheme.Mode != SchemeMode.Free)
            {
                TileZone? containing = null;
                foreach (var zone in ctx.TileZones)
                {
                    if (zone.Contains(target))
                    {
                        containing = zone;
                        break;
                    }
                }

                if (containing != null)
                    return SnapToTile(req, scheme.TilePatterns[containing], containing, target);

                if (scheme.Mode == SchemeMode.TileOnly)
                {
                    NodePlacement? best = null;
                    double bestDistance = double.PositiveInfinity;
                    foreach (var zone in ctx.TileZones)
                    {
                        var snapped = SnapToTile(req, scheme.TilePatterns[zone], zone, target);
                        if (snapped == null)
                            continue;

                        double distance = snapped.Anchor.DistanceTo(target);
                        if (distance < bestDistance)
                        {
                            best = snapped;
                            bestDistance = distance;
                        }
                    }

                    return best;
                }
            }

            return PlaceFree(req, scheme.FreePattern, target, avoidTileZones: scheme.Mode == SchemeMode.Mixed);
        }

        private static NodePlacement? PlaceFree(SolveRequest req, DevicePattern pattern, Point2D anchor, bool avoidTileZones)
        {
            // A pair that does not fit a small room is closed up, then mirrored, before giving up.
            var scales = pattern.Content == NodeContent.Pair ? new[] { 1.0, 0.75, 0.5 } : new[] { 1.0 };
            var signs = pattern.Content == NodeContent.Pair ? new[] { 1.0, -1.0 } : new[] { 1.0 };

            foreach (double scale in scales)
            {
                foreach (double sign in signs)
                {
                    Point2D? detector = pattern.HasDetector
                        ? pattern.Offset(anchor, pattern.DetectorOffsetMm * scale * sign)
                        : null;
                    Point2D? speaker = pattern.HasSpeaker
                        ? pattern.Offset(anchor, pattern.SpeakerOffsetMm * scale * sign)
                        : null;

                    var node = BuildNode(req, anchor, detector, speaker, null, avoidTileZones);
                    if (node != null)
                        return node;
                }
            }

            return null;
        }

        private static NodePlacement? SnapToTile(SolveRequest req, DevicePattern pattern, TileZone zone, Point2D target)
        {
            double u = zone.GridU(target);
            double v = zone.GridV(target);
            int baseU = (int)Math.Round(u / zone.PitchU - pattern.PhaseU);
            int baseV = (int)Math.Round(v / zone.PitchV - pattern.PhaseV);

            double window = Math.Max(1200.0, 1.5 * Math.Max(zone.PitchU, zone.PitchV));
            int rangeU = Math.Min(6, (int)Math.Ceiling(window / zone.PitchU));
            int rangeV = Math.Min(6, (int)Math.Ceiling(window / zone.PitchV));

            var anchors = new List<(Point2D Anchor, double Distance)>((2 * rangeU + 1) * (2 * rangeV + 1));
            for (int iu = baseU - rangeU; iu <= baseU + rangeU; iu++)
            {
                for (int iv = baseV - rangeV; iv <= baseV + rangeV; iv++)
                {
                    var anchor = zone.FromGrid((iu + pattern.PhaseU) * zone.PitchU, (iv + pattern.PhaseV) * zone.PitchV);
                    double distance = anchor.DistanceTo(target);
                    if (distance <= window + 1.0)
                        anchors.Add((anchor, distance));
                }
            }

            // Equidistant tile centres resolve towards the middle of the room, so the nodes either
            // side of a room centreline pick mirror-image tiles and the layout stays symmetric.
            var frame = req.Ctx.Frame;
            var roomCentre = frame.ToWorld((frame.MinU + frame.MaxU) * 0.5, (frame.MinV + frame.MaxV) * 0.5);
            anchors = anchors
                .OrderBy(a => Math.Round(a.Distance))
                .ThenBy(a => a.Anchor.DistanceTo(roomCentre))
                .ToList();

            // Nearest tile that is, in order of importance: clear of the wall by the required
            // distance, not already occupied by a light or diffuser, and an uncut tile.
            NodePlacement? best = null;
            int bestRank = int.MaxValue;
            foreach (var (anchor, _) in anchors)
            {
                Point2D? detector = pattern.HasDetector ? pattern.Offset(anchor, pattern.DetectorOffsetMm) : null;
                Point2D? speaker = pattern.HasSpeaker ? pattern.Offset(anchor, pattern.SpeakerOffsetMm) : null;
                var node = BuildNode(req, anchor, detector, speaker, zone, avoidTileZones: false);
                if (node == null)
                    continue;

                int rank = (node.MeetsWallOffset ? 0 : 4) +
                           (node.HasFixtureClash ? 2 : 0) +
                           (node.IsFullTile || !req.PreferFullTiles ? 0 : 1);
                if (rank == 0)
                    return node;

                if (rank < bestRank)
                {
                    best = node;
                    bestRank = rank;
                }
            }

            return best;
        }

        private static NodePlacement? BuildNode(
            SolveRequest req,
            Point2D anchor,
            Point2D? detector,
            Point2D? speaker,
            TileZone? zone,
            bool avoidTileZones)
        {
            var ctx = req.Ctx;
            double clearance = double.PositiveInfinity;
            bool meetsOffset = true;
            bool fullTile = zone != null;
            bool fixtureClash = false;

            foreach (var (position, spec) in new[] { (detector, req.DetectorSpec), (speaker, req.SpeakerSpec) })
            {
                if (position == null)
                    continue;
                if (!ctx.Covers(position))
                    return null;

                double wall = ctx.BoundaryDistance(position);
                if (wall < spec.HardMinWallClearanceMm - 1e-6)
                    return null;

                if (zone != null)
                {
                    if (!zone.Contains(position))
                        return null;
                    if (fullTile && !IsOnFullTile(ctx, zone, position))
                        fullTile = false;
                }
                else if (avoidTileZones)
                {
                    foreach (var tileZone in ctx.TileZones)
                    {
                        if (tileZone.Contains(position))
                            return null;
                    }
                }

                if (wall < spec.MinWallClearanceMm - 1e-6)
                    meetsOffset = false;
                fixtureClash |= ctx.IsOnFixture(position);
                clearance = Math.Min(clearance, wall);
            }

            if (double.IsPositiveInfinity(clearance))
                return null;

            return new NodePlacement
            {
                Anchor = anchor,
                Detector = detector,
                Speaker = speaker,
                Zone = zone,
                IsFullTile = fullTile,
                WallClearanceMm = clearance,
                MeetsWallOffset = meetsOffset,
                HasFixtureClash = fixtureClash
            };
        }

        /// <summary>True when the tile under the point is an uncut tile inside both the ceiling and the space.</summary>
        public static bool IsOnFullTile(CompartmentContext ctx, TileZone zone, Point2D position)
        {
            const double jointToleranceMm = 5.0;
            double tileU = Math.Floor(zone.GridU(position) / zone.PitchU) * zone.PitchU;
            double tileV = Math.Floor(zone.GridV(position) / zone.PitchV) * zone.PitchV;
            var corners = new[]
            {
                zone.FromGrid(tileU + jointToleranceMm, tileV + jointToleranceMm),
                zone.FromGrid(tileU + zone.PitchU - jointToleranceMm, tileV + jointToleranceMm),
                zone.FromGrid(tileU + zone.PitchU - jointToleranceMm, tileV + zone.PitchV - jointToleranceMm),
                zone.FromGrid(tileU + jointToleranceMm, tileV + zone.PitchV - jointToleranceMm)
            };

            var ring = new Coordinate[5];
            for (int i = 0; i < 4; i++)
                ring[i] = new Coordinate(corners[i].X, corners[i].Y);
            ring[4] = ring[0];

            var tile = zone.Footprint.Factory.CreatePolygon(ring);
            return zone.Prepared.Covers(tile) && ctx.Prepared.Covers(tile);
        }

        // ------------------------------------------------------------------ regular grids

        private static List<LayoutOption> EnumerateGridOptions(SolveRequest req, PlacementScheme scheme, bool allowInfill)
        {
            var results = new List<LayoutOption>();
            var ctx = req.Ctx;
            var frame = ctx.Frame;
            var governing = req.Governing;
            if (frame.SpanU <= 1.0 || frame.SpanV <= 1.0)
                return results;

            double pitchLimit = Math.Max(1000.0, Math.Min(governing.MaxWallDistanceMm * 2.0, governing.MaxSpacingMm));
            int minNu = Math.Max(1, (int)Math.Ceiling(frame.SpanU / pitchLimit - 1e-6));
            int minNv = Math.Max(1, (int)Math.Ceiling(frame.SpanV / pitchLimit - 1e-6));
            int maxNu = Math.Min(MaxAxisCount, minNu + 1);
            int maxNv = Math.Min(MaxAxisCount, minNv + 1);
            int maxAllowedCount = Math.Max(minNu * minNv + 1, (int)Math.Ceiling(minNu * minNv * 1.5));

            // Irregular rooms can need fewer nodes than their bounding box suggests, but never
            // fewer than half per axis; skipping the rest keeps large floors fast.
            var gridSizes = new List<(int Nu, int Nv)>();
            for (int nu = Math.Max(1, minNu / 2); nu <= maxNu; nu++)
            {
                for (int nv = Math.Max(1, minNv / 2); nv <= maxNv; nv++)
                {
                    if (nu * nv <= MaxGridNodes)
                        gridSizes.Add((nu, nv));
                }
            }

            gridSizes.Sort((a, b) =>
            {
                int byCount = (a.Nu * a.Nv).CompareTo(b.Nu * b.Nv);
                return byCount != 0 ? byCount : a.Nu.CompareTo(b.Nu);
            });

            double minReasonableSpacing = Math.Min(
                governing.MaxSpacingMm * 0.25,
                Math.Max(governing.MinWallClearanceMm * 2.0, 1000.0));

            var pattern = scheme.Mode == SchemeMode.Free
                ? scheme.FreePattern
                : scheme.TilePatterns[ctx.TileZones[0]];
            double wallOffsetU = governing.MinWallClearanceMm + pattern.ExtentAlong(frame.AxisU);
            double wallOffsetV = governing.MinWallClearanceMm + pattern.ExtentAlong(frame.AxisV);

            int bestCount = int.MaxValue;
            foreach (var (nu, nv) in gridSizes)
            {
                if (bestCount != int.MaxValue && nu * nv > Math.Max(bestCount * 2, bestCount + 3))
                    break;

                // Never subdivide an axis so tightly that devices bunch together.
                if (nu > 1 && frame.SpanU / nu < minReasonableSpacing)
                    continue;
                if (nv > 1 && frame.SpanV / nv < minReasonableSpacing)
                    continue;

                var option = new LayoutOption
                {
                    IsRegularGrid = true,
                    GridU = nu,
                    GridV = nv,
                    PatternPriority = scheme.Priority,
                    Description = DescribeGrid(nu, nv, scheme, req.Content)
                };

                var uCoords = new double[nu];
                var vCoords = new double[nv];
                for (int iu = 0; iu < nu; iu++)
                    uCoords[iu] = GridCoordinate(frame.MinU, frame.MaxU, nu, iu, wallOffsetU);
                for (int iv = 0; iv < nv; iv++)
                    vCoords[iv] = GridCoordinate(frame.MinV, frame.MaxV, nv, iv, wallOffsetV);

                var keys = new HashSet<long>();
                double cellSpanU = frame.SpanU / nu;
                double cellSpanV = frame.SpanV / nv;
                for (int iu = 0; iu < nu; iu++)
                {
                    double cellMinU = frame.MinU + iu * cellSpanU;
                    for (int iv = 0; iv < nv; iv++)
                    {
                        double cellMinV = frame.MinV + iv * cellSpanV;
                        var resolved = ResolveGridPointInCell(
                            ctx, frame.ToWorld(uCoords[iu], vCoords[iv]),
                            cellMinU, cellMinU + cellSpanU, cellMinV, cellMinV + cellSpanV,
                            governing);
                        if (resolved == null)
                            continue;

                        var node = Place(req, scheme, resolved);
                        if (node != null && keys.Add(PointKey(node.Anchor)))
                            option.Nodes.Add(node);
                    }
                }

                if (option.Nodes.Count == 0 || option.Nodes.Count > maxAllowedCount)
                    continue;

                FinaliseMetrics(req, option, stopAtFirstGap: true);

                // A grid that only misses the ceiling behind a core or in an odd corner is kept
                // and topped up, rather than discarded for something less regular.
                if (!option.IsComplete && allowInfill && !ctx.IsConvex &&
                    nu >= minNu && nv >= minNv && option.Nodes.Count >= 3 &&
                    TryInfill(req, scheme, option, uCoords, vCoords, keys))
                {
                    FinaliseMetrics(req, option, stopAtFirstGap: true);
                }

                if (!option.IsComplete || !option.MeetsSpacing)
                    continue;

                results.Add(option);
                bestCount = Math.Min(bestCount, option.Nodes.Count);
            }

            return results;
        }

        private static string DescribeGrid(int nu, int nv, PlacementScheme scheme, NodeContent content)
        {
            string grid = nu * nv == 1 ? "single centred position" : $"{nu} x {nv} grid";
            return content == NodeContent.Pair || scheme.Mode != SchemeMode.Free
                ? $"{grid}, {scheme.Name}"
                : grid;
        }

        /// <summary>
        /// Equal bays with a half bay at each wall. When the half bay would breach the minimum wall
        /// distance, the end nodes are pulled in to that distance and the rest spread evenly between.
        /// </summary>
        public static double GridCoordinate(double min, double max, int count, int index, double minWallOffsetMm)
        {
            double span = max - min;
            if (count <= 1)
                return min + span * 0.5;

            double halfBay = 0.5 * (span / count);
            if (halfBay >= minWallOffsetMm || span <= 2.0 * minWallOffsetMm)
                return min + (index + 0.5) * (span / count);

            double insetSpan = span - 2.0 * minWallOffsetMm;
            return min + minWallOffsetMm + index * (insetSpan / (count - 1));
        }

        private static Point2D? ResolveGridPointInCell(
            CompartmentContext ctx,
            Point2D rawPoint,
            double cellMinU,
            double cellMaxU,
            double cellMinV,
            double cellMaxV,
            DeviceSpacing governing)
        {
            bool rawInside = ctx.Covers(rawPoint);
            double rawClearance = rawInside ? ctx.BoundaryDistance(rawPoint) : 0.0;
            if (rawInside && rawClearance >= governing.MinWallClearanceMm - 0.01)
                return rawPoint;

            // Clip the grid cell against the space so corridors with side alcoves or L-shaped
            // rooms centre the node within the part of the cell that actually exists.
            var frame = ctx.Frame;
            var c0 = frame.ToWorld(cellMinU, cellMinV);
            var c1 = frame.ToWorld(cellMaxU, cellMinV);
            var c2 = frame.ToWorld(cellMaxU, cellMaxV);
            var c3 = frame.ToWorld(cellMinU, cellMaxV);
            var cell = ctx.Geometry.Factory.CreatePolygon(new[]
            {
                new Coordinate(c0.X, c0.Y),
                new Coordinate(c1.X, c1.Y),
                new Coordinate(c2.X, c2.Y),
                new Coordinate(c3.X, c3.Y),
                new Coordinate(c0.X, c0.Y)
            });

            NetTopologySuite.Geometries.Geometry clip;
            try
            {
                clip = OverlayNGRobust.Overlay(ctx.Geometry, cell, SpatialFunction.Intersection);
            }
            catch (TopologyException)
            {
                return rawInside ? rawPoint : null;
            }

            bool RawIsUsable() => rawInside && rawClearance >= governing.HardMinWallClearanceMm;

            if (clip.IsEmpty || clip.Area < 250_000.0)
                return RawIsUsable() ? rawPoint : null;

            double localMinU = double.PositiveInfinity, localMaxU = double.NegativeInfinity;
            double localMinV = double.PositiveInfinity, localMaxV = double.NegativeInfinity;
            foreach (var coordinate in clip.Coordinates)
            {
                double cu = coordinate.X * frame.AxisU.X + coordinate.Y * frame.AxisU.Y;
                double cv = coordinate.X * frame.AxisV.X + coordinate.Y * frame.AxisV.Y;
                if (cu < localMinU) localMinU = cu;
                if (cu > localMaxU) localMaxU = cu;
                if (cv < localMinV) localMinV = cv;
                if (cv > localMaxV) localMaxV = cv;
            }

            var localMid = frame.ToWorld((localMinU + localMaxU) * 0.5, (localMinV + localMaxV) * 0.5);
            if (ctx.Covers(localMid) && ctx.BoundaryDistance(localMid) >= governing.HardMinWallClearanceMm)
                return localMid;

            if (RawIsUsable())
                return rawPoint;

            var interior = clip.InteriorPoint;
            var interiorPoint = new Point2D(interior.X, interior.Y);
            return ctx.Covers(interiorPoint) && ctx.BoundaryDistance(interiorPoint) >= governing.HardMinWallClearanceMm
                ? interiorPoint
                : null;
        }

        /// <summary>
        /// Adds nodes on the grid lines and half-lines until the remaining ceiling is covered.
        /// Gives up when more than a quarter of the grid would have to be added again.
        /// </summary>
        private static bool TryInfill(
            SolveRequest req,
            PlacementScheme scheme,
            LayoutOption option,
            double[] uCoords,
            double[] vCoords,
            HashSet<long> usedKeys)
        {
            var ctx = req.Ctx;
            var frame = ctx.Frame;
            int probeCount = ctx.Targets.Count * ProbeCount * req.RoleCount;
            var remaining = new bool[probeCount];
            for (int i = 0; i < probeCount; i++)
                remaining[i] = true;

            int remainingCount = probeCount;
            foreach (var node in option.Nodes)
                remainingCount -= MarkCovered(remaining, CoveredProbes(req, node));
            if (remainingCount == 0)
                return false;

            var candidates = new List<NodePlacement>();
            var coverage = new List<bool[]>();
            var keys = new HashSet<long>(usedKeys);
            foreach (double u in WithMidpoints(uCoords))
            {
                foreach (double v in WithMidpoints(vCoords))
                {
                    var node = Place(req, scheme, frame.ToWorld(u, v));
                    if (node == null || !keys.Add(PointKey(node.Anchor)))
                        continue;

                    var covered = CoveredProbes(req, node);
                    if (CountGain(remaining, covered) > 0)
                    {
                        candidates.Add(node);
                        coverage.Add(covered);
                    }
                }
            }

            int maxInfill = Math.Max(1, (int)Math.Ceiling(option.Nodes.Count * 0.25));
            var added = new List<NodePlacement>();
            while (remainingCount > 0 && added.Count < maxInfill)
            {
                int best = -1;
                int bestGain = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int gain = CountGain(remaining, coverage[i]);
                    if (gain > bestGain ||
                        (gain == bestGain && gain > 0 && candidates[i].WallClearanceMm > candidates[best].WallClearanceMm))
                    {
                        best = i;
                        bestGain = gain;
                    }
                }

                if (best < 0)
                    break;

                added.Add(candidates[best]);
                remainingCount -= MarkCovered(remaining, coverage[best]);
            }

            if (remainingCount > 0)
                return false;

            option.Nodes.AddRange(added);
            option.InfillCount = added.Count;
            option.Description += $" + {added.Count} infill";
            return true;
        }

        private static IEnumerable<double> WithMidpoints(double[] coordinates)
        {
            for (int i = 0; i < coordinates.Length; i++)
            {
                yield return coordinates[i];
                if (i + 1 < coordinates.Length)
                    yield return (coordinates[i] + coordinates[i + 1]) * 0.5;
            }
        }

        private static int CountGain(bool[] remaining, bool[] covered)
        {
            int gain = 0;
            for (int i = 0; i < remaining.Length; i++)
            {
                if (remaining[i] && covered[i])
                    gain++;
            }

            return gain;
        }

        private static int MarkCovered(bool[] remaining, bool[] covered)
        {
            int marked = 0;
            for (int i = 0; i < remaining.Length; i++)
            {
                if (remaining[i] && covered[i])
                {
                    remaining[i] = false;
                    marked++;
                }
            }

            return marked;
        }

        // ------------------------------------------------------------------ rectangular bays

        /// <summary>
        /// Splits an L / T / U shaped space into rectangular bays by extending the walls that meet
        /// at each internal corner. Two layouts are returned: bays cut across U and across V.
        /// </summary>
        private static List<List<Polygon>> BuildBayLayouts(CompartmentContext ctx)
        {
            var layouts = new List<List<Polygon>>();
            if (ctx.IsConvex)
                return layouts;

            foreach (bool cutAcrossU in new[] { false, true })
            {
                var cuts = CollectBayCuts(ctx, cutAcrossU);
                if (cuts.Count == 0 || cuts.Count >= MaxBays)
                    continue;

                var bays = SliceIntoBays(ctx, cuts, cutAcrossU);
                if (bays.Count >= 2 && bays.Count <= MaxBays)
                    layouts.Add(bays);
            }

            return layouts;
        }

        private static List<double> CollectBayCuts(CompartmentContext ctx, bool cutAcrossU)
        {
            var frame = ctx.Frame;
            var along = cutAcrossU ? frame.AxisV : frame.AxisU;
            var raw = new List<double>();

            void Scan(Coordinate[] ring, bool isHole)
            {
                int count = ring.Length - 1;
                if (count < 3)
                    return;

                double doubledArea = 0.0;
                for (int i = 0; i < count; i++)
                    doubledArea += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
                double orientation = doubledArea >= 0 ? 1.0 : -1.0;

                for (int i = 0; i < count; i++)
                {
                    var previous = ring[(i - 1 + count) % count];
                    var current = ring[i];
                    var next = ring[(i + 1) % count];
                    double ax = current.X - previous.X, ay = current.Y - previous.Y;
                    double bx = next.X - current.X, by = next.Y - current.Y;
                    double lengthA = Math.Sqrt(ax * ax + ay * ay);
                    double lengthB = Math.Sqrt(bx * bx + by * by);
                    if (lengthA < MinBayEdgeMm || lengthB < MinBayEdgeMm)
                        continue;

                    // An internal corner of the space: reflex on the shell, convex on a core.
                    double turn = (ax * by - ay * bx) * orientation;
                    if (isHole ? turn <= 0 : turn >= 0)
                        continue;

                    // The cut continues the wall that runs along the cut direction.
                    bool aAlong = Math.Abs(ax * along.X + ay * along.Y) >= 0.985 * lengthA;
                    bool bAlong = Math.Abs(bx * along.X + by * along.Y) >= 0.985 * lengthB;
                    if (!aAlong && !bAlong)
                        continue;

                    var corner = new Point2D(current.X, current.Y);
                    raw.Add(cutAcrossU ? frame.U(corner) : frame.V(corner));
                }
            }

            // Corners are read from a simplified outline so column casings, wall jogs and
            // chamfers of a real model neither hide a corner nor create one.
            var outline = NetTopologySuite.Simplify.TopologyPreservingSimplifier.Simplify(ctx.Visibility, BayCornerToleranceMm)
                as Polygon ?? ctx.Visibility;
            Scan(outline.ExteriorRing.Coordinates, isHole: false);
            for (int i = 0; i < outline.NumInteriorRings; i++)
                Scan(outline.GetInteriorRingN(i).Coordinates, isHole: true);

            double min = cutAcrossU ? frame.MinU : frame.MinV;
            double max = cutAcrossU ? frame.MaxU : frame.MaxV;
            raw.Sort();
            var cuts = new List<double>();
            foreach (double value in raw)
            {
                if (value < min + 300.0 || value > max - 300.0)
                    continue;
                if (cuts.Count == 0 || value - cuts[cuts.Count - 1] > 50.0)
                    cuts.Add(value);
            }

            return cuts;
        }

        private static List<Polygon> SliceIntoBays(CompartmentContext ctx, List<double> cuts, bool cutAcrossU)
        {
            var frame = ctx.Frame;
            var bounds = new List<double> { (cutAcrossU ? frame.MinU : frame.MinV) - 10.0 };
            bounds.AddRange(cuts);
            bounds.Add((cutAcrossU ? frame.MaxU : frame.MaxV) + 10.0);

            double otherMin = (cutAcrossU ? frame.MinV : frame.MinU) - 10.0;
            double otherMax = (cutAcrossU ? frame.MaxV : frame.MaxU) + 10.0;
            var bays = new List<Polygon>();
            for (int i = 0; i + 1 < bounds.Count; i++)
            {
                var corners = cutAcrossU
                    ? new[]
                    {
                        frame.ToWorld(bounds[i], otherMin), frame.ToWorld(bounds[i + 1], otherMin),
                        frame.ToWorld(bounds[i + 1], otherMax), frame.ToWorld(bounds[i], otherMax)
                    }
                    : new[]
                    {
                        frame.ToWorld(otherMin, bounds[i]), frame.ToWorld(otherMax, bounds[i]),
                        frame.ToWorld(otherMax, bounds[i + 1]), frame.ToWorld(otherMin, bounds[i + 1])
                    };
                var ring = new Coordinate[5];
                for (int c = 0; c < 4; c++)
                    ring[c] = new Coordinate(corners[c].X, corners[c].Y);
                ring[4] = ring[0];

                NetTopologySuite.Geometries.Geometry slice;
                try
                {
                    slice = OverlayNGRobust.Overlay(
                        ctx.Geometry, ctx.Geometry.Factory.CreatePolygon(ring), SpatialFunction.Intersection);
                }
                catch (TopologyException)
                {
                    return new List<Polygon>();
                }

                for (int g = 0; g < slice.NumGeometries; g++)
                {
                    // Slivers are left to their neighbours; the whole-space check decides.
                    if (slice.GetGeometryN(g) is Polygon bay && bay.IsValid && bay.Area >= 1_000_000.0)
                        bays.Add(bay);
                }
            }

            return bays;
        }

        /// <summary>Gives every bay its own regular grid and checks the result against the whole space.</summary>
        private static LayoutOption? BuildBayOption(SolveRequest req, SchemeMode mode, int schemeIndex, List<Polygon> bays)
        {
            var option = new LayoutOption
            {
                IsRegularGrid = true,
                BayCount = bays.Count,
                PatternPriority = schemeIndex
            };
            var keys = new HashSet<long>();
            string patternName = string.Empty;

            foreach (var bay in bays)
            {
                var bayContext = CreateBayContext(req.Ctx, bay);
                var bayRequest = req.ForContext(bayContext);
                var schemes = BuildSchemes(bayRequest, mode);
                var scheme = schemes[Math.Min(schemeIndex, schemes.Count - 1)];
                patternName = scheme.Name;

                var best = Rank(EnumerateGridOptions(bayRequest, scheme, allowInfill: false)).FirstOrDefault();
                if (best == null)
                    return null;

                foreach (var node in best.Nodes)
                {
                    // Re-measure against the real walls: inside a bay the cut lines stood in for walls.
                    var placed = BuildNode(req, node.Anchor, node.Detector, node.Speaker, node.Zone, avoidTileZones: false);
                    if (placed != null && keys.Add(PointKey(placed.Anchor)))
                        option.Nodes.Add(placed);
                }
            }

            if (option.Nodes.Count == 0)
                return null;

            FinaliseMetrics(req, option, stopAtFirstGap: true);
            if (!option.IsComplete || !option.MeetsSpacing)
                return null;

            option.Description = req.Content == NodeContent.Pair || mode != SchemeMode.Free
                ? $"{bays.Count} bays, {option.Nodes.Count} positions, {patternName}"
                : $"{bays.Count} bays, {option.Nodes.Count} positions";
            return option;
        }

        private static CompartmentContext CreateBayContext(CompartmentContext parent, Polygon bay)
        {
            var compartment = new SmokeCompartment
            {
                CompartmentId = parent.Compartment.CompartmentId,
                DisplayName = parent.Compartment.DisplayName,
                Boundary = WallGraphPolygonizer.ToPolygon2D(bay),
                CeilingKind = parent.Compartment.CeilingKind,
                CeilingElevationMm = parent.Compartment.CeilingElevationMm
            };
            var context = new CompartmentContext(compartment, bay)
            {
                Ceiling = parent.Ceiling,
                Frame = RoomFrame.FromAxis(compartment.Boundary, parent.Frame.AxisU),
                TargetStepMm = parent.TargetStepMm,
                TargetEdgeInsetMm = parent.TargetEdgeInsetMm
            };
            context.CeilingZones.AddRange(parent.CeilingZones);
            context.TileZones.AddRange(parent.TileZones);
            context.FixtureBoxes.AddRange(parent.FixtureBoxes);
            context.Targets = CoverageTargets.Build(context, context.TargetStepMm, context.TargetEdgeInsetMm);
            return context;
        }

        // ------------------------------------------------------------------ coverage

        /// <summary>
        /// Wall reach from a device to each probe of a patch, measured along the room X and Y axes.
        /// </summary>
        private static void ProbeReaches(double deviceU, double deviceV, CoverageTarget target, double[] reaches)
        {
            double du = Math.Abs(deviceU - target.U);
            double dv = Math.Abs(deviceV - target.V);
            reaches[0] = Math.Max(du, dv);
            reaches[1] = Math.Max(Math.Abs(deviceU - target.U - target.ExtentUPlus), dv);
            reaches[2] = Math.Max(Math.Abs(deviceU - target.U + target.ExtentUMinus), dv);
            reaches[3] = Math.Max(du, Math.Abs(deviceV - target.V - target.ExtentVPlus));
            reaches[4] = Math.Max(du, Math.Abs(deviceV - target.V + target.ExtentVMinus));
        }

        private static Point2D ProbePoint(RoomFrame frame, CoverageTarget target, int probe)
        {
            switch (probe)
            {
                case 1: return frame.ToWorld(target.U + target.ExtentUPlus, target.V);
                case 2: return frame.ToWorld(target.U - target.ExtentUMinus, target.V);
                case 3: return frame.ToWorld(target.U, target.V + target.ExtentVPlus);
                case 4: return frame.ToWorld(target.U, target.V - target.ExtentVMinus);
                default: return target.Point;
            }
        }

        /// <summary>
        /// A device that sees the centre of a patch is taken to see all of it; otherwise each
        /// probe is tested on its own, so a device can still cover the near end of a patch that
        /// straddles a corner.
        /// </summary>
        private static bool SeesProbe(
            CompartmentContext ctx, Point2D device, CoverageTarget target, int probe, ref bool? centreVisible)
        {
            centreVisible ??= ctx.IsVisible(device, target.Point);
            return centreVisible.Value || (probe > 0 && ctx.IsVisible(device, ProbePoint(ctx.Frame, target, probe)));
        }

        /// <summary>
        /// Counts sampled ceiling patches that are not entirely within wall reach of devices that
        /// can see them. Different devices may cover different parts of one patch.
        /// </summary>
        public static int CountUncovered(
            CompartmentContext ctx,
            IReadOnlyList<Point2D> devices,
            DeviceSpacing spec,
            out double worstReachMm,
            bool stopAtFirstGap = false)
        {
            worstReachMm = 0.0;
            var frame = ctx.Frame;
            var deviceU = new double[devices.Count];
            var deviceV = new double[devices.Count];
            for (int i = 0; i < devices.Count; i++)
            {
                deviceU[i] = frame.U(devices[i]);
                deviceV[i] = frame.V(devices[i]);
            }

            double limit = spec.MaxWallDistanceMm + ReachToleranceMm;
            var reaches = new double[ProbeCount];
            var best = new double[ProbeCount];
            int uncovered = 0;
            foreach (var target in ctx.Targets)
            {
                for (int p = 0; p < ProbeCount; p++)
                    best[p] = double.PositiveInfinity;

                for (int i = 0; i < devices.Count; i++)
                {
                    ProbeReaches(deviceU[i], deviceV[i], target, reaches);
                    bool? centreVisible = null;
                    for (int p = 0; p < ProbeCount; p++)
                    {
                        if (reaches[p] <= limit && reaches[p] < best[p] &&
                            SeesProbe(ctx, devices[i], target, p, ref centreVisible))
                            best[p] = reaches[p];
                    }
                }

                double patchWorst = 0.0;
                for (int p = 0; p < ProbeCount; p++)
                    patchWorst = Math.Max(patchWorst, best[p]);

                if (double.IsPositiveInfinity(patchWorst))
                {
                    uncovered++;
                    if (stopAtFirstGap)
                        return uncovered;
                }
                else if (patchWorst > worstReachMm)
                {
                    worstReachMm = patchWorst;
                }
            }

            return uncovered;
        }

        /// <summary>Largest distance from any device to its nearest neighbour.</summary>
        public static double MaxNearestNeighbourSpacing(IReadOnlyList<Point2D> devices)
        {
            if (devices.Count < 2)
                return 0.0;

            double worst = 0.0;
            for (int i = 0; i < devices.Count; i++)
            {
                double nearest = double.PositiveInfinity;
                for (int j = 0; j < devices.Count; j++)
                {
                    if (i == j) continue;
                    double distance = devices[i].DistanceTo(devices[j]);
                    if (distance < nearest) nearest = distance;
                }

                if (nearest > worst) worst = nearest;
            }

            return worst;
        }

        private static void FinaliseMetrics(SolveRequest req, LayoutOption option, bool stopAtFirstGap)
        {
            var ctx = req.Ctx;
            option.MeetsWallOffset = option.Nodes.All(n => n.MeetsWallOffset);
            option.MinWallClearanceMm = option.Nodes.Count > 0 ? option.Nodes.Min(n => n.WallClearanceMm) : 0.0;
            option.UsesMarginSpill = ctx.TileZones.Count > 0 && req.UseTileGrid && option.Nodes.Any(n => !n.IsTile);

            double minSpacing = double.PositiveInfinity;
            for (int i = 0; i < option.Nodes.Count; i++)
            {
                for (int j = i + 1; j < option.Nodes.Count; j++)
                    minSpacing = Math.Min(minSpacing, option.Nodes[i].Anchor.DistanceTo(option.Nodes[j].Anchor));
            }

            option.MinSpacingMm = double.IsPositiveInfinity(minSpacing) ? 1e9 : minSpacing;
            option.UncoveredTargetCount = 0;
            option.WorstReachMm = 0.0;
            option.MeetsSpacing = true;

            foreach (var (positions, spec) in RolePositions(req, option.Nodes))
            {
                int uncovered = CountUncovered(ctx, positions, spec, out double worst, stopAtFirstGap);
                option.UncoveredTargetCount = Math.Max(option.UncoveredTargetCount, uncovered);
                option.WorstReachMm = Math.Max(option.WorstReachMm, worst);
                if (uncovered > 0 && stopAtFirstGap)
                    return;

                if (MaxNearestNeighbourSpacing(positions) > spec.MaxSpacingMm + ReachToleranceMm)
                    option.MeetsSpacing = false;
            }
        }

        private static IEnumerable<(List<Point2D> Positions, DeviceSpacing Spec)> RolePositions(
            SolveRequest req,
            IReadOnlyList<NodePlacement> nodes)
        {
            if (req.HasDetector)
                yield return (nodes.Where(n => n.Detector != null).Select(n => n.Detector!).ToList(), req.DetectorSpec);
            if (req.HasSpeaker)
                yield return (nodes.Where(n => n.Speaker != null).Select(n => n.Speaker!).ToList(), req.SpeakerSpec);
        }

        /// <summary>
        /// Patch probes a node covers, one block of flags per device kind in the request
        /// (detector block first), so each kind's coverage is tracked on its own.
        /// </summary>
        private static bool[] CoveredProbes(SolveRequest req, NodePlacement node)
        {
            var ctx = req.Ctx;
            var frame = ctx.Frame;
            int blockSize = ctx.Targets.Count * ProbeCount;
            var covered = new bool[blockSize * req.RoleCount];
            var reaches = new double[ProbeCount];

            int block = 0;
            foreach (var (wanted, position, spec) in new[]
                     {
                         (req.HasDetector, node.Detector, req.DetectorSpec),
                         (req.HasSpeaker, node.Speaker, req.SpeakerSpec)
                     })
            {
                if (!wanted)
                    continue;

                int offset = block++ * blockSize;
                if (position == null)
                    continue;

                double u = frame.U(position);
                double v = frame.V(position);
                double limit = spec.MaxWallDistanceMm + ReachToleranceMm;
                for (int t = 0; t < ctx.Targets.Count; t++)
                {
                    var target = ctx.Targets[t];
                    ProbeReaches(u, v, target, reaches);
                    bool? centreVisible = null;
                    for (int p = 0; p < ProbeCount; p++)
                    {
                        covered[offset + t * ProbeCount + p] =
                            reaches[p] <= limit && SeesProbe(ctx, position, target, p, ref centreVisible);
                    }
                }
            }

            return covered;
        }

        // ------------------------------------------------------------------ irregular fallback

        private static LayoutOption BuildGreedyOption(SolveRequest req, PlacementScheme scheme)
        {
            var candidates = BuildGreedyCandidates(req, scheme);
            var coverage = candidates.Select(c => CoveredProbes(req, c)).ToList();

            var compliant = Enumerable.Range(0, candidates.Count).Where(i => candidates[i].MeetsWallOffset).ToList();
            var selection = new List<int>();
            bool complete = false;
            if (compliant.Count > 0)
                selection = SelectCover(req, candidates, coverage, compliant, out complete);
            if (!complete && compliant.Count < candidates.Count)
            {
                selection = SelectCover(
                    req, candidates, coverage, Enumerable.Range(0, candidates.Count).ToList(), out complete);
            }

            var option = new LayoutOption
            {
                IsRegularGrid = false,
                PatternPriority = scheme.Priority,
                Description = "coverage-driven layout (no regular grid fits this shape)"
            };
            foreach (int index in selection)
                option.Nodes.Add(candidates[index]);

            FinaliseMetrics(req, option, stopAtFirstGap: false);
            return option;
        }

        private static List<NodePlacement> BuildGreedyCandidates(SolveRequest req, PlacementScheme scheme)
        {
            var ctx = req.Ctx;
            var frame = ctx.Frame;
            var governing = req.Governing;
            var candidates = new List<NodePlacement>();
            var keys = new HashSet<long>();

            void AddSeed(Point2D seed)
            {
                var node = Place(req, scheme, seed);
                if (node != null && keys.Add(PointKey(node.Anchor)))
                    candidates.Add(node);
            }

            void AddFrameGrid(double step, int cap)
            {
                int nu = Math.Min(cap, Math.Max(1, (int)Math.Ceiling(frame.SpanU / step)));
                int nv = Math.Min(cap, Math.Max(1, (int)Math.Ceiling(frame.SpanV / step)));
                for (int iu = 0; iu < nu; iu++)
                {
                    double u = GridCoordinate(frame.MinU, frame.MaxU, nu, iu, governing.MinWallClearanceMm);
                    for (int iv = 0; iv < nv; iv++)
                    {
                        double v = GridCoordinate(frame.MinV, frame.MaxV, nv, iv, governing.MinWallClearanceMm);
                        AddSeed(frame.ToWorld(u, v));
                    }
                }
            }

            // Coarse bays first, then a finer room-aligned sub-grid for wings, corridors and margins.
            AddFrameGrid(Math.Max(1000.0, governing.MaxWallDistanceMm), 40);
            AddFrameGrid(Math.Max(1000.0, Math.Min(2500.0, governing.MaxWallDistanceMm * 0.5)), 40);

            var interior = ctx.Geometry.InteriorPoint;
            AddSeed(new Point2D(interior.X, interior.Y));

            candidates.Sort((left, right) => right.WallClearanceMm.CompareTo(left.WallClearanceMm));
            return candidates;
        }

        private static List<int> SelectCover(
            SolveRequest req,
            IReadOnlyList<NodePlacement> candidates,
            IReadOnlyList<bool[]> coverage,
            IReadOnlyList<int> pool,
            out bool complete)
        {
            int probeCount = req.Ctx.Targets.Count * ProbeCount * req.RoleCount;
            var governing = req.Governing;

            // A single well-placed node beats any combination.
            int bestSingle = -1;
            foreach (int i in pool)
            {
                if (!coverage[i].All(c => c))
                    continue;
                if (bestSingle < 0 ||
                    (candidates[i].IsTile && !candidates[bestSingle].IsTile) ||
                    (candidates[i].IsTile == candidates[bestSingle].IsTile &&
                     candidates[i].WallClearanceMm > candidates[bestSingle].WallClearanceMm))
                    bestSingle = i;
            }

            if (bestSingle >= 0)
            {
                complete = true;
                return new List<int> { bestSingle };
            }

            if (pool.Count <= 250 && probeCount <= 25000)
            {
                int bestI = -1, bestJ = -1;
                bool bestWithinSpacing = false;
                double bestDistance = -1.0, bestWall = -1.0;
                for (int a = 0; a < pool.Count; a++)
                {
                    for (int b = a + 1; b < pool.Count; b++)
                    {
                        int i = pool[a], j = pool[b];
                        bool covers = true;
                        for (int t = 0; t < probeCount; t++)
                        {
                            if (!coverage[i][t] && !coverage[j][t])
                            {
                                covers = false;
                                break;
                            }
                        }

                        if (!covers)
                            continue;

                        double distance = candidates[i].Anchor.DistanceTo(candidates[j].Anchor);
                        bool withinSpacing = distance <= governing.MaxSpacingMm + ReachToleranceMm;
                        double wall = Math.Min(candidates[i].WallClearanceMm, candidates[j].WallClearanceMm);
                        bool better = bestI < 0
                            || (withinSpacing && !bestWithinSpacing)
                            || (withinSpacing == bestWithinSpacing && distance > bestDistance + 50.0)
                            || (withinSpacing == bestWithinSpacing && Math.Abs(distance - bestDistance) <= 50.0 && wall > bestWall);
                        if (better)
                        {
                            bestI = i;
                            bestJ = j;
                            bestWithinSpacing = withinSpacing;
                            bestDistance = distance;
                            bestWall = wall;
                        }
                    }
                }

                if (bestI >= 0)
                {
                    complete = true;
                    return new List<int> { bestI, bestJ };
                }
            }

            var remaining = new bool[probeCount];
            int remainingCount = probeCount;
            for (int t = 0; t < probeCount; t++)
                remaining[t] = true;

            var selected = new List<int>();
            while (remainingCount > 0)
            {
                int best = -1;
                int bestGain = 0;
                double bestScore = double.NegativeInfinity;
                foreach (int i in pool)
                {
                    int gain = CountGain(remaining, coverage[i]);
                    if (gain == 0)
                        continue;

                    double separation = selected.Count == 0
                        ? governing.MaxSpacingMm
                        : selected.Min(s => candidates[s].Anchor.DistanceTo(candidates[i].Anchor));

                    // Prefer nodes that cover a lot of ceiling and stay well clear of nodes already chosen.
                    double separationFactor = Math.Min(1.0, separation / Math.Max(1000.0, governing.MaxSpacingMm * 0.5));
                    double wallBonus = Math.Min(candidates[i].WallClearanceMm, governing.MaxWallDistanceMm) /
                                       Math.Max(1.0, governing.MaxWallDistanceMm);
                    double score = (double)gain / (ProbeCount * req.RoleCount) * (0.75 + 0.25 * separationFactor) +
                                   wallBonus * 0.5 + (candidates[i].IsTile ? 0.25 : 0.0);
                    if (score > bestScore || (Math.Abs(score - bestScore) < 1e-6 && gain > bestGain))
                    {
                        best = i;
                        bestScore = score;
                        bestGain = gain;
                    }
                }

                if (best < 0)
                    break;

                selected.Add(best);
                remainingCount -= MarkCovered(remaining, coverage[best]);
            }

            // Drop any pick made redundant by later ones.
            if (remainingCount == 0 && selected.Count > 2)
            {
                for (int p = selected.Count - 1; p >= 0; p--)
                {
                    var reduced = selected.Where((_, index) => index != p).ToList();
                    bool covers = true;
                    for (int t = 0; t < probeCount && covers; t++)
                        covers = reduced.Any(index => coverage[index][t]);

                    if (covers)
                        selected = reduced;
                }
            }

            complete = remainingCount == 0;
            return selected;
        }

        public static long PointKey(Point2D point)
        {
            long x = (long)Math.Round(point.X);
            long y = (long)Math.Round(point.Y);
            return (x << 32) ^ (y & 0xFFFFFFFFL);
        }
    }
}
