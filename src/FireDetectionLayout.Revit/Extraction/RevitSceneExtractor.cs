using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Revit.Extraction
{
    /// <summary>
    /// Extracts host and linked Architectural/Structural/Mechanical geometry into a
    /// Revit-independent SceneSnapshot (millimetres, host model coordinates).
    /// </summary>
    public sealed class RevitSceneExtractor
    {
        private const double FtToMm = 304.8;

        public SceneSnapshot ExtractActiveLevel(Document hostDoc, View activeView)
        {
            if (activeView.GenLevel == null)
                throw new InvalidOperationException("Open a Floor Plan or Reflected Ceiling Plan associated with a Level.");

            Level level = activeView.GenLevel;
            double levelElevMm = level.Elevation * FtToMm;

            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    DocumentTitle = hostDoc.Title,
                    RevitVersion = hostDoc.Application.VersionNumber,
                    ActiveViewName = activeView.Name,
                    LevelName = level.Name,
                    LevelElevationMm = Math.Round(levelElevMm, 1),
                    ExtractedUtc = DateTime.UtcNow
                }
            };

            ExtractFromDocument(hostDoc, Transform.Identity, "Host", levelElevMm, snapshot);

            var links = new FilteredElementCollector(hostDoc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null)
                    continue;

                string linkName = link.Name;
                snapshot.Metadata.IncludedLinks.Add(linkName);
                ExtractFromDocument(linkDoc, link.GetTotalTransform(), linkName, levelElevMm, snapshot);
            }

            return snapshot;
        }

        private static void ExtractFromDocument(
            Document doc,
            Transform tf,
            string sourceName,
            double targetLevelElevMm,
            SceneSnapshot snapshot)
        {
            const double levelBandBelowMm = 500.0;
            const double levelBandAboveMm = 5500.0;

            // 1. Physical walls in the active level vertical band.
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>();

            foreach (var wall in walls)
            {
                if (!(wall.Location is LocationCurve locCurve) || locCurve.Curve == null)
                    continue;

                BoundingBoxXYZ? bb = wall.get_BoundingBox(null);
                if (bb == null)
                    continue;

                double minZ = tf.OfPoint(bb.Min).Z * FtToMm;
                double maxZ = tf.OfPoint(bb.Max).Z * FtToMm;
                if (maxZ < targetLevelElevMm + 500.0 || minZ > targetLevelElevMm + levelBandAboveMm)
                    continue;

                var pts = locCurve.Curve.Tessellate();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    XYZ p0 = tf.OfPoint(pts[i]);
                    XYZ p1 = tf.OfPoint(pts[i + 1]);
                    snapshot.Walls.Add(new WallBarrier
                    {
                        ElementId = RevitIds.GetKey(wall.Id),
                        SourceModel = sourceName,
                        TypeName = wall.WallType?.Name ?? string.Empty,
                        Centerline = new Segment2D(
                            new Point2D(p0.X * FtToMm, p0.Y * FtToMm),
                            new Point2D(p1.X * FtToMm, p1.Y * FtToMm)),
                        ThicknessMm = Math.Round(wall.Width * FtToMm, 1),
                        BaseElevationMm = Math.Round(minZ, 1),
                        TopElevationMm = Math.Round(maxZ, 1),
                        IsCurtainWall = wall.WallType?.Kind == WallKind.Curtain
                    });
                }
            }

            // 2. Ceilings in the active level vertical band.
            var ceilings = new FilteredElementCollector(doc)
                .OfClass(typeof(Ceiling))
                .WhereElementIsNotElementType()
                .Cast<Ceiling>();

            foreach (var ceiling in ceilings)
            {
                BoundingBoxXYZ? bb = ceiling.get_BoundingBox(null);
                if (bb == null)
                    continue;

                double minZ = tf.OfPoint(bb.Min).Z * FtToMm;
                double maxZ = tf.OfPoint(bb.Max).Z * FtToMm;
                if (minZ < targetLevelElevMm + 1500.0 || minZ > targetLevelElevMm + levelBandAboveMm)
                    continue;

                var footprint = ExtractCeilingFootprint(ceiling, tf);
                if (footprint == null || footprint.OuterRing.Count < 3)
                    continue;

                var ceilingType = doc.GetElement(ceiling.GetTypeId()) as CeilingType;
                string typeName = ceilingType?.Name ?? ceiling.Name;
                string materialName = ExtractPrimaryMaterialName(doc, ceilingType);

                snapshot.Ceilings.Add(new CeilingSurface
                {
                    ElementId = RevitIds.GetKey(ceiling.Id),
                    SourceModel = sourceName,
                    TypeName = typeName,
                    MaterialName = materialName,
                    UndersideElevationMm = Math.Round(minZ, 1),
                    TopElevationMm = Math.Round(maxZ, 1),
                    Footprint = footprint,
                    TileGrid = TryExtractTileGrid(doc, ceiling, ceilingType, tf)
                });
            }

            // 3. Rooms strictly matched to the target level elevation.
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>();

            foreach (var room in rooms)
            {
                if (room.Area <= 0 || room.Level == null)
                    continue;

                double roomElevMm = (tf.OfPoint(new XYZ(0, 0, room.Level.Elevation)).Z) * FtToMm;
                if (Math.Abs(roomElevMm - targetLevelElevMm) > levelBandBelowMm)
                    continue;

                if (!(room.Location is LocationPoint lp))
                    continue;

                XYZ pt = tf.OfPoint(lp.Point);
                snapshot.Rooms.Add(new RoomMetadata
                {
                    ElementId = RevitIds.GetKey(room.Id),
                    SourceModel = sourceName,
                    Number = room.Number ?? string.Empty,
                    Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name,
                    AreaSqM = Math.Round(room.Area * 0.09290304, 2),
                    LocationPoint = new Point2D(pt.X * FtToMm, pt.Y * FtToMm)
                });
            }

            // 4. Ceiling-level MEP obstacles (Lighting Fixtures, Air Terminals, Sprinklers).
            ExtractObstacles(doc, tf, sourceName, BuiltInCategory.OST_LightingFixtures, MepObstacleCategory.LightingFixture, targetLevelElevMm, levelBandAboveMm, snapshot);
            ExtractObstacles(doc, tf, sourceName, BuiltInCategory.OST_DuctTerminal, MepObstacleCategory.SupplyAirDiffuser, targetLevelElevMm, levelBandAboveMm, snapshot);
            ExtractObstacles(doc, tf, sourceName, BuiltInCategory.OST_Sprinklers, MepObstacleCategory.Sprinkler, targetLevelElevMm, levelBandAboveMm, snapshot);

            // 5. Existing fire alarm devices in host model.
            if (sourceName == "Host")
            {
                var fireDevices = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>();

                foreach (var fi in fireDevices)
                {
                    if (!(fi.Location is LocationPoint lp))
                        continue;

                    XYZ pt = lp.Point;
                    double zMm = pt.Z * FtToMm;
                    if (zMm < targetLevelElevMm || zMm > targetLevelElevMm + levelBandAboveMm)
                        continue;

                    string comments = fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    snapshot.ExistingDevices.Add(new ExistingFireDevice
                    {
                        ElementId = RevitIds.GetKey(fi.Id),
                        FamilyName = fi.Symbol?.FamilyName ?? string.Empty,
                        TypeName = fi.Symbol?.Name ?? string.Empty,
                        Location = new Point2D(pt.X * FtToMm, pt.Y * FtToMm),
                        ElevationMm = Math.Round(zMm, 1),
                        IsToolOwned = comments.IndexOf("FIRE_LAYOUT_AUTO", StringComparison.OrdinalIgnoreCase) >= 0
                    });
                }
            }
        }

        private static void ExtractObstacles(
            Document doc,
            Transform tf,
            string sourceName,
            BuiltInCategory category,
            MepObstacleCategory mappedCategory,
            double targetLevelElevMm,
            double levelBandAboveMm,
            SceneSnapshot snapshot)
        {
            var elements = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType();

            foreach (var elem in elements)
            {
                BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                if (bb == null)
                    continue;

                XYZ min = tf.OfPoint(bb.Min);
                XYZ max = tf.OfPoint(bb.Max);
                double zMm = min.Z * FtToMm;
                if (zMm < targetLevelElevMm + 1500.0 || zMm > targetLevelElevMm + levelBandAboveMm)
                    continue;

                var box = new BoundingBox2D
                {
                    MinX = Math.Min(min.X, max.X) * FtToMm,
                    MinY = Math.Min(min.Y, max.Y) * FtToMm,
                    MaxX = Math.Max(min.X, max.X) * FtToMm,
                    MaxY = Math.Max(min.Y, max.Y) * FtToMm
                };

                snapshot.Obstacles.Add(new MepObstacle
                {
                    ElementId = RevitIds.GetKey(elem.Id),
                    SourceModel = sourceName,
                    Category = mappedCategory,
                    TypeName = elem.Name,
                    Center = box.Center,
                    Bounds = box,
                    ElevationMm = Math.Round(zMm, 1)
                });
            }
        }

        private static Polygon2D? ExtractCeilingFootprint(Ceiling ceiling, Transform tf)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
            GeometryElement? geom = ceiling.get_Geometry(opt);
            if (geom == null)
                return null;

            PlanarFace? bottomFace = null;
            double lowestZ = double.PositiveInfinity;

            foreach (GeometryObject obj in geom)
            {
                if (!(obj is Solid solid) || solid.Faces.Size == 0)
                    continue;

                foreach (Face face in solid.Faces)
                {
                    if (face is PlanarFace pf && pf.FaceNormal.Z < -0.95)
                    {
                        double z = pf.Origin.Z;
                        if (z < lowestZ)
                        {
                            lowestZ = z;
                            bottomFace = pf;
                        }
                    }
                }
            }

            if (bottomFace == null)
                return null;

            var loops = bottomFace.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0)
                return null;

            var polygon = new Polygon2D();
            bool first = true;
            foreach (CurveLoop loop in loops)
            {
                var ring = new List<Point2D>();
                foreach (Curve curve in loop)
                {
                    var pts = curve.Tessellate();
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        XYZ p = tf.OfPoint(pts[i]);
                        ring.Add(new Point2D(p.X * FtToMm, p.Y * FtToMm));
                    }
                }

                if (ring.Count < 3)
                    continue;

                if (first)
                {
                    polygon.OuterRing = ring;
                    first = false;
                }
                else
                {
                    polygon.Holes.Add(ring);
                }
            }

            return polygon.OuterRing.Count >= 3 ? polygon : null;
        }

        private static string ExtractPrimaryMaterialName(Document doc, CeilingType? ceilingType)
        {
            if (ceilingType == null)
                return string.Empty;

            var cs = ceilingType.GetCompoundStructure();
            if (cs == null)
                return string.Empty;

            var layers = cs.GetLayers();
            if (layers.Count == 0)
                return string.Empty;

            var mat = doc.GetElement(layers[layers.Count - 1].MaterialId) as Material;
            return mat?.Name ?? string.Empty;
        }

        private static TileGridSpec? TryExtractTileGrid(Document doc, Ceiling ceiling, CeilingType? ceilingType, Transform tf)
        {
            // Revit 2025.3+: use Ceiling.GetCeilingGridLines(false) via reflection so R23-R25.0 still compile.
            MethodInfo? gridMethod = ceiling.GetType().GetMethod("GetCeilingGridLines", new[] { typeof(bool) });
            if (gridMethod != null)
            {
                try
                {
                    if (gridMethod.Invoke(ceiling, new object[] { false }) is IList<Curve> curves && curves.Count >= 2)
                    {
                        var nativeGrid = BuildGridFromLines(curves, tf);
                        if (nativeGrid != null)
                            return nativeGrid;
                    }
                }
                catch
                {
                    // Fall through to fill-pattern metadata fallback.
                }
            }

            // Fallback (Revit 2023-2025.2): read pitch/orientation from ceiling material surface pattern.
            if (ceilingType == null)
                return null;

            var cs = ceilingType.GetCompoundStructure();
            var layers = cs?.GetLayers();
            if (layers == null || layers.Count == 0)
                return null;

            var mat = doc.GetElement(layers[layers.Count - 1].MaterialId) as Material;
            if (mat == null || mat.SurfaceForegroundPatternId == ElementId.InvalidElementId)
                return null;

            var fpe = doc.GetElement(mat.SurfaceForegroundPatternId) as FillPatternElement;
            var pattern = fpe?.GetFillPattern();
            if (pattern == null || pattern.Target != FillPatternTarget.Model)
                return null;

            var grids = pattern.GetFillGrids();
            if (grids.Count < 2)
                return null;

            double pitchU = Math.Round(grids[0].Offset * FtToMm, 1);
            double pitchV = Math.Round(grids[1].Offset * FtToMm, 1);
            if (pitchU < 200.0 || pitchV < 200.0)
                return null;

            double angle = grids[0].Angle;
            XYZ dirU = tf.OfVector(new XYZ(Math.Cos(angle), Math.Sin(angle), 0)).Normalize();
            XYZ dirV = new XYZ(-dirU.Y, dirU.X, 0);

            return new TileGridSpec
            {
                OriginMm = new Point2D(0, 0),
                AxisU = new Point2D(dirU.X, dirU.Y),
                AxisV = new Point2D(dirV.X, dirV.Y),
                PitchUMm = pitchU,
                PitchVMm = pitchV,
                IsOriginVerified = false,
                Source = "FillPatternFallback"
            };
        }

        private static TileGridSpec? BuildGridFromLines(IList<Curve> curves, Transform tf)
        {
            var lines = curves
                .Select(c => (Start: tf.OfPoint(c.GetEndPoint(0)), End: tf.OfPoint(c.GetEndPoint(1))))
                .Where(s => s.Start.DistanceTo(s.End) > 0.1)
                .ToList();

            if (lines.Count < 2)
                return null;

            XYZ uDir = (lines[0].End - lines[0].Start).Normalize();
            uDir = new XYZ(uDir.X, uDir.Y, 0).Normalize();
            XYZ vDir = new XYZ(-uDir.Y, uDir.X, 0);

            var uOffsets = new List<double>();
            var vOffsets = new List<double>();

            foreach (var seg in lines)
            {
                XYZ d = new XYZ(seg.End.X - seg.Start.X, seg.End.Y - seg.Start.Y, 0).Normalize();
                if (Math.Abs(d.DotProduct(uDir)) > 0.95)
                    vOffsets.Add((seg.Start.X * vDir.X + seg.Start.Y * vDir.Y) * FtToMm);
                else if (Math.Abs(d.DotProduct(vDir)) > 0.95)
                    uOffsets.Add((seg.Start.X * uDir.X + seg.Start.Y * uDir.Y) * FtToMm);
            }

            double pitchU = MedianSpacing(uOffsets);
            double pitchV = MedianSpacing(vOffsets);
            if (pitchU < 200.0 || pitchV < 200.0)
                return null;

            uOffsets.Sort();
            vOffsets.Sort();
            double originU = uOffsets[0];
            double originV = vOffsets[0];

            return new TileGridSpec
            {
                OriginMm = new Point2D(
                    originU * uDir.X + originV * vDir.X,
                    originU * uDir.Y + originV * vDir.Y),
                AxisU = new Point2D(uDir.X, uDir.Y),
                AxisV = new Point2D(vDir.X, vDir.Y),
                PitchUMm = Math.Round(pitchU, 1),
                PitchVMm = Math.Round(pitchV, 1),
                IsOriginVerified = true,
                Source = "RevitGetCeilingGridLines"
            };
        }

        private static double MedianSpacing(List<double> values)
        {
            if (values.Count < 2) return 0;
            values.Sort();
            var diffs = new List<double>();
            for (int i = 1; i < values.Count; i++)
            {
                double diff = values[i] - values[i - 1];
                if (diff > 50.0) diffs.Add(diff);
            }
            if (diffs.Count == 0) return 0;
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }
    }
}
