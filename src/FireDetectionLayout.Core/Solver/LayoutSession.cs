using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;

namespace FireDetectionLayout.Core.Solver
{
    public sealed class PlanImportResult
    {
        public int ImportedDeviceCount { get; set; }
        public int UnassignedDeviceCount { get; set; }
        public int AffectedCompartmentCount { get; set; }
    }

    /// <summary>
    /// A solved level that stays editable. The session owns the <see cref="PlacementPlan"/> and
    /// keeps it consistent while the user regenerates spaces, switches device kinds on or off,
    /// drags, adds or cancels devices - every change is re-verified against the rule profile.
    /// </summary>
    public sealed class LayoutSession
    {
        private const double OverlapDistanceMm = 250.0;
        private const double FreeSnapIncrementMm = 50.0;

        private readonly WallGraphPolygonizer _polygonizer = new WallGraphPolygonizer();
        private readonly CeilingClassifier _ceilingClassifier = new CeilingClassifier();
        private readonly List<CompartmentContext> _contexts = new List<CompartmentContext>();
        private readonly Dictionary<string, CompartmentContext> _byId =
            new Dictionary<string, CompartmentContext>(StringComparer.Ordinal);
        private double _referenceCeilingElevationMm;

        public LayoutSession(SceneSnapshot snapshot, RuleProfile? profile = null, LayoutSession? previous = null)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Profile = profile ?? RuleProfile.CreateAs1670Default();
            Plan = new PlacementPlan
            {
                ProfileName = Profile.ProfileName,
                LevelName = snapshot.Metadata.LevelName,
                LevelElementId = snapshot.Metadata.LevelElementId
            };

            DiscoverCompartments();
            if (previous != null)
                CarryOverUserChoices(previous);
            SolveAll(resetVariants: false);
        }

        public SceneSnapshot Snapshot { get; }
        public RuleProfile Profile { get; }
        public PlacementPlan Plan { get; }

        // ------------------------------------------------------------------ queries

        public SmokeCompartment? GetCompartment(string compartmentId) =>
            _byId.TryGetValue(compartmentId, out var ctx) ? ctx.Compartment : null;

        public IReadOnlyList<ProposedDevice> GetDevices(string compartmentId) =>
            _byId.TryGetValue(compartmentId, out var ctx)
                ? (IReadOnlyList<ProposedDevice>)ctx.Devices.ToList()
                : Array.Empty<ProposedDevice>();

        public ProposedDevice? GetDevice(string deviceId) =>
            Plan.Devices.FirstOrDefault(d => d.DeviceId == deviceId);

        /// <summary>Space containing a model point, or null when the point is outside every space.</summary>
        public SmokeCompartment? FindCompartmentAt(Point2D point)
        {
            foreach (var ctx in _contexts)
            {
                if (ctx.Covers(point))
                    return ctx.Compartment;
            }

            return null;
        }

        /// <summary>Spacing requirements currently applied to one device kind.</summary>
        public DeviceSpacing GetSpacing(DeviceRole role) => Profile.GetSpacing(role);

        // ------------------------------------------------------------------ whole-level actions

        /// <summary>Steps every space to its next layout alternative and discards manual edits.</summary>
        public void RegenerateAll()
        {
            foreach (var ctx in _contexts)
                AdvanceVariant(ctx);
            SolveAll(resetVariants: false);
        }

        /// <summary>Returns every space to its preferred layout and discards manual edits.</summary>
        public void ResetAll() => SolveAll(resetVariants: true);

        // ------------------------------------------------------------------ per-space actions

        /// <summary>Shows the next layout alternative for one space, discarding manual edits in it.</summary>
        public bool RegenerateCompartment(string compartmentId)
        {
            if (!_byId.TryGetValue(compartmentId, out var ctx))
                return false;

            AdvanceVariant(ctx);
            SolveCompartment(ctx);
            RebuildPlan();
            return true;
        }

        public bool ResetCompartment(string compartmentId)
        {
            if (!_byId.TryGetValue(compartmentId, out var ctx))
                return false;

            ctx.Compartment.VariantIndex = 0;
            SolveCompartment(ctx);
            RebuildPlan();
            return true;
        }

        /// <summary>Includes or excludes detectors and speakers for one space and re-solves it.</summary>
        public bool SetCompartmentDevices(string compartmentId, bool includeDetectors, bool includeSpeakers)
        {
            if (!_byId.TryGetValue(compartmentId, out var ctx))
                return false;

            ctx.HasUserDeviceOverride = true;
            ctx.Compartment.IncludeDetectors = includeDetectors;
            ctx.Compartment.IncludeSpeakers = includeSpeakers;
            ctx.Compartment.VariantIndex = 0;
            SolveCompartment(ctx);
            RebuildPlan();
            return true;
        }

        /// <summary>Switches one space between smoke and heat detection and re-solves it.</summary>
        public bool SetCompartmentDetectorKind(string compartmentId, DeviceRole detectorKind)
        {
            if (!detectorKind.IsDetector() || !_byId.TryGetValue(compartmentId, out var ctx))
                return false;

            ctx.HasUserDetectorKindOverride = true;
            ctx.Compartment.DetectorKind = detectorKind;
            ctx.Compartment.VariantIndex = 0;
            SolveCompartment(ctx);
            RebuildPlan();
            return true;
        }

        public bool SetCompartmentApproved(string compartmentId, bool approved)
        {
            if (!_byId.TryGetValue(compartmentId, out var ctx))
                return false;

            ctx.Compartment.IsApprovedForPlacement = approved && ctx.Devices.Any(d => !d.IsExcluded);
            BuildSummary();
            return true;
        }

        // ------------------------------------------------------------------ per-device actions

        /// <summary>Cancels (or restores) a single device. Cancelled devices are never placed.</summary>
        public bool SetDeviceExcluded(string deviceId, bool excluded)
        {
            if (!TryFindDevice(deviceId, out var ctx, out var device))
                return false;

            bool hadLiveDevices = ctx.Devices.Any(d => !d.IsExcluded);
            device.IsExcluded = excluded;
            ctx.Compartment.IsUserModified = true;
            Verify(ctx, autoApprove: false);

            // Cancelling the last device takes the space out of the placement; restoring one puts it back.
            if (!excluded && !hadLiveDevices && ctx.Compartment.Status != CompartmentStatus.Red)
                ctx.Compartment.IsApprovedForPlacement = true;

            BuildSummary();
            return true;
        }

        /// <summary>
        /// Moves a device within its space. With <paramref name="snap"/> the position locks to the
        /// nearest tile centre on a tile ceiling, or to a 50 mm increment elsewhere.
        /// </summary>
        public bool MoveDevice(string deviceId, Point2D target, bool snap)
        {
            if (!TryFindDevice(deviceId, out var ctx, out var device))
                return false;

            var location = snap ? SnapPoint(ctx, target) : target;
            if (!ctx.Covers(location))
            {
                if (!ctx.Covers(target))
                    return false;
                location = target;
            }

            device.Location = location;
            device.IsManual = true;
            RefreshDeviceHosting(ctx, device, "Moved by user.");
            ctx.Compartment.IsUserModified = true;
            Verify(ctx, autoApprove: false);
            BuildSummary();
            return true;
        }

        /// <summary>Adds a device at a model point; returns null when the point is outside every space.</summary>
        public ProposedDevice? AddDevice(DeviceRole role, Point2D target, bool snap)
        {
            var ctx = _contexts.FirstOrDefault(c => c.Covers(target));
            if (ctx == null || ctx.IsWithheld)
                return null;

            var location = snap ? SnapPoint(ctx, target) : target;
            if (!ctx.Covers(location))
                location = target;

            var device = new ProposedDevice
            {
                CompartmentId = ctx.Compartment.CompartmentId,
                Role = role,
                Location = location,
                IsManual = true,
                RotationDegrees = ctx.Frame.AngleDegrees
            };
            RefreshDeviceHosting(ctx, device, "Added by user.");
            ctx.Devices.Add(device);
            ctx.Compartment.IsUserModified = true;
            if (ctx.Compartment.Status == CompartmentStatus.Skipped)
                ctx.Compartment.IsApprovedForPlacement = true;
            Verify(ctx, autoApprove: false);
            RebuildPlan();
            return device;
        }

        /// <summary>Nearest position a device would lock to (tile centre or 50 mm increment).</summary>
        public Point2D SnapPoint(Point2D target)
        {
            var ctx = _contexts.FirstOrDefault(c => c.Covers(target));
            return ctx == null ? target : SnapPoint(ctx, target);
        }

        // ------------------------------------------------------------------ external data

        /// <summary>
        /// Replaces the proposed devices with the positions of an externally assessed plan.
        /// Each device is assigned to the space that contains it and every affected space is
        /// re-verified against the rule profile.
        /// </summary>
        public PlanImportResult ImportDevices(PlacementPlan external)
        {
            if (external == null)
                throw new ArgumentNullException(nameof(external));

            var result = new PlanImportResult();

            // The imported plan replaces the whole level: a space it leaves empty ends up empty.
            var incoming = new Dictionary<CompartmentContext, List<ProposedDevice>>();
            foreach (var ctx in _contexts)
            {
                if (!ctx.IsWithheld)
                    incoming[ctx] = new List<ProposedDevice>();
            }

            foreach (var source in external.Devices)
            {
                var ctx = _contexts.FirstOrDefault(c => !c.IsWithheld && c.Covers(source.Location));
                if (ctx == null)
                {
                    result.UnassignedDeviceCount++;
                    continue;
                }

                var device = new ProposedDevice
                {
                    CompartmentId = ctx.Compartment.CompartmentId,
                    Role = source.Role,
                    Location = new Point2D(source.Location.X, source.Location.Y),
                    PairId = source.PairId,
                    IsExcluded = source.IsExcluded,
                    IsManual = true,
                    RotationDegrees = source.RotationDegrees
                };
                RefreshDeviceHosting(ctx, device, "Imported from external assessment.");

                incoming[ctx].Add(device);
                result.ImportedDeviceCount++;
            }

            foreach (var entry in incoming)
            {
                var ctx = entry.Key;
                var compartment = ctx.Compartment;

                // A plan reviewed with this tool also carries the reviewer's per-space choices.
                var reviewed = external.Compartments.FirstOrDefault(c =>
                    compartment.RoomElementIds.Count > 0
                        ? c.RoomElementIds.Any(compartment.RoomElementIds.Contains)
                        : c.CompartmentId == compartment.CompartmentId &&
                          Math.Abs(c.AreaSqM - compartment.AreaSqM) <= 0.05);
                if (reviewed != null)
                {
                    ctx.HasUserDeviceOverride = true;
                    compartment.IncludeDetectors = reviewed.IncludeDetectors;
                    compartment.IncludeSpeakers = reviewed.IncludeSpeakers;
                    if (reviewed.DetectorKind.IsDetector())
                    {
                        ctx.HasUserDetectorKindOverride = true;
                        compartment.DetectorKind = reviewed.DetectorKind;
                    }
                }

                ctx.Devices.Clear();
                ctx.Devices.AddRange(entry.Value);
                ctx.CurrentOption = null;
                ctx.CurrentSpeakerOption = null;
                ctx.SolveError = null;
                compartment.IsUserModified = true;
                compartment.VariantCount = 0;
                compartment.LayoutDescription = "imported from external assessment";
                Verify(ctx, autoApprove: true);
                if (reviewed != null && !reviewed.IsApprovedForPlacement)
                    compartment.IsApprovedForPlacement = false;
            }

            result.AffectedCompartmentCount = incoming.Count(entry => entry.Value.Count > 0);
            if (result.UnassignedDeviceCount > 0)
            {
                Plan.Warnings.Add(
                    $"{result.UnassignedDeviceCount} imported device(s) were outside every analysed space and were ignored.");
            }

            RebuildPlan();
            return result;
        }

        /// <summary>
        /// Replaces the tile grid of one ceiling (for example after the user picks the real grid
        /// origin in Revit) and re-solves the spaces under that ceiling.
        /// </summary>
        public int UpdateCeilingTileGrid(string ceilingElementId, string sourceModel, TileGridSpec grid)
        {
            var ceiling = Snapshot.Ceilings.FirstOrDefault(c =>
                c.ElementId == ceilingElementId &&
                string.Equals(c.SourceModel, sourceModel, StringComparison.OrdinalIgnoreCase));
            if (ceiling == null)
                return 0;

            ceiling.TileGrid = grid;
            int affected = 0;
            foreach (var ctx in _contexts)
            {
                if (!ctx.Ceiling.OverlappingCeilings.Any(o => ReferenceEquals(o.Ceiling, ceiling)))
                    continue;

                AnalyseCeiling(ctx);
                SolveCompartment(ctx);
                affected++;
            }

            RebuildPlan();
            return affected;
        }

        // ------------------------------------------------------------------ discovery

        private void DiscoverCompartments()
        {
            var metadata = Snapshot.Metadata;
            _referenceCeilingElevationMm = Snapshot.Ceilings.Count > 0
                ? Snapshot.Ceilings.Average(c => c.UndersideElevationMm)
                : Snapshot.Soffits.Count > 0
                    ? Snapshot.Soffits.Average(s => s.UndersideElevationMm)
                    : metadata.LevelElevationMm + metadata.DefaultCeilingHeightMm;

            // Stage 1: physical compartments from walls and ceiling-level downstands.
            var wallResult = _polygonizer.BuildCompartments(
                Snapshot.Walls, metadata.LevelElevationMm, _referenceCeilingElevationMm, Profile.SmokeDetection,
                Snapshot.Ceilings);
            Plan.UnresolvedOpenEdges.AddRange(wallResult.DanglingEdges);
            Plan.Warnings.AddRange(Snapshot.Diagnostics);

            // Stage 1B: recover Rooms, MEP Spaces and ceilings the wall graph could not close.
            var discovered = CompartmentDiscovery.Discover(wallResult, Snapshot, Profile.SmokeDetection, Plan.Warnings);
            var scope = new HashSet<string>(Snapshot.ScopeRoomElementIds, StringComparer.Ordinal);

            int index = 0;
            foreach (var entry in discovered)
            {
                index++;
                var compartment = new SmokeCompartment
                {
                    CompartmentId = $"COMP-{index:D3}",
                    SourceKind = entry.SourceKind,
                    Boundary = entry.Boundary,
                    AreaSqM = Math.Round(entry.Geometry.Area / 1_000_000.0, 2)
                };
                CompartmentDiscovery.AttachRoomMetadata(
                    compartment, entry.Geometry, Snapshot.Spaces, Snapshot.Rooms, entry.SourceRoom);

                if (scope.Count > 0 && !compartment.RoomElementIds.Any(scope.Contains))
                    continue;

                var ctx = new CompartmentContext(compartment, entry.Geometry)
                {
                    SourceRoom = entry.SourceRoom,
                    DiscoveryNote = entry.DiscoveryNote
                };
                AnalyseCeiling(ctx);
                ApplyRoomRules(ctx);

                _contexts.Add(ctx);
                _byId[compartment.CompartmentId] = ctx;
                Plan.Compartments.Add(compartment);
            }
        }

        /// <summary>Stage 3A: classify the ceiling over a space and prepare its layout frame.</summary>
        private void AnalyseCeiling(CompartmentContext ctx)
        {
            var compartment = ctx.Compartment;
            var metadata = Snapshot.Metadata;
            double fallbackElevationMm = ctx.SourceRoom != null && ctx.SourceRoom.UnboundedHeightMm >= 1800.0
                ? metadata.LevelElevationMm + ctx.SourceRoom.UnboundedHeightMm
                : _referenceCeilingElevationMm;

            var analysis = _ceilingClassifier.AnalyzeCompartment(
                compartment.Boundary, Snapshot.Ceilings, fallbackElevationMm, Profile.Ceilings, Snapshot.Soffits);
            ctx.Ceiling = analysis;
            compartment.CeilingKind = analysis.Kind;
            compartment.CeilingElevationMm = Math.Round(analysis.DominantElevationMm, 1);

            ctx.BaseNotes.Clear();
            ctx.BaseStatus = CompartmentStatus.Green;
            ctx.IsWithheld = false;
            if (!string.IsNullOrWhiteSpace(ctx.DiscoveryNote))
                ctx.BaseNotes.Add(ctx.DiscoveryNote!);
            ctx.BaseNotes.AddRange(analysis.Notes);

            double heightMm = compartment.CeilingElevationMm - metadata.LevelElevationMm;
            if (heightMm > Profile.SmokeDetection.StandardFlatCeilingMaxHeightMm)
            {
                ctx.BaseStatus = CompartmentStatus.Amber;
                ctx.BaseNotes.Add(
                    $"Ceiling height ({heightMm:F0} mm) exceeds standard flat-ceiling limit; verify AS 1670.1 derating.");
            }

            var existing = Snapshot.ExistingDevices.Where(d => ctx.Covers(d.Location)).ToList();
            int foreign = existing.Count(d => !d.IsToolOwned);
            if (foreign > 0)
            {
                ctx.BaseStatus = CompartmentStatus.Amber;
                ctx.BaseNotes.Add($"{foreign} existing fire device(s) already in compartment.");
            }

            if (existing.Count > foreign)
            {
                ctx.BaseNotes.Add(
                    $"{existing.Count - foreign} device(s) from an earlier run of this tool are in this space.");
            }

            if (analysis.Kind == CeilingKind.ExposedOrNone && ctx.BaseStatus == CompartmentStatus.Green)
                ctx.BaseStatus = CompartmentStatus.Amber;

            // Sloped ceilings and unreadable geometry are never laid out automatically.
            if (analysis.Kind == CeilingKind.Sloped || analysis.Kind == CeilingKind.Unknown || analysis.HasGeometryError)
            {
                ctx.BaseStatus = CompartmentStatus.Red;
                ctx.IsWithheld = true;
            }

            ctx.CeilingZones.Clear();
            ctx.TileZones.Clear();
            if (!ctx.IsWithheld && analysis.Kind != CeilingKind.ExposedOrNone)
            {
                bool gridlessTile = false;
                foreach (var overlap in analysis.OverlappingCeilings)
                {
                    var footprint = WallGraphPolygonizer.TryToValidNtsPolygon(overlap.Ceiling.Footprint);
                    if (footprint == null)
                        continue;

                    if (overlap.HasTileGrid && Profile.Layout.CoordinateWithCeilingGrid)
                    {
                        var zone = new TileZone(overlap.Ceiling, footprint, overlap.Ceiling.TileGrid!);
                        ctx.TileZones.Add(zone);
                        ctx.CeilingZones.Add(zone);
                    }
                    else
                    {
                        ctx.CeilingZones.Add(new CeilingZone(overlap.Ceiling, overlap.Kind, footprint));
                        gridlessTile |= overlap.Kind == CeilingKind.SuspendedTile && !overlap.HasTileGrid;
                    }
                }

                if (gridlessTile && Profile.Layout.CoordinateWithCeilingGrid)
                {
                    ctx.BaseNotes.Add(
                        "Tile ceiling has no readable grid (no model surface pattern); laid out on the room grid without tile snapping.");
                }
            }

            // Fixtures in this ceiling that a device cannot share a tile with.
            ctx.FixtureBoxes.Clear();
            var spaceBounds = compartment.Boundary.GetBounds();
            foreach (var obstacle in Snapshot.Obstacles)
            {
                if (obstacle.Category == MepObstacleCategory.Sprinkler ||
                    !spaceBounds.Intersects(obstacle.Bounds, 50.0))
                    continue;
                if (Math.Abs(obstacle.ElevationMm - compartment.CeilingElevationMm) <= 750.0)
                    ctx.FixtureBoxes.Add(obstacle.Bounds);
            }

            var primaryTile = ctx.TileZones.Count > 0 ? ctx.TileZones[0] : null;
            compartment.TileSize = primaryTile?.SizeLabel ?? string.Empty;
            compartment.TileGrid = primaryTile?.Grid;

            ctx.Frame = RoomFrame.Compute(
                compartment.Boundary,
                analysis.Kind == CeilingKind.SuspendedTile ? primaryTile?.Grid : null);

            double tightestReach = new[] { DeviceRole.SmokeDetector, DeviceRole.HeatDetector, DeviceRole.EwisSpeaker }
                .Min(role => Profile.GetSpacing(role).MaxWallDistanceMm);
            ctx.TargetStepMm = Math.Max(500.0, Math.Min(1500.0, tightestReach * 0.3));
            ctx.TargetEdgeInsetMm = Math.Min(350.0, Math.Max(100.0, Profile.SmokeDetection.HardMinWallClearanceMm * 0.8));
            ctx.Targets = CoverageTargets.Build(ctx, ctx.TargetStepMm, ctx.TargetEdgeInsetMm);
            ctx.Options = null;
        }

        private void ApplyRoomRules(CompartmentContext ctx)
        {
            var compartment = ctx.Compartment;
            var rules = Profile.RoomRules;
            string names = string.Join(" ", compartment.RoomNames);

            bool excluded = RoomTypeRules.MatchesAny(names, rules.ExcludeKeywords) &&
                            !RoomTypeRules.MatchesAny(names, rules.ExcludeExceptionKeywords);
            bool detectorOnly = RoomTypeRules.MatchesAny(names, rules.DetectorOnlyKeywords);
            bool speakerOnly = RoomTypeRules.MatchesAny(names, rules.SpeakerOnlyKeywords);
            bool heat = RoomTypeRules.MatchesAny(names, Profile.HeatDetection.RoomKeywords);

            ctx.RuleNotes.Clear();
            ctx.DefaultIncludeDetectors = !excluded && !(speakerOnly && !detectorOnly);
            ctx.DefaultIncludeSpeakers = !excluded && !(detectorOnly && !speakerOnly);
            ctx.DefaultDetectorKind = heat ? DeviceRole.HeatDetector : Profile.Layout.DefaultDetectorKind;

            if (excluded)
                ctx.RuleNotes.Add("Excluded by room-type rule.");
            else if (!ctx.DefaultIncludeDetectors)
                ctx.RuleNotes.Add("Detectors omitted by room-type rule (speaker only).");
            else if (!ctx.DefaultIncludeSpeakers && Profile.Layout.PlaceSpeakers)
                ctx.RuleNotes.Add("Speakers omitted by room-type rule (detector only).");

            if (heat && Profile.Layout.DefaultDetectorKind != DeviceRole.HeatDetector)
                ctx.RuleNotes.Add("Heat detection assigned by room-type rule.");

            if (!ctx.HasUserDeviceOverride)
            {
                compartment.IncludeDetectors = ctx.DefaultIncludeDetectors;
                compartment.IncludeSpeakers = ctx.DefaultIncludeSpeakers;
            }

            if (!ctx.HasUserDetectorKindOverride)
                compartment.DetectorKind = ctx.DefaultDetectorKind;
        }

        /// <summary>Keeps the user's per-space choices when the same level is solved again.</summary>
        private void CarryOverUserChoices(LayoutSession previous)
        {
            var overridden = previous._contexts
                .Where(c => c.HasUserDeviceOverride || c.HasUserDetectorKindOverride)
                .ToList();
            if (overridden.Count == 0)
                return;

            foreach (var ctx in _contexts)
            {
                // Matched on what the space is, not on its position in the list: by the Rooms /
                // Spaces inside it, or for an unnamed space by where it is and how big it is.
                var compartment = ctx.Compartment;
                var centre = ctx.Geometry.Centroid;
                var old = compartment.RoomElementIds.Count > 0
                    ? overridden.FirstOrDefault(c => c.Compartment.RoomElementIds.Any(compartment.RoomElementIds.Contains))
                    : overridden.FirstOrDefault(c =>
                        c.Compartment.RoomElementIds.Count == 0 &&
                        Math.Abs(c.Compartment.AreaSqM - compartment.AreaSqM) <= 0.05 &&
                        c.Geometry.Centroid.Distance(centre) <= 250.0);
                if (old == null)
                    continue;

                if (old.HasUserDeviceOverride)
                {
                    ctx.HasUserDeviceOverride = true;
                    ctx.Compartment.IncludeDetectors = old.Compartment.IncludeDetectors;
                    ctx.Compartment.IncludeSpeakers = old.Compartment.IncludeSpeakers;
                }

                if (old.HasUserDetectorKindOverride)
                {
                    ctx.HasUserDetectorKindOverride = true;
                    ctx.Compartment.DetectorKind = old.Compartment.DetectorKind;
                }
            }
        }

        // ------------------------------------------------------------------ solving

        private bool WantsDetectors(CompartmentContext ctx) =>
            Profile.Layout.PlaceDetectors && ctx.Compartment.IncludeDetectors;

        private bool WantsSpeakers(CompartmentContext ctx) =>
            Profile.Layout.PlaceSpeakers && ctx.Compartment.IncludeSpeakers;

        private static void AdvanceVariant(CompartmentContext ctx)
        {
            var compartment = ctx.Compartment;
            compartment.VariantIndex = compartment.VariantCount > 1
                ? (compartment.VariantIndex + 1) % compartment.VariantCount
                : 0;
        }

        private void SolveAll(bool resetVariants)
        {
            if (resetVariants)
            {
                foreach (var ctx in _contexts)
                    ctx.Compartment.VariantIndex = 0;
            }

            // Spaces are independent, so a whole level solves in parallel.
            Parallel.ForEach(_contexts, SolveCompartment);
            RebuildPlan();
        }

        private void SolveCompartment(CompartmentContext ctx)
        {
            var compartment = ctx.Compartment;
            ctx.Devices.Clear();
            ctx.CurrentOption = null;
            ctx.CurrentSpeakerOption = null;
            ctx.SolveError = null;
            compartment.IsUserModified = false;
            compartment.LayoutDescription = string.Empty;
            compartment.VariantCount = 0;

            try
            {
                bool detectors = WantsDetectors(ctx);
                bool speakers = WantsSpeakers(ctx);
                if (!ctx.IsWithheld && (detectors || speakers))
                    BuildLayout(ctx, detectors, speakers);
            }
            catch (Exception error) when (error is TopologyException || error is ArgumentException ||
                                          error is InvalidOperationException)
            {
                ctx.Devices.Clear();
                ctx.CurrentOption = null;
                ctx.SolveError = $"Layout could not be solved for this space: {error.Message}";
            }

            Verify(ctx, autoApprove: true);
        }

        /// <summary>Stage 2 and 3B: choose a grid layout and turn its nodes into hosted devices.</summary>
        private void BuildLayout(CompartmentContext ctx, bool detectors, bool speakers)
        {
            var compartment = ctx.Compartment;
            bool independent = detectors && speakers &&
                               Profile.Ewis.LayoutMode == SpeakerLayoutMode.IndependentGrid;
            string signature = $"{detectors}|{speakers}|{compartment.DetectorKind}|{independent}";
            if (ctx.Options == null || ctx.OptionsSignature != signature)
            {
                var content = independent || !speakers
                    ? NodeContent.DetectorOnly
                    : detectors ? NodeContent.Pair : NodeContent.SpeakerOnly;
                ctx.Options = GridLayoutSolver.BuildOptions(CreateRequest(ctx, content));
                ctx.SpeakerOptions = independent
                    ? GridLayoutSolver.BuildOptions(CreateRequest(ctx, NodeContent.SpeakerOnly))
                    : null;
                ctx.OptionsSignature = signature;
            }

            int primaryCount = ctx.Options.Count;
            int speakerCount = ctx.SpeakerOptions?.Count ?? 0;
            compartment.VariantCount = Math.Max(primaryCount, speakerCount);
            if (compartment.VariantCount == 0)
                return;

            int variant = compartment.VariantIndex % compartment.VariantCount;
            compartment.VariantIndex = variant;

            var descriptions = new List<string>();
            if (primaryCount > 0)
            {
                var option = ctx.Options[variant % primaryCount];
                ctx.CurrentOption = option;
                descriptions.Add(option.Description);
                foreach (var node in option.Nodes)
                {
                    string? pairId = node.Detector != null && node.Speaker != null ? NewId() : null;
                    if (node.Detector != null)
                        ctx.Devices.Add(CreateDevice(ctx, compartment.DetectorKind, node.Detector, pairId));
                    if (node.Speaker != null)
                        ctx.Devices.Add(CreateDevice(ctx, DeviceRole.EwisSpeaker, node.Speaker, pairId));
                }
            }

            if (independent && speakerCount > 0)
            {
                var option = ctx.SpeakerOptions![variant % speakerCount];
                ctx.CurrentSpeakerOption = option;
                descriptions.Add("speakers: " + option.Description);
                foreach (var node in option.Nodes)
                {
                    if (node.Speaker != null)
                        ctx.Devices.Add(CreateDevice(ctx, DeviceRole.EwisSpeaker, node.Speaker, null));
                }

                SeparateCoincidentDevices(ctx);
            }
            else
            {
                ctx.CurrentSpeakerOption = null;
            }

            compartment.LayoutDescription = string.Join("; ", descriptions);
        }

        private SolveRequest CreateRequest(CompartmentContext ctx, NodeContent content) => new SolveRequest
        {
            Ctx = ctx,
            Content = content,
            DetectorSpec = Profile.GetSpacing(ctx.Compartment.DetectorKind),
            SpeakerSpec = Profile.GetSpacing(DeviceRole.EwisSpeaker),
            PairSeparationMm = Math.Max(200.0, Profile.Ewis.PairedDeviceOffsetMm),
            UseTileGrid = Profile.Layout.CoordinateWithCeilingGrid,
            PreferFullTiles = Profile.Layout.PreferFullTiles
        };

        /// <summary>
        /// Independent detector and speaker grids can land on the same spot. One of the two is then
        /// shifted beside the other - whichever move keeps its own coverage compliant - so they read
        /// as a deliberate pair.
        /// </summary>
        private void SeparateCoincidentDevices(CompartmentContext ctx)
        {
            double separation = Math.Max(200.0, Profile.Ewis.PairedDeviceOffsetMm);
            var detectorSpec = Profile.GetSpacing(ctx.Compartment.DetectorKind);
            var speakerSpec = Profile.GetSpacing(DeviceRole.EwisSpeaker);
            var detectors = ctx.Devices.Where(d => d.Role.IsDetector()).ToList();
            var speakers = ctx.Devices.Where(d => d.Role == DeviceRole.EwisSpeaker).ToList();

            foreach (var speaker in speakers)
            {
                var detector = detectors
                    .OrderBy(d => d.Location.DistanceTo(speaker.Location))
                    .FirstOrDefault();
                if (detector == null || detector.Location.DistanceTo(speaker.Location) >= separation * 0.75)
                    continue;

                if (TryRelocateBeside(ctx, speaker, detector.Location, separation, speakers, speakerSpec) ||
                    TryRelocateBeside(ctx, detector, speaker.Location, separation, detectors, detectorSpec))
                {
                    string pairId = detector.PairId ?? NewId();
                    detector.PairId = pairId;
                    speaker.PairId = pairId;
                }
            }
        }

        private bool TryRelocateBeside(
            CompartmentContext ctx,
            ProposedDevice moving,
            Point2D anchor,
            double separation,
            IReadOnlyList<ProposedDevice> sameKind,
            DeviceSpacing spec)
        {
            // On a tile ceiling whole-tile steps keep the moved device on a tile centre.
            var zone = FindTileModule(ctx, anchor);
            var steps = new List<(Point2D Axis, double Distance)>();
            if (zone != null)
            {
                steps.Add((zone.AxisU, Math.Max(1.0, Math.Round(separation / zone.PitchU)) * zone.PitchU));
                steps.Add((zone.AxisV, Math.Max(1.0, Math.Round(separation / zone.PitchV)) * zone.PitchV));
                steps.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            }
            else
            {
                steps.Add((ctx.Frame.LongAxis, separation));
                steps.Add((ctx.Frame.ShortAxis, separation));
            }

            var original = moving.Location;
            foreach (double requiredClearance in new[] { spec.MinWallClearanceMm, spec.HardMinWallClearanceMm })
            {
                foreach (var (axis, distance) in steps)
                {
                    foreach (double sign in new[] { 1.0, -1.0 })
                    {
                        var candidate = new Point2D(
                            anchor.X + axis.X * distance * sign,
                            anchor.Y + axis.Y * distance * sign);
                        if (!ctx.Covers(candidate) || ctx.BoundaryDistance(candidate) < requiredClearance - 1e-6)
                            continue;
                        if (zone != null && !zone.Contains(candidate))
                            continue;

                        moving.Location = candidate;
                        var positions = sameKind.Select(d => d.Location).ToList();
                        bool compliant =
                            GridLayoutSolver.CountUncovered(ctx, positions, spec, out _, stopAtFirstGap: true) == 0 &&
                            GridLayoutSolver.MaxNearestNeighbourSpacing(positions) <=
                            spec.MaxSpacingMm + GridLayoutSolver.ReachToleranceMm;
                        if (compliant)
                        {
                            RefreshDeviceHosting(ctx, moving, null);
                            return true;
                        }

                        moving.Location = original;
                    }
                }
            }

            return false;
        }
        // ------------------------------------------------------------------ devices

        private ProposedDevice CreateDevice(CompartmentContext ctx, DeviceRole role, Point2D location, string? pairId)
        {
            var device = new ProposedDevice
            {
                CompartmentId = ctx.Compartment.CompartmentId,
                Role = role,
                Location = new Point2D(location.X, location.Y),
                PairId = pairId
            };
            RefreshDeviceHosting(ctx, device, null);
            return device;
        }

        /// <summary>Stage 3B: resolve the ceiling a device hangs from, its height and any MEP clash.</summary>
        private void RefreshDeviceHosting(CompartmentContext ctx, ProposedDevice device, string? leadingNote)
        {
            var compartment = ctx.Compartment;
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(leadingNote))
                notes.Add(leadingNote!);

            var tileZone = FindTileModule(ctx, device.Location);
            device.WasSnappedToTile = tileZone != null;
            device.RotationDegrees = ctx.Frame.AngleDegrees;

            if (tileZone != null)
            {
                device.HostCeilingElementId = tileZone.Ceiling.ElementId;
                device.HostCeilingKind = CeilingKind.SuspendedTile;
                device.ElevationMm = Math.Round(tileZone.Ceiling.UndersideElevationMm, 1);
                notes.Add(tileZone.Grid.IsOriginVerified
                    ? "On tile centre."
                    : "On pattern-grid tile centre (origin unverified - confirm).");
            }
            else if (compartment.CeilingKind == CeilingKind.ExposedOrNone)
            {
                device.HostCeilingElementId = ctx.Ceiling.PrimarySoffit?.ElementId;
                device.HostCeilingKind = CeilingKind.ExposedOrNone;
                device.ElevationMm = compartment.CeilingElevationMm;
                notes.Add(ctx.Ceiling.PrimarySoffit != null
                    ? "Placed on exposed soffit (no ceiling) - confirm mounting."
                    : "Placed at fallback elevation (no ceiling/soffit) - confirm mounting.");
            }
            else
            {
                // Under several ceilings (bulkhead below a higher ceiling) the lowest one hosts the device.
                CeilingZone? host = null;
                foreach (var zone in ctx.CeilingZones)
                {
                    if (zone.Contains(device.Location) &&
                        (host == null || zone.Ceiling.UndersideElevationMm < host.Ceiling.UndersideElevationMm))
                        host = zone;
                }

                device.HostCeilingElementId = host?.Ceiling.ElementId ?? ctx.Ceiling.PrimaryCeiling?.ElementId;
                device.HostCeilingKind = CeilingKind.Plasterboard;
                device.ElevationMm = host != null
                    ? Math.Round(host.Ceiling.UndersideElevationMm, 1)
                    : compartment.CeilingElevationMm;
                notes.Add(ctx.TileZones.Count > 0
                    ? "Placed off the tile field to maintain wall reach."
                    : "Aligned to room grid.");
            }

            device.WasNudgedForObstacle = HasObstacleConflict(device, out string obstacleNote);
            if (device.WasNudgedForObstacle)
                notes.Add(obstacleNote);

            device.Notes = string.Join(" ", notes);
        }

        /// <summary>Tile zone whose module centre (tile centre or half-tile centre) the point sits on.</summary>
        private static TileZone? FindTileModule(CompartmentContext ctx, Point2D location)
        {
            foreach (var zone in ctx.TileZones)
            {
                if (!zone.Contains(location))
                    continue;

                foreach (var module in TileModuleCentres(zone, location))
                {
                    if (module.DistanceTo(location) <= 2.0)
                        return zone;
                }
            }

            return null;
        }

        /// <summary>Device positions offered by the tile under a point: its centre, plus half centres on long tiles.</summary>
        private static IEnumerable<Point2D> TileModuleCentres(TileZone zone, Point2D location)
        {
            double centreU = (Math.Floor(zone.GridU(location) / zone.PitchU) + 0.5) * zone.PitchU;
            double centreV = (Math.Floor(zone.GridV(location) / zone.PitchV) + 0.5) * zone.PitchV;
            yield return zone.FromGrid(centreU, centreV);

            if (zone.PitchU >= 1190.0)
            {
                yield return zone.FromGrid(centreU - zone.PitchU * 0.25, centreV);
                yield return zone.FromGrid(centreU + zone.PitchU * 0.25, centreV);
            }

            if (zone.PitchV >= 1190.0)
            {
                yield return zone.FromGrid(centreU, centreV - zone.PitchV * 0.25);
                yield return zone.FromGrid(centreU, centreV + zone.PitchV * 0.25);
            }
        }

        private Point2D SnapPoint(CompartmentContext ctx, Point2D target)
        {
            foreach (var zone in ctx.TileZones)
            {
                if (!zone.Contains(target))
                    continue;

                return TileModuleCentres(zone, target)
                    .OrderBy(module => module.DistanceTo(target))
                    .First();
            }

            var frame = ctx.Frame;
            double u = frame.MinU + Math.Round((frame.U(target) - frame.MinU) / FreeSnapIncrementMm) * FreeSnapIncrementMm;
            double v = frame.MinV + Math.Round((frame.V(target) - frame.MinV) / FreeSnapIncrementMm) * FreeSnapIncrementMm;
            return frame.ToWorld(u, v);
        }

        private bool HasObstacleConflict(ProposedDevice device, out string note)
        {
            var clearances = Profile.MepClearances;
            foreach (var obstacle in Snapshot.Obstacles)
            {
                if (Math.Abs(obstacle.ElevationMm - device.ElevationMm) > 1500.0 && obstacle.ElevationMm > 0)
                    continue;

                double required;
                switch (obstacle.Category)
                {
                    case MepObstacleCategory.SupplyAirDiffuser: required = clearances.SupplyDiffuserClearanceMm; break;
                    case MepObstacleCategory.ReturnAirGrille: required = clearances.ReturnGrilleClearanceMm; break;
                    case MepObstacleCategory.LightingFixture: required = clearances.LuminaireClearanceMm; break;
                    case MepObstacleCategory.Sprinkler: required = clearances.SprinklerClearanceMm; break;
                    default: required = 300.0; break;
                }

                if (device.Location.DistanceTo(obstacle.Center) < required)
                {
                    note = $"Within {required:F0} mm of {obstacle.Category} ({obstacle.TypeName}).";
                    return true;
                }
            }

            note = string.Empty;
            return false;
        }

        private bool TryFindDevice(string deviceId, out CompartmentContext ctx, out ProposedDevice device)
        {
            foreach (var candidate in _contexts)
            {
                var match = candidate.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (match != null)
                {
                    ctx = candidate;
                    device = match;
                    return true;
                }
            }

            ctx = null!;
            device = null!;
            return false;
        }

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 10);

        // ------------------------------------------------------------------ verification

        /// <summary>
        /// Stage 4: re-checks one space against the rule profile and grades it.
        /// <paramref name="autoApprove"/> resets the Place tick from the result; manual edits leave
        /// the user's own approval untouched.
        /// </summary>
        private void Verify(CompartmentContext ctx, bool autoApprove)
        {
            var compartment = ctx.Compartment;
            var notes = new List<string>(ctx.BaseNotes);
            notes.AddRange(ctx.RuleNotes);
            var status = ctx.BaseStatus;

            void Raise(CompartmentStatus level, string note)
            {
                if (level == CompartmentStatus.Red ||
                    (level == CompartmentStatus.Amber && status == CompartmentStatus.Green))
                    status = level;
                notes.Add(note);
            }

            var active = ctx.Devices.Where(d => !d.IsExcluded).ToList();
            var detectors = active.Where(d => d.Role.IsDetector()).ToList();
            var speakers = active.Where(d => d.Role == DeviceRole.EwisSpeaker).ToList();
            compartment.ProposedDetectorCount = detectors.Count;
            compartment.ProposedSpeakerCount = speakers.Count;
            compartment.WorstCaseWallDistanceMm = 0.0;
            compartment.MaxDeviceSpacingMm = 0.0;

            if (ctx.IsWithheld)
            {
                compartment.Status = CompartmentStatus.Red;
                compartment.IsApprovedForPlacement = false;
                compartment.Notes = notes.Distinct().ToList();
                return;
            }

            bool wantDetectors = WantsDetectors(ctx);
            bool wantSpeakers = WantsSpeakers(ctx);
            if (!wantDetectors && !wantSpeakers && ctx.Devices.Count == 0)
            {
                compartment.Status = CompartmentStatus.Skipped;
                compartment.IsApprovedForPlacement = false;
                if (ctx.RuleNotes.Count == 0)
                    notes.Add(ctx.HasUserDeviceOverride ? "Excluded by user." : "No devices requested for this space.");
                compartment.Notes = notes.Distinct().ToList();
                return;
            }

            if (ctx.SolveError != null)
                Raise(CompartmentStatus.Red, ctx.SolveError);

            if (!compartment.IsUserModified)
            {
                foreach (var option in new[] { ctx.CurrentOption, ctx.CurrentSpeakerOption })
                {
                    if (option == null)
                        continue;
                    if (option.UsesMarginSpill)
                    {
                        Raise(CompartmentStatus.Amber,
                            "Tile-only placement cannot cover the full compartment; plasterboard margin spillover is required.");
                    }

                    if (!option.IsRegularGrid)
                    {
                        Raise(CompartmentStatus.Amber,
                            "No regular grid covers this shape; a coverage-driven layout was used - review positions.");
                    }
                }
            }

            bool detectorsArePrimary = wantDetectors || detectors.Count > 0;
            if (detectorsArePrimary)
            {
                VerifyRole(ctx, detectors, Profile.GetSpacing(compartment.DetectorKind), "Detector",
                    updateMetrics: true, Raise);
            }

            if (wantSpeakers || speakers.Count > 0)
            {
                VerifyRole(ctx, speakers, Profile.GetSpacing(DeviceRole.EwisSpeaker), "Speaker",
                    updateMetrics: !detectorsArePrimary, Raise);
            }

            if (active.Any(d => d.WasSnappedToTile && IsOnUnverifiedGrid(ctx, d)))
            {
                Raise(CompartmentStatus.Amber,
                    "Tile grid origin could not be read from Revit for this ceiling; confirm the tile set-out or pick the grid origin.");
            }

            if (compartment.IsUserModified && ctx.TileZones.Count > 0)
            {
                int offTile = active.Count(d => !d.WasSnappedToTile && ctx.TileZones.Any(z => z.Contains(d.Location)));
                if (offTile > 0)
                    Raise(CompartmentStatus.Amber, $"{offTile} device(s) are not on a tile centre.");
            }

            // Fixture coordination is left to the designer: reported, but not a reason to hold a space back.
            int clashes = active.Count(d => d.WasNudgedForObstacle);
            if (clashes > 0)
                notes.Add($"{clashes} device(s) are close to a light fitting, diffuser or sprinkler - nudge them in the preview if needed.");

            for (int i = 0; i < active.Count; i++)
            {
                bool overlapping = false;
                for (int j = i + 1; j < active.Count && !overlapping; j++)
                    overlapping = active[i].Location.DistanceTo(active[j].Location) < OverlapDistanceMm;

                if (overlapping)
                {
                    Raise(CompartmentStatus.Amber, $"Two devices are closer than {OverlapDistanceMm:F0} mm to each other.");
                    break;
                }
            }

            int cancelled = ctx.Devices.Count - active.Count;
            if (cancelled > 0)
                notes.Add($"{cancelled} device(s) cancelled by user.");

            compartment.Status = status;
            compartment.Notes = notes.Distinct().ToList();
            if (autoApprove)
                compartment.IsApprovedForPlacement = status != CompartmentStatus.Red;
            else if (active.Count == 0)
                compartment.IsApprovedForPlacement = false;
        }

        private void VerifyRole(
            CompartmentContext ctx,
            IReadOnlyList<ProposedDevice> devices,
            DeviceSpacing spec,
            string label,
            bool updateMetrics,
            Action<CompartmentStatus, string> raise)
        {
            string lower = label.ToLowerInvariant();
            if (devices.Count == 0)
            {
                bool cancelled = ctx.Devices.Any(d =>
                    d.IsExcluded && (label == "Speaker" ? d.Role == DeviceRole.EwisSpeaker : d.Role.IsDetector()));
                raise(CompartmentStatus.Red, cancelled
                    ? $"Every {lower} in this space has been cancelled."
                    : $"No valid {lower} positions found.");
                return;
            }

            var positions = devices.Select(d => d.Location).ToList();
            int uncovered = GridLayoutSolver.CountUncovered(ctx, positions, spec, out double worstReach);
            double spacing = GridLayoutSolver.MaxNearestNeighbourSpacing(positions);
            if (updateMetrics)
            {
                ctx.Compartment.WorstCaseWallDistanceMm = Math.Round(worstReach, 0);
                ctx.Compartment.MaxDeviceSpacingMm = Math.Round(spacing, 0);
            }

            if (uncovered > 0)
            {
                raise(CompartmentStatus.Red,
                    $"{label} coverage is incomplete: {uncovered} sampled point(s) are beyond the {spec.MaxWallDistanceMm:F0} mm X/Y wall reach or hidden from every {lower}.");
            }

            if (spacing > spec.MaxSpacingMm + GridLayoutSolver.ReachToleranceMm)
            {
                raise(CompartmentStatus.Red,
                    $"{label} spacing {spacing:F0} mm exceeds the {spec.MaxSpacingMm:F0} mm limit.");
            }

            foreach (var device in devices)
            {
                if (!ctx.Covers(device.Location))
                {
                    raise(CompartmentStatus.Red, $"{label} is outside the space boundary.");
                    continue;
                }

                double wall = ctx.BoundaryDistance(device.Location);
                if (wall < spec.HardMinWallClearanceMm - 0.5)
                {
                    raise(CompartmentStatus.Red,
                        $"{label} is {wall:F0} mm from a wall (below the {spec.HardMinWallClearanceMm:F0} mm absolute minimum).");
                }
                else if (wall < spec.MinWallClearanceMm - 0.5)
                {
                    raise(CompartmentStatus.Amber,
                        $"{label} is {wall:F0} mm from a wall (below the required {spec.MinWallClearanceMm:F0} mm).");
                }
            }
        }

        private static bool IsOnUnverifiedGrid(CompartmentContext ctx, ProposedDevice device)
        {
            foreach (var zone in ctx.TileZones)
            {
                if (zone.Ceiling.ElementId == device.HostCeilingElementId)
                    return !zone.Grid.IsOriginVerified;
            }

            return false;
        }

        // ------------------------------------------------------------------ plan upkeep

        private void RebuildPlan()
        {
            Plan.Devices.Clear();
            foreach (var ctx in _contexts)
                Plan.Devices.AddRange(ctx.Devices);
            BuildSummary();
        }

        private void BuildSummary()
        {
            var approved = new HashSet<string>(
                Plan.Compartments.Where(c => c.IsApprovedForPlacement).Select(c => c.CompartmentId));
            var placeable = Plan.Devices.Where(d => !d.IsExcluded && approved.Contains(d.CompartmentId)).ToList();

            Plan.Summary = new PlacementSummary
            {
                TotalCompartments = Plan.Compartments.Count,
                GreenCount = Plan.Compartments.Count(c => c.Status == CompartmentStatus.Green),
                AmberCount = Plan.Compartments.Count(c => c.Status == CompartmentStatus.Amber),
                RedCount = Plan.Compartments.Count(c => c.Status == CompartmentStatus.Red),
                SkippedCount = Plan.Compartments.Count(c => c.Status == CompartmentStatus.Skipped),
                TotalSmokeDetectors = placeable.Count(d => d.Role == DeviceRole.SmokeDetector),
                TotalHeatDetectors = placeable.Count(d => d.Role == DeviceRole.HeatDetector),
                TotalEwisSpeakers = placeable.Count(d => d.Role == DeviceRole.EwisSpeaker),
                ExcludedDeviceCount = Plan.Devices.Count(d => d.IsExcluded),
                UnresolvedOpenEdgeCount = Plan.UnresolvedOpenEdges.Count
            };
        }
    }
}
