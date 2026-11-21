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
        public void Solve_ShellSpaceWithConcreteSoffitOnlyPlacesDetectorsAndFlagsAmber()
        {
            var area = RectanglePolygon(0, 0, 8000, 6000);
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    DocumentTitle = "ShellModel.rvt",
                    LevelName = "Level 1",
                    LevelElevationMm = 0,
                    DefaultCeilingHeightMm = 2700
                },
                Soffits = new List<CeilingSurface>
                {
                    new CeilingSurface
                    {
                        ElementId = "S-200",
                        TypeName = "200mm Concrete Slab",
                        UndersideElevationMm = 3800,
                        TopElevationMm = 4000,
                        Footprint = area
                    }
                },
                Rooms = new List<RoomMetadata>
                {
                    new RoomMetadata
                    {
                        ElementId = "R-Shell",
                        Number = "102",
                        Name = "Shell Tenancy",
                        LocationPoint = new Point2D(4000, 3000)
                    }
                }
            };
            AddWalls(area.OuterRing, snapshot.Walls, topElevationMm: 3800);

            var plan = new FireLayoutEngine().Solve(snapshot);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CeilingKind.ExposedOrNone, comp.CeilingKind);
            Assert.Equal(CompartmentStatus.Amber, comp.Status);
            Assert.True(comp.IsApprovedForPlacement);
            Assert.Equal(3800.0, comp.CeilingElevationMm, 1);
            Assert.True(comp.ProposedDetectorCount >= 1);
            Assert.Contains(comp.Notes, note => note.Contains("200mm Concrete Slab"));
            Assert.All(plan.Devices, d =>
            {
                Assert.Equal(3800.0, d.ElevationMm, 1);
                Assert.Equal(CeilingKind.ExposedOrNone, d.HostCeilingKind);
                Assert.Equal("S-200", d.HostCeilingElementId);
            });
        }

        [Fact]
        public void Solve_SpaceWithNoCeilingAndNoSoffitStillSolvesAndFlagsAmber()
        {
            var area = RectanglePolygon(0, 0, 6000, 4000);
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    DocumentTitle = "NoCeiling.rvt",
                    LevelName = "Level 1",
                    LevelElevationMm = 0,
                    DefaultCeilingHeightMm = 3600
                }
            };
            AddWalls(area.OuterRing, snapshot.Walls, topElevationMm: 3600);

            var plan = new FireLayoutEngine().Solve(snapshot);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CeilingKind.ExposedOrNone, comp.CeilingKind);
            Assert.Equal(CompartmentStatus.Amber, comp.Status);
            Assert.True(comp.IsApprovedForPlacement);
            Assert.Equal(3600.0, comp.CeilingElevationMm, 1);
            Assert.Single(plan.Devices);
            Assert.Equal(3600.0, plan.Devices[0].ElevationMm, 1);
        }

        [Fact]
        public void Solve_LongRoomPlacesDetectorsInSymmetricWellSpacedGrid()
        {
            var snapshot = BuildRectangularRoomSnapshot(14000, 6000, "Plasterboard", null);
            var plan = new FireLayoutEngine().Solve(snapshot);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.Equal(2, plan.Devices.Count);

            var ordered = plan.Devices.OrderBy(d => d.Location.X).ToList();
            Assert.Equal(3500.0, ordered[0].Location.X, 1);
            Assert.Equal(3000.0, ordered[0].Location.Y, 1);
            Assert.Equal(10500.0, ordered[1].Location.X, 1);
            Assert.Equal(3000.0, ordered[1].Location.Y, 1);

            double spacing = ordered[0].Location.DistanceTo(ordered[1].Location);
            Assert.InRange(spacing, 6900.0, 7100.0);
        }

        [Fact]
        public void Solve_RotatedRoomFollowsRoomProfileOrientation()
        {
            double angleRad = 30.0 * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            Point2D Rot(double u, double v) => new Point2D(u * cos - v * sin, u * sin + v * cos);

            var ring = new List<Point2D>
            {
                Rot(0, 0),
                Rot(14000, 0),
                Rot(14000, 6000),
                Rot(0, 6000)
            };

            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    DocumentTitle = "Rotated.rvt",
                    LevelName = "Level 1",
                    LevelElevationMm = 0,
                    DefaultCeilingHeightMm = 2700
                }
            };
            AddWalls(ring, snapshot.Walls);
            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "C-ROT",
                TypeName = "Plasterboard",
                UndersideElevationMm = 2700,
                TopElevationMm = 2750,
                Footprint = new Polygon2D { OuterRing = ring }
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.Equal(2, plan.Devices.Count);

            // Project placed devices back into the rotated room's (u, v) frame.
            var uv = plan.Devices
                .Select(d => (
                    U: d.Location.X * cos + d.Location.Y * sin,
                    V: -d.Location.X * sin + d.Location.Y * cos))
                .OrderBy(p => p.U)
                .ToList();

            Assert.InRange(uv[0].U, 3400.0, 3600.0);
            Assert.InRange(uv[0].V, 2900.0, 3100.0);
            Assert.InRange(uv[1].U, 10400.0, 10600.0);
            Assert.InRange(uv[1].V, 2900.0, 3100.0);
        }

        [Fact]
        public void Solve_RespectsConfiguredMinimumWallOffset()
        {
            var snapshot = BuildRectangularRoomSnapshot(14000, 5000, "Plasterboard", null);
            var profile = RuleProfile.CreateAs1670Default();
            profile.SmokeDetection.MaxWallDistanceMm = 3500.0;
            profile.SmokeDetection.MaxDetectorSpacingMm = 7000.0;
            profile.SmokeDetection.MinWallClearanceMm = 2000.0;
            profile.SmokeDetection.HardMinWallClearanceMm = 300.0;

            var plan = new FireLayoutEngine().Solve(snapshot, profile);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.True(plan.Devices.Count >= 2);
            Assert.All(plan.Devices, d =>
            {
                Assert.True(d.Location.X >= 1999.0 && d.Location.X <= 12001.0);
                Assert.True(d.Location.Y >= 1999.0 && d.Location.Y <= 3001.0);
            });
        }

        [Fact]
        public void Solve_ReordersMisorderedCeilingLoopsSoPipeHoleDoesNotInvalidateCorridor()
        {
            var corridor = RectanglePolygon(0, 0, 24000, 2400);
            var snapshot = BuildSnapshot(corridor, "5/8\" GWB on Mtl. Furring");
            // Simulate Revit returning a 120mm pipe penetration loop before the actual ceiling boundary.
            snapshot.Ceilings[0].Footprint = new Polygon2D
            {
                OuterRing = RectanglePolygon(4000, 1000, 4120, 1120).OuterRing,
                Holes = new List<List<Point2D>> { corridor.OuterRing }
            };

            var plan = new FireLayoutEngine().Solve(snapshot);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CeilingKind.Plasterboard, comp.CeilingKind);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.True(comp.IsApprovedForPlacement);
            Assert.Equal(3, plan.Devices.Count);
        }

        [Fact]
        public void Solve_SmallRoomWithCornerNotchAndColumnPlacesSingleDetector()
        {
            var room = new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 3000),
                    new Point2D(1000, 3000), new Point2D(1000, 2400), new Point2D(0, 2400)
                },
                Holes = new List<List<Point2D>>
                {
                    // 400mm x 400mm structural column
                    RectanglePolygon(1400, 1300, 1800, 1700).OuterRing
                }
            };
            var snapshot = BuildSnapshot(room, "Plasterboard");

            var plan = new FireLayoutEngine().Solve(snapshot);

            var comp = Assert.Single(plan.Compartments);
            Assert.Equal(CompartmentStatus.Green, comp.Status);
            Assert.Single(plan.Devices);
        }

        [Fact]
        public void Solve_RecoversRoomWhenWallCenterlineLoopIsOpenAndSubtractsEnclosedSubRooms()
        {
            // Simulate Office Unit 401: south wall has a 250mm centerline gap so wall-graph fails,
            // while an interior WC (0..3000 x 7000..10000) DOES close in the wall graph.
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata { LevelName = "L4", LevelElevationMm = 10000 }
            };

            // Closed WC sub-room walls
            AddWall(snapshot, 0, 7000, 3000, 7000, 10000, 13500);
            AddWall(snapshot, 3000, 7000, 3000, 10000, 10000, 13500);
            AddWall(snapshot, 3000, 10000, 0, 10000, 10000, 13500);
            AddWall(snapshot, 0, 10000, 0, 7000, 10000, 13500);

            // Outer office walls with a 250mm gap at the south-east corner
            AddWall(snapshot, 0, 0, 11750, 0, 10000, 13500);
            AddWall(snapshot, 12000, 0, 12000, 10000, 10000, 13500);
            AddWall(snapshot, 12000, 10000, 3000, 10000, 10000, 13500);
            AddWall(snapshot, 0, 7000, 0, 0, 10000, 13500);

            // Overhead concrete soffit covering the entire floor (no suspended ceiling)
            snapshot.Soffits.Add(new CeilingSurface
            {
                ElementId = "SOF-1",
                TypeName = "NW Concrete on Metal Deck",
                UndersideElevationMm = 13500,
                TopElevationMm = 13700,
                Footprint = RectanglePolygon(-500, -500, 12500, 10500)
            });

            // Revit Room boundary for Office Unit 401 (L-shaped around the WC)
            snapshot.Rooms.Add(new RoomMetadata
            {
                ElementId = "RM-401",
                Number = "401",
                Name = "Office Unit",
                AreaSqM = 111.0,
                UnboundedHeightMm = 3500,
                LocationPoint = new Point2D(7000, 5000),
                Footprint = new Polygon2D
                {
                    OuterRing = new List<Point2D>
                    {
                        new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 10000),
                        new Point2D(3000, 10000), new Point2D(3000, 7000), new Point2D(0, 7000)
                    }
                }
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Equal(2, plan.Compartments.Count);
            var office = Assert.Single(plan.Compartments, c => c.DisplayName == "401 Office Unit");
            Assert.Equal("Room", office.SourceKind);
            Assert.Equal(CeilingKind.ExposedOrNone, office.CeilingKind);
            Assert.True(office.IsApprovedForPlacement);
            Assert.True(office.ProposedDetectorCount >= 2);
            Assert.Equal(13500, office.CeilingElevationMm);
        }

        [Fact]
        public void Solve_RecoversMepSpaceAndOrphanCeilingWhenNoWallsClose()
        {
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata { LevelName = "L4", LevelElevationMm = 0 }
            };

            // MEP Space with no walls and no ceiling/soffit -> uses UnboundedHeightMm fallback
            snapshot.Spaces.Add(new RoomMetadata
            {
                ElementId = "SP-101",
                Number = "101",
                Name = "Open Shell",
                IsSpace = true,
                AreaSqM = 36.0,
                UnboundedHeightMm = 3200,
                LocationPoint = new Point2D(3000, 3000),
                Footprint = RectanglePolygon(0, 0, 6000, 6000)
            });

            // Separate orphan ceiling with no walls and no room/space
            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "CLG-202",
                TypeName = "Plasterboard",
                MaterialName = "Gypsum",
                UndersideElevationMm = 2800,
                TopElevationMm = 2820,
                Footprint = RectanglePolygon(10000, 0, 15000, 5000)
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Equal(2, plan.Compartments.Count);
            var spaceComp = Assert.Single(plan.Compartments, c => c.SourceKind == "Space");
            Assert.Equal("101 Open Shell", spaceComp.DisplayName);
            Assert.Equal(3200, spaceComp.CeilingElevationMm);
            Assert.Equal(1, spaceComp.ProposedDetectorCount);

            var ceilComp = Assert.Single(plan.Compartments, c => c.SourceKind == "Ceiling");
            Assert.Equal(CeilingKind.Plasterboard, ceilComp.CeilingKind);
            Assert.Equal(1, ceilComp.ProposedDetectorCount);
        }

        [Fact]
        public void Solve_EmitsWarningWhenRoomHasNoFootprintAndIsMissedByWalls()
        {
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata { LevelName = "L4", LevelElevationMm = 0 }
            };
            snapshot.Rooms.Add(new RoomMetadata
            {
                ElementId = "RM-999",
                Number = "401",
                Name = "Office Unit",
                AreaSqM = 216.0,
                LocationPoint = new Point2D(5000, 5000),
                Footprint = null
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Contains(plan.Warnings, w => w.Contains("401 Office Unit"));
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

        private static void AddWall(SceneSnapshot snapshot, double x0, double y0, double x1, double y1, double baseZ, double topZ)
        {
            snapshot.Walls.Add(new WallBarrier
            {
                ElementId = $"W-{snapshot.Walls.Count + 1}",
                Centerline = new Segment2D(new Point2D(x0, y0), new Point2D(x1, y1)),
                ThicknessMm = 150,
                BaseElevationMm = baseZ,
                TopElevationMm = topZ
            });
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

        private static void AddWalls(IReadOnlyList<Point2D> ring, List<WallBarrier> walls, double topElevationMm = 3000)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                walls.Add(new WallBarrier
                {
                    Centerline = new Segment2D(ring[i], ring[(i + 1) % ring.Count]),
                    BaseElevationMm = 0,
                    TopElevationMm = topElevationMm
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
