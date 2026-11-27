using System.Collections.Generic;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Tests
{
    /// <summary>Builds synthetic level snapshots out of rectangular rooms for solver tests.</summary>
    internal sealed class SnapshotBuilder
    {
        public SceneSnapshot Snapshot { get; } = new SceneSnapshot
        {
            Metadata = new SnapshotMetadata
            {
                DocumentTitle = "TestModel.rvt",
                LevelName = "Level 1",
                LevelElevationMm = 0,
                DefaultCeilingHeightMm = 2700
            }
        };

        /// <summary>
        /// Adds a rectangular room measured to its wall faces. Walls are modelled on centrelines
        /// set half a wall thickness outside those faces, as Revit reports them.
        /// </summary>
        public SnapshotBuilder AddRoom(
            string number,
            string name,
            double x,
            double y,
            double width,
            double depth,
            string ceilingType = "13mm Plasterboard",
            double tileU = 0,
            double tileV = 0,
            Point2D? tileOrigin = null,
            double wallThicknessMm = 0,
            double ceilingElevationMm = 2700,
            bool originVerified = true)
        {
            double half = wallThicknessMm * 0.5;
            var corners = new[]
            {
                new Point2D(x - half, y - half),
                new Point2D(x + width + half, y - half),
                new Point2D(x + width + half, y + depth + half),
                new Point2D(x - half, y + depth + half)
            };
            for (int i = 0; i < corners.Length; i++)
            {
                Snapshot.Walls.Add(new WallBarrier
                {
                    ElementId = $"W-{Snapshot.Walls.Count + 1}",
                    Centerline = new Segment2D(corners[i], corners[(i + 1) % corners.Length]),
                    ThicknessMm = wallThicknessMm,
                    BaseElevationMm = 0,
                    TopElevationMm = 3000
                });
            }

            Snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = $"C-{number}",
                TypeName = ceilingType,
                UndersideElevationMm = ceilingElevationMm,
                TopElevationMm = ceilingElevationMm + 50,
                Footprint = Rectangle(x, y, x + width, y + depth),
                TileGrid = tileU > 0 && tileV > 0
                    ? new TileGridSpec
                    {
                        OriginMm = tileOrigin ?? new Point2D(x, y),
                        AxisU = new Point2D(1, 0),
                        AxisV = new Point2D(0, 1),
                        PitchUMm = tileU,
                        PitchVMm = tileV,
                        IsOriginVerified = originVerified,
                        Source = originVerified ? "RevitGetCeilingGridLines" : "FillPatternFallback"
                    }
                    : null
            });

            Snapshot.Rooms.Add(new RoomMetadata
            {
                ElementId = $"R-{number}",
                Number = number,
                Name = name,
                AreaSqM = width * depth / 1_000_000.0,
                LocationPoint = new Point2D(x + width * 0.5, y + depth * 0.5)
            });
            return this;
        }

        public static Polygon2D Rectangle(double x1, double y1, double x2, double y2) => new Polygon2D
        {
            OuterRing = new List<Point2D>
            {
                new Point2D(x1, y1),
                new Point2D(x2, y1),
                new Point2D(x2, y2),
                new Point2D(x1, y2)
            }
        };
    }
}
