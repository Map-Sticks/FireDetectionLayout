using System.Collections.Generic;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Solver;
using FireDetectionLayout.Core.Storage;
using Xunit;

namespace FireDetectionLayout.Tests
{
    public class FireLayoutEngineTests
    {
        [Fact]
        public void Solve_TiledOfficeSnapsDetectorsToVerifiedTileCentres()
        {
            var snapshot = BuildRectangularRoomSnapshot(
                widthMm: 12000,
                heightMm: 6000,
                ceilingType: "1200x600 Acoustic Tile",
                tileGrid: new TileGridSpec
                {
                    OriginMm = new Point2D(0, 0),
                    AxisU = new Point2D(1, 0),
                    AxisV = new Point2D(0, 1),
                    PitchUMm = 1200,
                    PitchVMm = 600,
                    IsOriginVerified = true,
                    Source = "RevitGetCeilingGridLines"
                });

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Single(plan.Compartments);
            var comp = plan.Compartments[0];
            Assert.Equal(CeilingKind.SuspendedTile, comp.CeilingKind);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.Equal(2, comp.ProposedDetectorCount);
            Assert.All(plan.Devices, d =>
            {
                Assert.True(d.WasSnappedToTile);
                // Must sit on a 1200x600 tile centre (odd multiple of 600 in X, odd multiple of 300 in Y).
                Assert.Equal(600.0, d.Location.X % 1200.0, 1);
                Assert.Equal(300.0, d.Location.Y % 600.0, 1);
            });
        }

        [Fact]
        public void Solve_PlasterboardSmallRoomPlacesSingleCentredDetector()
        {
            var snapshot = BuildRectangularRoomSnapshot(
                widthMm: 5000,
                heightMm: 4000,
                ceilingType: "13mm Gyprock Plasterboard",
                tileGrid: null);

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Single(plan.Compartments);
            var comp = plan.Compartments[0];
            Assert.Equal(CeilingKind.Plasterboard, comp.CeilingKind);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.Equal(1, comp.ProposedDetectorCount);
            Assert.Equal(2500.0, plan.Devices[0].Location.X, 1);
            Assert.Equal(2000.0, plan.Devices[0].Location.Y, 1);
        }

        [Fact]
        public void SnapshotSerializer_RoundTripsSnapshotAndPlan()
        {
            var snapshot = BuildRectangularRoomSnapshot(6000, 4000, "Plasterboard", null);
            string json = SnapshotSerializer.Serialize(snapshot);
            var restored = SnapshotSerializer.Deserialize<SceneSnapshot>(json);

            Assert.Equal(snapshot.Walls.Count, restored.Walls.Count);
            Assert.Equal(snapshot.Ceilings.Count, restored.Ceilings.Count);
        }

        private static SceneSnapshot BuildRectangularRoomSnapshot(
            double widthMm,
            double heightMm,
            string ceilingType,
            TileGridSpec? tileGrid)
        {
            return new SceneSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    DocumentTitle = "TestModel.rvt",
                    LevelName = "Level 1",
                    LevelElevationMm = 0,
                    DefaultCeilingHeightMm = 2700
                },
                Walls = new List<WallBarrier>
                {
                    new WallBarrier { Centerline = new Segment2D(new Point2D(0, 0), new Point2D(widthMm, 0)), BaseElevationMm = 0, TopElevationMm = 3000 },
                    new WallBarrier { Centerline = new Segment2D(new Point2D(widthMm, 0), new Point2D(widthMm, heightMm)), BaseElevationMm = 0, TopElevationMm = 3000 },
                    new WallBarrier { Centerline = new Segment2D(new Point2D(widthMm, heightMm), new Point2D(0, heightMm)), BaseElevationMm = 0, TopElevationMm = 3000 },
                    new WallBarrier { Centerline = new Segment2D(new Point2D(0, heightMm), new Point2D(0, 0)), BaseElevationMm = 0, TopElevationMm = 3000 }
                },
                Ceilings = new List<CeilingSurface>
                {
                    new CeilingSurface
                    {
                        ElementId = "C-100",
                        TypeName = ceilingType,
                        UndersideElevationMm = 2700,
                        TopElevationMm = 2750,
                        TileGrid = tileGrid,
                        Footprint = new Polygon2D
                        {
                            OuterRing = new List<Point2D>
                            {
                                new Point2D(0, 0),
                                new Point2D(widthMm, 0),
                                new Point2D(widthMm, heightMm),
                                new Point2D(0, heightMm)
                            }
                        }
                    }
                },
                Rooms = new List<RoomMetadata>
                {
                    new RoomMetadata
                    {
                        ElementId = "R-100",
                        Number = "101",
                        Name = "Office",
                        LocationPoint = new Point2D(widthMm * 0.5, heightMm * 0.5)
                    }
                }
            };
        }
    }
}
