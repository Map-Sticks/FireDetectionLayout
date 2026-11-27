using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Solver;
using FireDetectionLayout.Core.Storage;
using Xunit;

namespace FireDetectionLayout.Tests
{
    public class LayoutSessionTests
    {
        private static RuleProfile PairedProfile()
        {
            var profile = RuleProfile.CreateAs1670Default();
            profile.Layout.PlaceSpeakers = true;
            return profile;
        }

        private static string Describe(SmokeCompartment compartment) =>
            $"{compartment.Status}: {string.Join("; ", compartment.Notes)}";

        [Theory]
        [InlineData(1200, 600, true)]   // two devices share one tile, each centred in its own half
        [InlineData(1200, 300, true)]   // two devices share one plank
        [InlineData(600, 600, false)]   // one device per tile: neighbouring tiles
        [InlineData(300, 300, false)]   // one device per tile: tiles 600 mm apart
        public void PairedLayout_KeepsDetectorAndSpeakerOnTileModuleCentres(double tileU, double tileV, bool sameTile)
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 7200, 6000, $"{tileU}x{tileV} Acoustic Tile", tileU, tileV)
                .Snapshot;

            var session = new LayoutSession(snapshot, PairedProfile());

            var compartment = Assert.Single(session.Plan.Compartments);
            Assert.True(compartment.Status == CompartmentStatus.Green, Describe(compartment));
            Assert.Equal($"{tileU}x{tileV}", compartment.TileSize);
            var detector = Assert.Single(session.Plan.Devices, d => d.Role == DeviceRole.SmokeDetector);
            var speaker = Assert.Single(session.Plan.Devices, d => d.Role == DeviceRole.EwisSpeaker);
            Assert.NotNull(detector.PairId);
            Assert.Equal(detector.PairId, speaker.PairId);
            Assert.Equal(600.0, detector.Location.DistanceTo(speaker.Location), 1);

            // A device module is a whole tile, or half of a tile long enough to host two devices.
            double moduleU = tileU >= 1200 ? tileU / 2 : tileU;
            double moduleV = tileV >= 1200 ? tileV / 2 : tileV;
            foreach (var device in new[] { detector, speaker })
            {
                Assert.True(device.WasSnappedToTile);
                Assert.Equal(moduleU / 2, device.Location.X % moduleU, 1);
                Assert.Equal(moduleV / 2, device.Location.Y % moduleV, 1);
            }

            bool sharesTile =
                Math.Floor(detector.Location.X / tileU) == Math.Floor(speaker.Location.X / tileU) &&
                Math.Floor(detector.Location.Y / tileV) == Math.Floor(speaker.Location.Y / tileV);
            Assert.Equal(sameTile, sharesTile);
        }

        [Theory]
        [InlineData(1200, 600)]  // tiles run along the corridor
        [InlineData(600, 1200)]  // tiles run across the corridor
        public void PairedLayout_CorridorDevicesStayInsideTilesAndClearOfWalls(double tileU, double tileV)
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("C1", "Corridor", 0, 0, 24000, 1800, "1200x600 Acoustic Tile", tileU, tileV)
                .Snapshot;

            var session = new LayoutSession(snapshot, PairedProfile());

            var compartment = Assert.Single(session.Plan.Compartments);
            Assert.True(compartment.Status == CompartmentStatus.Green, Describe(compartment));
            Assert.Equal(3, compartment.ProposedDetectorCount);
            Assert.Equal(3, compartment.ProposedSpeakerCount);
            Assert.All(session.Plan.Devices, device =>
            {
                Assert.True(device.WasSnappedToTile);
                Assert.InRange(device.Location.Y, 500.0, 1300.0);
            });
        }

        [Fact]
        public void PairedLayout_FlushCeilingStraddlesTheGridPointAlongTheRoom()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 9000, 4800).Snapshot;

            var session = new LayoutSession(snapshot, PairedProfile());

            var detector = Assert.Single(session.Plan.Devices, d => d.Role == DeviceRole.SmokeDetector);
            var speaker = Assert.Single(session.Plan.Devices, d => d.Role == DeviceRole.EwisSpeaker);
            Assert.Equal(4200.0, detector.Location.X, 1);
            Assert.Equal(4800.0, speaker.Location.X, 1);
            Assert.Equal(2400.0, detector.Location.Y, 1);
            Assert.Equal(2400.0, speaker.Location.Y, 1);
        }

        [Fact]
        public void IndependentSpeakerGrid_UsesSpeakerSpacingAndPairsCoincidentDevices()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 12000, 6000).Snapshot;
            var profile = PairedProfile();
            profile.Ewis.LayoutMode = SpeakerLayoutMode.IndependentGrid;
            profile.Ewis.MaxSpeakerSpacingMm = 4000;
            profile.Ewis.MaxWallDistanceMm = 2500;

            var session = new LayoutSession(snapshot, profile);

            var compartment = Assert.Single(session.Plan.Compartments);
            Assert.True(compartment.Status == CompartmentStatus.Green, Describe(compartment));
            Assert.Equal(2, compartment.ProposedDetectorCount);
            Assert.True(compartment.ProposedSpeakerCount > compartment.ProposedDetectorCount);

            var active = session.Plan.Devices;
            for (int i = 0; i < active.Count; i++)
            {
                for (int j = i + 1; j < active.Count; j++)
                    Assert.True(active[i].Location.DistanceTo(active[j].Location) >= 400.0);
            }
        }

        [Fact]
        public void Solve_MeasuresWallReachToTheWallFace()
        {
            // One centred detector would be 5.5 m from the end walls of an 11 m room.
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 11000, 4000).Snapshot;

            var plan = new FireLayoutEngine().Solve(snapshot);

            var compartment = Assert.Single(plan.Compartments);
            Assert.Equal(2, compartment.ProposedDetectorCount);
            Assert.InRange(compartment.WorstCaseWallDistanceMm, 2700.0, 5000.0);
        }

        [Fact]
        public void Solve_TrimsWallThicknessSoDistancesAreMeasuredFromWallFaces()
        {
            // 5.0 x 4.0 m between faces of 200 mm walls: centrelines are 100 mm outside each face.
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 5000, 4000, wallThicknessMm: 200).Snapshot;

            var plan = new FireLayoutEngine().Solve(snapshot);

            var bounds = Assert.Single(plan.Compartments).Boundary.GetBounds();
            Assert.Equal(0.0, bounds.MinX, 0);
            Assert.Equal(5000.0, bounds.MaxX, 0);
            Assert.Equal(0.0, bounds.MinY, 0);
            Assert.Equal(4000.0, bounds.MaxY, 0);
        }

        [Fact]
        public void LayoutOnly_IgnoresTheTileGrid()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 12000, 6000, "1200x600 Acoustic Tile", 1200, 600,
                    tileOrigin: new Point2D(170, 90))
                .Snapshot;
            var profile = RuleProfile.CreateAs1670Default();
            profile.Layout.CoordinateWithCeilingGrid = false;

            var plan = new FireLayoutEngine().Solve(snapshot, profile);

            var ordered = plan.Devices.OrderBy(d => d.Location.X).ToList();
            Assert.Equal(2, ordered.Count);
            Assert.Equal(3000.0, ordered[0].Location.X, 1);
            Assert.Equal(9000.0, ordered[1].Location.X, 1);
            Assert.All(ordered, device =>
            {
                Assert.Equal(3000.0, device.Location.Y, 1);
                Assert.False(device.WasSnappedToTile);
            });
        }

        [Fact]
        public void HeatDetection_AppliesItsOwnSpacingAndFollowsRoomTypeRules()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 16000, 8000)
                .AddRoom("102", "Kitchen", 20000, 0, 4000, 4000)
                .Snapshot;

            var session = new LayoutSession(snapshot);
            var office = session.Plan.Compartments.Single(c => c.DisplayName.Contains("Office"));
            var kitchen = session.Plan.Compartments.Single(c => c.DisplayName.Contains("Kitchen"));

            Assert.Equal(DeviceRole.SmokeDetector, office.DetectorKind);
            Assert.Equal(2, office.ProposedDetectorCount);
            Assert.Equal(DeviceRole.HeatDetector, kitchen.DetectorKind);
            Assert.All(session.GetDevices(kitchen.CompartmentId), d => Assert.Equal(DeviceRole.HeatDetector, d.Role));

            // 7.0 m spacing / 3.5 m wall reach needs a 3 x 2 grid where smoke needed 2 x 1.
            Assert.True(session.SetCompartmentDetectorKind(office.CompartmentId, DeviceRole.HeatDetector));
            Assert.Equal(6, office.ProposedDetectorCount);
            Assert.True(office.Status == CompartmentStatus.Green, Describe(office));
            Assert.All(session.GetDevices(office.CompartmentId), d => Assert.Equal(DeviceRole.HeatDetector, d.Role));
        }

        [Fact]
        public void RoomTypeRules_MatchWholeWordsAndHonourExceptions()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("L1", "Lift Lobby", 0, 0, 4000, 3000)
                .AddRoom("L2", "Lift Shaft", 6000, 0, 2500, 2500)
                .AddRoom("S1", "Uplift Store", 10000, 0, 3000, 3000)
                .Snapshot;

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.Equal(1, plan.Compartments.Single(c => c.DisplayName.Contains("Lobby")).ProposedDetectorCount);
            Assert.Equal(1, plan.Compartments.Single(c => c.DisplayName.Contains("Uplift")).ProposedDetectorCount);
            var shaft = plan.Compartments.Single(c => c.DisplayName.Contains("Shaft"));
            Assert.Equal(CompartmentStatus.Skipped, shaft.Status);
            Assert.False(shaft.IncludeDetectors);
            Assert.DoesNotContain(plan.Devices, d => d.CompartmentId == shaft.CompartmentId);
        }

        [Fact]
        public void ScopeRoomElementIds_LimitsTheAnalysisToSelectedRooms()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 5000, 4000)
                .AddRoom("102", "Meeting", 7000, 0, 5000, 4000)
                .Snapshot;
            snapshot.ScopeRoomElementIds.Add("R-102");

            var plan = new FireLayoutEngine().Solve(snapshot);

            var compartment = Assert.Single(plan.Compartments);
            Assert.Equal("102 Meeting", compartment.DisplayName);
            Assert.All(plan.Devices, d => Assert.Equal(compartment.CompartmentId, d.CompartmentId));
        }

        [Fact]
        public void Regenerate_CyclesThroughDistinctCompliantAlternatives()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 12000, 6000, "1200x600 Acoustic Tile", 1200, 600)
                .Snapshot;
            var session = new LayoutSession(snapshot, PairedProfile());
            var compartment = Assert.Single(session.Plan.Compartments);
            Assert.True(compartment.VariantCount > 1);

            string Signature() => string.Join("|", session.Plan.Devices
                .Select(d => $"{d.Role}:{d.Location.X:F0},{d.Location.Y:F0}")
                .OrderBy(s => s, StringComparer.Ordinal));

            var seen = new List<string>();
            for (int i = 0; i < compartment.VariantCount; i++)
            {
                Assert.Equal(i, compartment.VariantIndex);
                Assert.NotEqual(CompartmentStatus.Red, compartment.Status);
                seen.Add(Signature());
                Assert.True(session.RegenerateCompartment(compartment.CompartmentId));
            }

            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.Equal(0, compartment.VariantIndex);
            Assert.Equal(seen[0], Signature());
        }

        [Fact]
        public void SetCompartmentDevices_ExcludesAndRestoresEachDeviceKind()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 9000, 4800).Snapshot;
            var session = new LayoutSession(snapshot, PairedProfile());
            var compartment = Assert.Single(session.Plan.Compartments);

            session.SetCompartmentDevices(compartment.CompartmentId, includeDetectors: true, includeSpeakers: false);
            var detector = Assert.Single(session.Plan.Devices);
            Assert.Equal(DeviceRole.SmokeDetector, detector.Role);
            Assert.Equal(4500.0, detector.Location.X, 1);

            session.SetCompartmentDevices(compartment.CompartmentId, includeDetectors: false, includeSpeakers: true);
            Assert.Equal(DeviceRole.EwisSpeaker, Assert.Single(session.Plan.Devices).Role);

            session.SetCompartmentDevices(compartment.CompartmentId, includeDetectors: false, includeSpeakers: false);
            Assert.Empty(session.Plan.Devices);
            Assert.Equal(CompartmentStatus.Skipped, compartment.Status);
            Assert.False(compartment.IsApprovedForPlacement);

            session.SetCompartmentDevices(compartment.CompartmentId, includeDetectors: true, includeSpeakers: true);
            Assert.Equal(2, session.Plan.Devices.Count);
            Assert.Equal(CompartmentStatus.Green, compartment.Status);
            Assert.True(compartment.IsApprovedForPlacement);
        }

        [Fact]
        public void SetDeviceExcluded_CancelsThePlacementAndFlagsTheLostCoverage()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 14000, 6000).Snapshot;
            var session = new LayoutSession(snapshot);
            var compartment = Assert.Single(session.Plan.Compartments);
            Assert.Equal(2, session.Plan.Summary.TotalSmokeDetectors);

            string cancelled = session.Plan.Devices[0].DeviceId;
            Assert.True(session.SetDeviceExcluded(cancelled, true));

            Assert.True(session.GetDevice(cancelled)!.IsExcluded);
            Assert.Equal(1, compartment.ProposedDetectorCount);
            Assert.Equal(1, session.Plan.Summary.TotalSmokeDetectors);
            Assert.Equal(1, session.Plan.Summary.ExcludedDeviceCount);
            Assert.Equal(CompartmentStatus.Red, compartment.Status);
            Assert.Contains(compartment.Notes, note => note.Contains("coverage is incomplete"));
            Assert.True(compartment.IsApprovedForPlacement);

            Assert.True(session.SetDeviceExcluded(cancelled, false));
            Assert.Equal(CompartmentStatus.Green, compartment.Status);
            Assert.Equal(2, session.Plan.Summary.TotalSmokeDetectors);
        }

        [Fact]
        public void SetDeviceExcluded_RestoringTheOnlyDevicePutsTheSpaceBackInThePlacement()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 4000, 3000).Snapshot;
            var session = new LayoutSession(snapshot);
            var compartment = Assert.Single(session.Plan.Compartments);
            string only = Assert.Single(session.Plan.Devices).DeviceId;

            session.SetDeviceExcluded(only, true);
            Assert.False(compartment.IsApprovedForPlacement);
            Assert.Equal(0, session.Plan.Summary.TotalSmokeDetectors);

            session.SetDeviceExcluded(only, false);
            Assert.True(compartment.IsApprovedForPlacement);
            Assert.Equal(1, session.Plan.Summary.TotalSmokeDetectors);
        }

        [Fact]
        public void Resolve_KeepsPerSpaceChoicesWithTheRoomTheyWereMadeFor()
        {
            var first = new LayoutSession(new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 4000, 3000).Snapshot);
            first.SetCompartmentDevices(first.Plan.Compartments[0].CompartmentId, includeDetectors: false, includeSpeakers: false);

            // An identical room now sits ahead of 101 in the model.
            var changed = new SnapshotBuilder()
                .AddRoom("100", "Office", -6000, 0, 4000, 3000)
                .AddRoom("101", "Office", 0, 0, 4000, 3000)
                .Snapshot;
            var second = new LayoutSession(changed, null, first);

            Assert.False(second.Plan.Compartments.Single(c => c.DisplayName == "101 Office").IncludeDetectors);
            var added = second.Plan.Compartments.Single(c => c.DisplayName == "100 Office");
            Assert.True(added.IncludeDetectors);
            Assert.Equal(1, added.ProposedDetectorCount);
        }

        [Theory]
        [InlineData(17.0)]
        [InlineData(45.0)]
        [InlineData(71.5)]
        public void RotatedLShapedRoom_IsSolvedLikeAnAxisAlignedOne(double degrees)
        {
            double angle = degrees * Math.PI / 180.0;
            Point2D Turn(double x, double y) => new Point2D(
                x * Math.Cos(angle) - y * Math.Sin(angle), x * Math.Sin(angle) + y * Math.Cos(angle));
            var shape = new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    Turn(0, 0), Turn(13700, 0), Turn(13700, 5300), Turn(6100, 5300), Turn(6100, 11900), Turn(0, 11900)
                }
            };

            var plan = new FireLayoutEngine().Solve(ShapeSnapshot(shape, "13mm Plasterboard"));

            var compartment = Assert.Single(plan.Compartments);
            Assert.True(compartment.Status == CompartmentStatus.Green, Describe(compartment));
            Assert.DoesNotContain("coverage-driven", compartment.LayoutDescription);
        }

        [Fact]
        public void MoveDevice_SnapsToTheNearestTileCentreAndStaysInsideTheSpace()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 7200, 6000, "600x600 Acoustic Tile", 600, 600)
                .Snapshot;
            var session = new LayoutSession(snapshot);
            var compartment = Assert.Single(session.Plan.Compartments);
            var device = Assert.Single(session.Plan.Devices);

            Assert.True(session.MoveDevice(device.DeviceId, new Point2D(2710, 3380), snap: true));
            Assert.Equal(2700.0, device.Location.X, 1);
            Assert.Equal(3300.0, device.Location.Y, 1);
            Assert.True(device.WasSnappedToTile);
            Assert.True(device.IsManual);
            Assert.True(compartment.IsUserModified);
            Assert.Equal(CompartmentStatus.Green, compartment.Status);

            Assert.False(session.MoveDevice(device.DeviceId, new Point2D(-500, 3000), snap: true));
            Assert.Equal(2700.0, device.Location.X, 1);

            // Dragged hard into a corner the single detector no longer reaches the far walls.
            Assert.True(session.MoveDevice(device.DeviceId, new Point2D(900, 900), snap: true));
            Assert.Equal(CompartmentStatus.Red, compartment.Status);
        }

        [Fact]
        public void AddDevice_PlacesAUserDeviceInTheSpaceUnderThePoint()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 9000, 4800).Snapshot;
            var session = new LayoutSession(snapshot);

            var added = session.AddDevice(DeviceRole.EwisSpeaker, new Point2D(2012, 1987), snap: true);

            Assert.NotNull(added);
            Assert.Equal(2000.0, added!.Location.X, 1);
            Assert.Equal(2000.0, added.Location.Y, 1);
            Assert.True(added.IsManual);
            Assert.Equal(1, session.Plan.Summary.TotalEwisSpeakers);
            Assert.Null(session.AddDevice(DeviceRole.EwisSpeaker, new Point2D(50000, 50000), snap: true));
        }

        [Fact]
        public void ImportDevices_ReplacesTheLayoutAndReverifiesItAgainstTheRules()
        {
            var snapshot = new SnapshotBuilder().AddRoom("101", "Office", 0, 0, 14000, 6000).Snapshot;
            var session = new LayoutSession(snapshot);
            var compartment = Assert.Single(session.Plan.Compartments);

            var external = new PlacementPlan();
            external.Devices.Add(new ProposedDevice { Role = DeviceRole.SmokeDetector, Location = new Point2D(2000, 3000) });
            external.Devices.Add(new ProposedDevice { Role = DeviceRole.SmokeDetector, Location = new Point2D(90000, 3000) });

            var result = session.ImportDevices(external);

            Assert.Equal(1, result.ImportedDeviceCount);
            Assert.Equal(1, result.UnassignedDeviceCount);
            var device = Assert.Single(session.Plan.Devices);
            Assert.True(device.IsManual);
            Assert.Equal(2700.0, device.ElevationMm, 1);
            Assert.Equal(CompartmentStatus.Red, compartment.Status);
            Assert.False(compartment.IsApprovedForPlacement);

            // The imported plan replaces the level: a space it leaves empty keeps nothing of the old layout.
            var twoRooms = new LayoutSession(new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 5000, 4000)
                .AddRoom("102", "Meeting", 7000, 0, 5000, 4000)
                .Snapshot);
            var partial = new PlacementPlan();
            partial.Devices.Add(new ProposedDevice { Role = DeviceRole.SmokeDetector, Location = new Point2D(9500, 2000) });
            twoRooms.ImportDevices(partial);
            var emptied = twoRooms.Plan.Compartments.Single(c => c.DisplayName == "101 Office");
            Assert.Empty(twoRooms.GetDevices(emptied.CompartmentId));
            Assert.False(emptied.IsApprovedForPlacement);
            Assert.Equal(1, twoRooms.Plan.Summary.TotalSmokeDetectors);

            // A compliant external layout is accepted as-is.
            external.Devices.Clear();
            external.Devices.Add(new ProposedDevice { Role = DeviceRole.SmokeDetector, Location = new Point2D(3500, 3000) });
            external.Devices.Add(new ProposedDevice { Role = DeviceRole.SmokeDetector, Location = new Point2D(10500, 3000) });
            session.ImportDevices(external);
            Assert.Equal(CompartmentStatus.Green, compartment.Status);
            Assert.True(compartment.IsApprovedForPlacement);
        }

        [Fact]
        public void SnapshotSerializer_RoundTripsARuleProfileWithoutDuplicatingItsKeywordLists()
        {
            var profile = RuleProfile.CreateAs1670Default();
            profile.HeatDetection.MaxDetectorSpacingMm = 6500;

            var restored = SnapshotSerializer.Deserialize<RuleProfile>(SnapshotSerializer.Serialize(profile));

            Assert.Equal(6500.0, restored.HeatDetection.MaxDetectorSpacingMm);
            Assert.Equal(profile.Ceilings.TileKeywords, restored.Ceilings.TileKeywords);
            Assert.Equal(profile.RoomRules.ExcludeKeywords, restored.RoomRules.ExcludeKeywords);
        }

        [Fact]
        public void UpdateCeilingTileGrid_ResolvesTheSpaceOnTheConfirmedGrid()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 7200, 6000, "600x600 Acoustic Tile", 600, 600, originVerified: false)
                .Snapshot;
            var session = new LayoutSession(snapshot);
            var compartment = Assert.Single(session.Plan.Compartments);
            Assert.Equal(CompartmentStatus.Amber, compartment.Status);

            int affected = session.UpdateCeilingTileGrid("C-101", "Host", new TileGridSpec
            {
                OriginMm = new Point2D(300, 0),
                AxisU = new Point2D(1, 0),
                AxisV = new Point2D(0, 1),
                PitchUMm = 600,
                PitchVMm = 600,
                IsOriginVerified = true,
                Source = "UserPicked"
            });

            Assert.Equal(1, affected);
            Assert.True(compartment.Status == CompartmentStatus.Green, Describe(compartment));
            var device = Assert.Single(session.Plan.Devices);
            Assert.Equal(3600.0, device.Location.X, 1);
            Assert.Equal(0.0, device.Location.X % 600.0, 1);
        }
        [Fact]
        public void BranchingCorridor_IsLaidOutAsRegularRowsAlongEachLeg()
        {
            // 40 m corridor with a 13 m branch off its middle: no single grid can see round the corner.
            var shape = new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    new Point2D(0, 0), new Point2D(40000, 0), new Point2D(40000, 2000), new Point2D(21000, 2000),
                    new Point2D(21000, 15000), new Point2D(19000, 15000), new Point2D(19000, 2000), new Point2D(0, 2000)
                }
            };
            var snapshot = ShapeSnapshot(shape, "13mm Plasterboard");

            var plan = new FireLayoutEngine().Solve(snapshot);

            var compartment = Assert.Single(plan.Compartments);
            Assert.True(compartment.Status == CompartmentStatus.Green, Describe(compartment));
            Assert.Contains("bays", compartment.LayoutDescription);
            Assert.Equal(6, plan.Devices.Count);
            Assert.All(plan.Devices, device =>
            {
                bool onMainCentreline = Math.Abs(device.Location.Y - 1000.0) < 1.0;
                bool onBranchCentreline = Math.Abs(device.Location.X - 20000.0) < 1.0;
                Assert.True(onMainCentreline || onBranchCentreline, $"({device.Location.X:F0},{device.Location.Y:F0})");
            });
        }

        [Fact]
        public void OpenPlanAroundACore_StaysOnRegularGridsInsteadOfScatteredPositions()
        {
            var shape = new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    new Point2D(0, 0), new Point2D(31000, 0), new Point2D(31000, 13000),
                    new Point2D(17000, 13000), new Point2D(17000, 27000), new Point2D(0, 27000)
                },
                Holes = new List<List<Point2D>>
                {
                    SnapshotBuilder.Rectangle(6000, 5000, 11000, 9000).OuterRing
                }
            };
            var snapshot = ShapeSnapshot(shape, "13mm Plasterboard");

            var plan = new FireLayoutEngine().Solve(snapshot);

            var openPlan = plan.Compartments.OrderByDescending(c => c.AreaSqM).First();
            Assert.True(openPlan.Status == CompartmentStatus.Green, Describe(openPlan));
            Assert.DoesNotContain("coverage-driven", openPlan.LayoutDescription);
            Assert.InRange(openPlan.WorstCaseWallDistanceMm, 1.0, 5001.0);
        }

        [Fact]
        public void TileSnap_SkipsATileAlreadyOccupiedByALightFitting()
        {
            var snapshot = new SnapshotBuilder()
                .AddRoom("101", "Office", 0, 0, 7200, 6000, "600x600 Acoustic Tile", 600, 600)
                .Snapshot;
            var free = Assert.Single(new FireLayoutEngine().Solve(snapshot).Devices);

            // A 600 x 600 troffer in the tile the detector would otherwise take.
            snapshot.Obstacles.Add(new MepObstacle
            {
                ElementId = "LIGHT-1",
                Category = MepObstacleCategory.LightingFixture,
                TypeName = "600x600 Troffer",
                Center = new Point2D(free.Location.X, free.Location.Y),
                ElevationMm = 2700,
                Bounds = new BoundingBox2D
                {
                    MinX = free.Location.X - 300, MaxX = free.Location.X + 300,
                    MinY = free.Location.Y - 300, MaxY = free.Location.Y + 300
                }
            });
            var plan = new FireLayoutEngine().Solve(snapshot);

            var moved = Assert.Single(plan.Devices);
            Assert.Equal(600.0, moved.Location.DistanceTo(free.Location), 1);
            Assert.True(moved.WasSnappedToTile);
            Assert.False(moved.WasNudgedForObstacle);
            Assert.Equal(CompartmentStatus.Green, Assert.Single(plan.Compartments).Status);
        }

        [Fact]
        public void DeepDownstand_DividesAnExposedSoffitButNotASpaceWithACeilingBelowIt()
        {
            SceneSnapshot Build(bool withCeiling)
            {
                var snapshot = new SceneSnapshot
                {
                    Metadata = new SnapshotMetadata { LevelName = "Level 1", LevelElevationMm = 0, DefaultCeilingHeightMm = 2700 }
                };
                var outline = SnapshotBuilder.Rectangle(0, 0, 16000, 8000).OuterRing;
                for (int i = 0; i < outline.Count; i++)
                {
                    snapshot.Walls.Add(new WallBarrier
                    {
                        Centerline = new Segment2D(outline[i], outline[(i + 1) % outline.Count]),
                        BaseElevationMm = 0,
                        TopElevationMm = 4000
                    });
                }

                // Beam wrap hanging 800 mm below the 4.0 m soffit, across the middle of the room.
                snapshot.Walls.Add(new WallBarrier
                {
                    ElementId = "BEAM-WRAP",
                    Centerline = new Segment2D(new Point2D(8000, 0), new Point2D(8000, 8000)),
                    ThicknessMm = 400,
                    BaseElevationMm = 3200,
                    TopElevationMm = 4000
                });
                snapshot.Soffits.Add(new CeilingSurface
                {
                    ElementId = "SLAB", TypeName = "Concrete Slab", UndersideElevationMm = 4000,
                    Footprint = SnapshotBuilder.Rectangle(0, 0, 16000, 8000)
                });
                if (withCeiling)
                {
                    snapshot.Ceilings.Add(new CeilingSurface
                    {
                        ElementId = "CLG", TypeName = "13mm Plasterboard", UndersideElevationMm = 2700,
                        Footprint = SnapshotBuilder.Rectangle(0, 0, 16000, 8000)
                    });
                }
                else
                {
                    // Keeps the level's reference ceiling height at 2.7 m, as elsewhere on the floor.
                    snapshot.Ceilings.Add(new CeilingSurface
                    {
                        ElementId = "ELSEWHERE", TypeName = "13mm Plasterboard", UndersideElevationMm = 2700,
                        Footprint = SnapshotBuilder.Rectangle(30000, 0, 33000, 3000)
                    });
                }

                return snapshot;
            }

            var exposed = new FireLayoutEngine().Solve(Build(withCeiling: false));
            var pockets = exposed.Compartments.Where(c => c.CeilingKind == CeilingKind.ExposedOrNone).ToList();
            Assert.Equal(2, pockets.Count);
            Assert.All(pockets, pocket => Assert.Equal(1, pocket.ProposedDetectorCount));
            Assert.All(exposed.Devices.Where(d => d.HostCeilingKind == CeilingKind.ExposedOrNone),
                device => Assert.True(Math.Abs(device.Location.X - 8000.0) > 1000.0));

            var ceiled = new FireLayoutEngine().Solve(Build(withCeiling: true));
            var room = Assert.Single(ceiled.Compartments);
            Assert.Equal(CeilingKind.Plasterboard, room.CeilingKind);
        }

        [Fact]
        public void RoomFootprint_KeepsDevicesOffWallsStandingInsideIt()
        {
            // The wall loop is open, so the Room boundary is used - but a full-height partition
            // modelled with no thickness still stands on the room centreline.
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata { LevelName = "Level 1", LevelElevationMm = 0, DefaultCeilingHeightMm = 2700 }
            };
            snapshot.Walls.Add(new WallBarrier
            {
                Centerline = new Segment2D(new Point2D(4000, 0), new Point2D(4000, 4500)),
                BaseElevationMm = 0, TopElevationMm = 3000
            });
            snapshot.Walls.Add(new WallBarrier
            {
                Centerline = new Segment2D(new Point2D(0, 0), new Point2D(8000, 0)),
                BaseElevationMm = 0, TopElevationMm = 3000
            });
            snapshot.Walls.Add(new WallBarrier
            {
                Centerline = new Segment2D(new Point2D(8000, 0), new Point2D(8000, 6000)),
                BaseElevationMm = 0, TopElevationMm = 3000
            });
            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "CLG", TypeName = "13mm Plasterboard", UndersideElevationMm = 2700,
                Footprint = SnapshotBuilder.Rectangle(0, 0, 8000, 6000)
            });
            snapshot.Rooms.Add(new RoomMetadata
            {
                ElementId = "R-1", Number = "101", Name = "Office", AreaSqM = 48,
                LocationPoint = new Point2D(2000, 3000),
                Footprint = SnapshotBuilder.Rectangle(0, 0, 8000, 6000)
            });

            var plan = new FireLayoutEngine().Solve(snapshot);

            Assert.NotEmpty(plan.Devices);
            Assert.All(plan.Devices, device =>
                Assert.False(Math.Abs(device.Location.X - 4000.0) < 300.0 && device.Location.Y < 4500.0,
                    $"device at ({device.Location.X:F0},{device.Location.Y:F0}) is on the partition"));
        }

        private static SceneSnapshot ShapeSnapshot(Polygon2D shape, string ceilingType)
        {
            var snapshot = new SceneSnapshot
            {
                Metadata = new SnapshotMetadata { LevelName = "Level 1", LevelElevationMm = 0, DefaultCeilingHeightMm = 2700 }
            };
            foreach (var ring in new[] { shape.OuterRing }.Concat(shape.Holes))
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    snapshot.Walls.Add(new WallBarrier
                    {
                        Centerline = new Segment2D(ring[i], ring[(i + 1) % ring.Count]),
                        BaseElevationMm = 0,
                        TopElevationMm = 3000
                    });
                }
            }

            snapshot.Ceilings.Add(new CeilingSurface
            {
                ElementId = "CLG",
                TypeName = ceilingType,
                UndersideElevationMm = 2700,
                Footprint = shape
            });
            return snapshot;
        }
    }
}