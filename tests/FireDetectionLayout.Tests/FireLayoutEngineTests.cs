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
        public void Solve_LShapedRoomPlacesDetectorsOnlyInVisibleCompartmentArea()
        {
            var area = new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 4000),
                    new Point2D(4000, 4000), new Point2D(4000, 10000), new Point2D(0, 10000)
                }
            };
            var plan = new FireLayoutEngine().Solve(BuildSnapshot(area, "Gyprock Plasterboard"));

            var compartment = Assert.Single(plan.Compartments);
            Assert.Equal(CompartmentStatus.Green, compartment.Status);
            Assert.True(plan.Devices.Count >= 2);
            Assert.All(plan.Devices, device =>
            {
                Assert.True(device.Location.X <= 4000 || device.Location.Y <= 4000);
            });
        }

        [Fact]
        public void Solve_MixedFinishUsesTileCentresWhenTheyCoverTheCompartment()
        {
            var area = RectanglePolygon(0, 0, 8000, 6000);
            var snapshot = BuildSnapshot(area, "13mm Flush Plasterboard");
            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "TileField",
                TypeName = "1200x600 Acoustic Tile",
                UndersideElevationMm = 2700,
                TileGrid = new TileGridSpec
                {
                    OriginMm = new Point2D(0, 0),
                    AxisU = new Point2D(1, 0),
                    AxisV = new Point2D(0, 1),
                    PitchUMm = 1200,
                    PitchVMm = 600,
                    IsOriginVerified = true
                },
                Footprint = RectanglePolygon(500, 500, 7500, 5500)
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            var compartment = Assert.Single(plan.Compartments);
            Assert.Equal(CeilingKind.MixedFinish, compartment.CeilingKind);
            Assert.True(
                compartment.Status == CompartmentStatus.Green,
                $"{compartment.Status}: {string.Join("; ", compartment.Notes)}");
            Assert.All(plan.Devices, device => Assert.True(device.WasSnappedToTile));
        }

        [Fact]
        public void Solve_MixedFinishSpillsToMarginWhenTileFieldCannotReachOuterWalls()
        {
            var area = RectanglePolygon(0, 0, 20000, 8000);
            var snapshot = BuildSnapshot(area, "13mm Flush Plasterboard");
            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "TileField",
                TypeName = "1200x600 Acoustic Tile",
                UndersideElevationMm = 2700,
                TileGrid = new TileGridSpec
                {
                    OriginMm = new Point2D(0, 0),
                    AxisU = new Point2D(1, 0),
                    AxisV = new Point2D(0, 1),
                    PitchUMm = 1200,
                    PitchVMm = 600,
                    IsOriginVerified = true
                },
                Footprint = RectanglePolygon(8000, 1000, 12000, 7000)
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            var compartment = Assert.Single(plan.Compartments);
            Assert.Equal(CeilingKind.MixedFinish, compartment.CeilingKind);
            Assert.Equal(CompartmentStatus.Amber, compartment.Status);
            Assert.Contains(compartment.Notes, note => note.Contains("plasterboard margin spillover"));
            Assert.Contains(plan.Devices, device => !device.WasSnappedToTile);
        }

        [Fact]
        public void Solve_InvalidCeilingWithholdsOnlyAffectedCompartment()
        {
            var snapshot = BuildSnapshot(RectanglePolygon(0, 0, 6000, 6000), "Plasterboard");
            var invalidCeiling = snapshot.Ceilings[0];
            invalidCeiling.ElementId = "C-Invalid";
            invalidCeiling.Footprint = new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    new Point2D(0, 0), new Point2D(6000, 6000),
                    new Point2D(0, 6000), new Point2D(6000, 0)
                }
            };
            var otherArea = RectanglePolygon(10000, 0, 14000, 4000);
            AddWalls(otherArea.OuterRing, snapshot.Walls);
            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "C-Valid",
                TypeName = "Plasterboard",
                UndersideElevationMm = 2700,
                Footprint = otherArea
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Equal(2, plan.Compartments.Count);
            var withheld = Assert.Single(plan.Compartments, c => c.CeilingKind == CeilingKind.Unknown);
            Assert.Equal(CompartmentStatus.Red, withheld.Status);
            Assert.False(withheld.IsApprovedForPlacement);
            Assert.Contains(withheld.Notes, note => note.Contains("C-Invalid"));
            var approved = Assert.Single(plan.Compartments, c => c.CeilingKind == CeilingKind.Plasterboard);
            Assert.Equal(CompartmentStatus.Green, approved.Status);
            Assert.True(approved.IsApprovedForPlacement);
            var device = Assert.Single(plan.Devices);
            Assert.Equal(approved.CompartmentId, device.CompartmentId);
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

        private static SceneSnapshot BuildSnapshot(Polygon2D area, string ceilingType)
        {
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    DocumentTitle = "TestModel.rvt",
                    LevelName = "Level 1",
                    LevelElevationMm = 0,
                    DefaultCeilingHeightMm = 2700
                },
                Ceilings = new List<CeilingSurface>
                {
                    new CeilingSurface
                    {
                        ElementId = "Ceiling",
                        TypeName = ceilingType,
                        UndersideElevationMm = 2700,
                        Footprint = area
                    }
                }
            };

            AddWalls(area.OuterRing, snapshot.Walls);
            foreach (var hole in area.Holes)
                AddWalls(hole, snapshot.Walls);

            return snapshot;
        }

        private static void AddWalls(IReadOnlyList<Point2D> ring, List<WallBarrier> walls)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                walls.Add(new WallBarrier
                {
                    Centerline = new Segment2D(ring[i], ring[(i + 1) % ring.Count]),
                    BaseElevationMm = 0,
                    TopElevationMm = 3000
                });
            }
        }

        private static Polygon2D RectanglePolygon(double x1, double y1, double x2, double y2) =>
            new Polygon2D
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
