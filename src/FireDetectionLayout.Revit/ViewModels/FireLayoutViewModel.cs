using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Reporting;
using FireDetectionLayout.Core.Solver;
using FireDetectionLayout.Core.Storage;
using FireDetectionLayout.Preview;
using FireDetectionLayout.Revit.Extraction;
using FireDetectionLayout.Revit.Placement;
using FireDetectionLayout.Revit.Settings;
using Microsoft.Win32;

namespace FireDetectionLayout.Revit.ViewModels
{
    /// <summary>
    /// Drives the add-in: reads the settings pane, extracts the chosen levels from Revit, solves
    /// them off the UI thread into the shared <see cref="LayoutWorkspace"/>, and commits the
    /// reviewed result back to the model.
    /// </summary>
    public sealed partial class FireLayoutViewModel : ObservableObject
    {
        private const double FtToMm = 304.8;
        private const string DialogTitle = "Fire Detection Layout";

        private static readonly BuiltInCategory[] DeviceCategories =
        {
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_CommunicationDevices
        };

        private readonly RevitSceneExtractor _extractor = new RevitSceneExtractor();
        private readonly RevitPlacementCommitter _committer = new RevitPlacementCommitter();

        private ProjectSettings _settings = new ProjectSettings();
        private string _settingsPath = string.Empty;
        private List<SceneSnapshot> _lastSnapshots = new List<SceneSnapshot>();
        private ElementId? _analysisViewId;
        private int _solveGeneration;
        private LayoutPreviewWindow? _previewWindow;

        public FireLayoutViewModel()
        {
            DeviceKinds.Add(new DeviceKindRow(DeviceRole.SmokeDetector, "Smoke detector", AvailableFamilies));
            DeviceKinds.Add(new DeviceKindRow(DeviceRole.HeatDetector, "Heat detector", AvailableFamilies));
            DeviceKinds.Add(new DeviceKindRow(DeviceRole.EwisSpeaker, "Speaker", AvailableFamilies));
            ApplySettingsToUi();

            Workspace.CommitCommand = CommitApprovedCommand;
            Workspace.ExportCommand = ExportPackageCommand;
            Workspace.ImportCommand = ImportPlanCommand;
            Workspace.PickGridOriginCommand = PickGridOriginCommand;
            Workspace.LayoutChanged += (_, __) =>
            {
                if (IsPreviewEnabled)
                    QueuePreviewRefresh();
            };
        }

        /// <summary>Results under review, shared by the dockable pane and the preview window.</summary>
        public LayoutWorkspace Workspace { get; } = new LayoutWorkspace();

        public ObservableCollection<FamilySymbolOption> AvailableFamilies { get; } = new ObservableCollection<FamilySymbolOption>();
        public ObservableCollection<DeviceKindRow> DeviceKinds { get; } = new ObservableCollection<DeviceKindRow>();
        public ObservableCollection<LevelOption> LevelOptions { get; } = new ObservableCollection<LevelOption>();

        [ObservableProperty]
        private string _statusMessage = "Open a floor plan or ceiling plan and click Analyze.";

        [ObservableProperty]
        private string _warningsText = string.Empty;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _scopeActiveLevel = true;

        [ObservableProperty]
        private bool _scopeSelectedLevels;

        [ObservableProperty]
        private bool _scopeSelectedRooms;

        [ObservableProperty]
        private bool _placeDetectors = true;

        [ObservableProperty]
        private bool _placeSpeakers = true;

        [ObservableProperty]
        private bool _pairSpeakers = true;

        [ObservableProperty]
        private bool _coordinateWithCeilingGrid = true;

        [ObservableProperty]
        private double _pairOffsetMm = 600.0;

        [ObservableProperty]
        private bool _replaceToolOwned = true;

        [ObservableProperty]
        private bool _captureViewSnapshot = true;

        [ObservableProperty]
        private bool _isPreviewEnabled;

        private DeviceKindRow Row(DeviceRole role) => DeviceKinds.First(row => row.Role == role);

        // ------------------------------------------------------------------ settings

        /// <summary>Loads the remembered settings, families and levels of a document into the pane.</summary>
        public void LoadDocument(Document doc)
        {
            string path = ProjectSettingsStore.GetPath(doc);
            if (!string.Equals(path, _settingsPath, StringComparison.OrdinalIgnoreCase))
            {
                _settings = ProjectSettingsStore.Load(doc);
                _settingsPath = path;

                // The previous document's family choices must not leak into this project's mapping.
                foreach (var row in DeviceKinds)
                    row.Family = null;
                ApplySettingsToUi();
                Workspace.SetResults(Enumerable.Empty<LayoutSession>());
                _lastSnapshots = new List<SceneSnapshot>();
            }

            ApplyFamilies(CollectDeviceFamilies(doc));
            ApplyLevels(doc);
        }

        private void ApplySettingsToUi()
        {
            var profile = _settings.Profile;
            PlaceDetectors = profile.Layout.PlaceDetectors;
            PlaceSpeakers = profile.Layout.PlaceSpeakers;
            PairSpeakers = profile.Ewis.LayoutMode == SpeakerLayoutMode.PairedWithDetectors;
            CoordinateWithCeilingGrid = profile.Layout.CoordinateWithCeilingGrid;
            PairOffsetMm = profile.Ewis.PairedDeviceOffsetMm;
            ReplaceToolOwned = _settings.ReplaceToolOwnedDevices;
            CaptureViewSnapshot = _settings.CaptureViewSnapshot;

            var smoke = Row(DeviceRole.SmokeDetector);
            smoke.MaxSpacingMm = profile.SmokeDetection.MaxDetectorSpacingMm;
            smoke.MaxWallDistanceMm = profile.SmokeDetection.MaxWallDistanceMm;
            smoke.MinWallClearanceMm = profile.SmokeDetection.MinWallClearanceMm;

            var heat = Row(DeviceRole.HeatDetector);
            heat.MaxSpacingMm = profile.HeatDetection.MaxDetectorSpacingMm;
            heat.MaxWallDistanceMm = profile.HeatDetection.MaxWallDistanceMm;
            heat.MinWallClearanceMm = profile.HeatDetection.MinWallClearanceMm;

            var speaker = Row(DeviceRole.EwisSpeaker);
            speaker.MaxSpacingMm = profile.Ewis.MaxSpeakerSpacingMm;
            speaker.MaxWallDistanceMm = profile.Ewis.MaxWallDistanceMm;
            speaker.MinWallClearanceMm = profile.Ewis.MinWallClearanceMm;
        }

        /// <summary>Writes the pane back into the settings and returns an independent copy of the profile.</summary>
        private RuleProfile CaptureProfile()
        {
            var profile = _settings.Profile;
            profile.Layout.PlaceDetectors = PlaceDetectors;
            profile.Layout.PlaceSpeakers = PlaceSpeakers;
            profile.Layout.CoordinateWithCeilingGrid = CoordinateWithCeilingGrid;
            profile.Ewis.LayoutMode = PairSpeakers
                ? SpeakerLayoutMode.PairedWithDetectors
                : SpeakerLayoutMode.IndependentGrid;
            profile.Ewis.PairedDeviceOffsetMm = Math.Max(200.0, PairOffsetMm);

            var smoke = Row(DeviceRole.SmokeDetector);
            profile.SmokeDetection.MaxDetectorSpacingMm = smoke.MaxSpacingMm;
            profile.SmokeDetection.MaxWallDistanceMm = smoke.MaxWallDistanceMm;
            profile.SmokeDetection.MinWallClearanceMm = Math.Max(0.0, smoke.MinWallClearanceMm);

            var heat = Row(DeviceRole.HeatDetector);
            profile.HeatDetection.MaxDetectorSpacingMm = heat.MaxSpacingMm;
            profile.HeatDetection.MaxWallDistanceMm = heat.MaxWallDistanceMm;
            profile.HeatDetection.MinWallClearanceMm = Math.Max(0.0, heat.MinWallClearanceMm);

            var speaker = Row(DeviceRole.EwisSpeaker);
            profile.Ewis.MaxSpeakerSpacingMm = speaker.MaxSpacingMm;
            profile.Ewis.MaxWallDistanceMm = speaker.MaxWallDistanceMm;
            profile.Ewis.MinWallClearanceMm = Math.Max(0.0, speaker.MinWallClearanceMm);

            _settings.SmokeDetectorFamily = smoke.Family?.DisplayName ?? _settings.SmokeDetectorFamily;
            _settings.HeatDetectorFamily = heat.Family?.DisplayName ?? _settings.HeatDetectorFamily;
            _settings.SpeakerFamily = speaker.Family?.DisplayName ?? _settings.SpeakerFamily;
            _settings.ReplaceToolOwnedDevices = ReplaceToolOwned;
            _settings.CaptureViewSnapshot = CaptureViewSnapshot;

            return SnapshotSerializer.Deserialize<RuleProfile>(SnapshotSerializer.Serialize(profile));
        }

        private static List<FamilySymbolOption> CollectDeviceFamilies(Document doc)
        {
            var options = new List<FamilySymbolOption>();
            foreach (var category in DeviceCategories)
            {
                options.AddRange(new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Select(s => new FamilySymbolOption(s.Id, s.FamilyName ?? string.Empty, s.Name ?? string.Empty)));
            }

            return options
                .OrderBy(o => o.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.TypeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ApplyFamilies(IReadOnlyList<FamilySymbolOption> families)
        {
            var remembered = new Dictionary<DeviceRole, string>
            {
                [DeviceRole.SmokeDetector] = Row(DeviceRole.SmokeDetector).Family?.DisplayName ?? _settings.SmokeDetectorFamily,
                [DeviceRole.HeatDetector] = Row(DeviceRole.HeatDetector).Family?.DisplayName ?? _settings.HeatDetectorFamily,
                [DeviceRole.EwisSpeaker] = Row(DeviceRole.EwisSpeaker).Family?.DisplayName ?? _settings.SpeakerFamily
            };

            AvailableFamilies.Clear();
            foreach (var family in families)
                AvailableFamilies.Add(family);

            foreach (var row in DeviceKinds)
            {
                string name = remembered[row.Role];
                row.Family =
                    AvailableFamilies.FirstOrDefault(f => string.Equals(f.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    ?? AvailableFamilies.FirstOrDefault(f => LooksLike(f, row.Role));
            }
        }

        /// <summary>Best guess at the family for a device kind from common naming.</summary>
        private static bool LooksLike(FamilySymbolOption option, DeviceRole role)
        {
            string text = option.DisplayName.ToLowerInvariant();
            bool Has(params string[] words) => words.Any(text.Contains);

            switch (role)
            {
                case DeviceRole.HeatDetector:
                    return Has("heat", "thermal");
                case DeviceRole.EwisSpeaker:
                    return Has("speaker", "ewis", "sounder", "occupant warning", "ows", "horn");
                default:
                    return Has("smoke", "photo", "optical") && !Has("heat", "thermal");
            }
        }

        private void ApplyLevels(Document doc)
        {
            var ticked = new HashSet<string>(LevelOptions.Where(o => o.IsSelected).Select(o => o.Name));
            LevelOptions.Clear();
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
            {
                LevelOptions.Add(new LevelOption(level.Id, level.Name, Math.Round(level.Elevation * FtToMm))
                {
                    IsSelected = ticked.Contains(level.Name)
                });
            }
        }

        // ------------------------------------------------------------------ analyse

        [RelayCommand]
        private void RefreshFamilies()
        {
            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    StatusMessage = "No active Revit document.";
                    return;
                }

                LoadDocument(uidoc.Document);
                StatusMessage = AvailableFamilies.Count > 0
                    ? $"Loaded {AvailableFamilies.Count} fire alarm / communication device type(s) from '{uidoc.Document.Title}'."
                    : "No Fire Alarm Device or Communication Device families are loaded in this project.";
            });
        }

        [RelayCommand]
        private void Analyze()
        {
            StatusMessage = "Reading walls, ceilings, rooms and fixtures from Revit...";
            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    StatusMessage = "No active Revit document.";
                    return;
                }

                Document doc = uidoc.Document;
                View view = uidoc.ActiveView;
                LoadDocument(doc);

                if (!TryResolveScope(uidoc, out var levels, out var scopeRoomIds, out string problem))
                {
                    StatusMessage = problem;
                    return;
                }

                var profile = CaptureProfile();
                var timer = Stopwatch.StartNew();
                List<SceneSnapshot> snapshots;
                try
                {
                    snapshots = levels
                        .Select(level => _extractor.ExtractLevel(doc, level, view.Name, scopeRoomIds, _settings.TileGridOrigins))
                        .ToList();
                }
                catch (Exception error)
                {
                    StatusMessage = $"Could not read the model: {error.Message}";
                    throw;
                }

                if (CaptureViewSnapshot && view.GenLevel != null)
                {
                    string levelKey = RevitIds.GetKey(view.GenLevel.Id);
                    var viewed = snapshots.FirstOrDefault(s => s.Metadata.LevelElementId == levelKey);
                    if (viewed != null)
                    {
                        viewed.ViewImage = ViewSnapshotExporter.Capture(
                            uidoc,
                            Path.Combine(Path.GetTempPath(), "FireDetectionLayout"),
                            $"view_{DateTime.Now:yyyyMMdd_HHmmss}");
                    }
                }

                _analysisViewId = view.Id;
                _lastSnapshots = snapshots;
                ProjectSettingsStore.Save(doc, _settings);
                StartSolver(snapshots, profile, timer);
            });
        }

        /// <summary>Works out which levels (and optionally which rooms) the pane asks for.</summary>
        private bool TryResolveScope(
            UIDocument uidoc,
            out List<Level> levels,
            out List<string>? scopeRoomIds,
            out string problem)
        {
            Document doc = uidoc.Document;
            levels = new List<Level>();
            scopeRoomIds = null;
            problem = string.Empty;

            if (ScopeSelectedRooms)
            {
                var spatial = uidoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .OfType<SpatialElement>()
                    .Where(element => element.Area > 0 && element.Level != null)
                    .ToList();
                if (spatial.Count == 0)
                {
                    problem = "Select one or more placed Rooms or MEP Spaces in Revit, then click Analyze.";
                    return false;
                }

                scopeRoomIds = spatial.Select(element => RevitIds.GetKey(element.Id)).ToList();
                levels = spatial.Select(element => element.Level).GroupBy(l => l.Id).Select(g => g.First()).ToList();
                return true;
            }

            if (ScopeSelectedLevels)
            {
                levels = LevelOptions
                    .Where(option => option.IsSelected)
                    .Select(option => doc.GetElement(option.LevelId))
                    .OfType<Level>()
                    .ToList();
                if (levels.Count == 0)
                {
                    problem = "Tick at least one level to analyse.";
                    return false;
                }

                return true;
            }

            if (uidoc.ActiveView.GenLevel == null)
            {
                problem = "Open a floor plan or reflected ceiling plan, or choose Selected levels.";
                return false;
            }

            levels.Add(uidoc.ActiveView.GenLevel);
            return true;
        }

        [RelayCommand]
        private void ReSolve()
        {
            if (_lastSnapshots.Count == 0)
            {
                StatusMessage = "Run Analyze first.";
                return;
            }

            StartSolver(_lastSnapshots, CaptureProfile(), Stopwatch.StartNew());
        }

        private void StartSolver(IReadOnlyList<SceneSnapshot> snapshots, RuleProfile profile, Stopwatch timer)
        {
            int generation = ++_solveGeneration;
            var dispatcher = Dispatcher.CurrentDispatcher;

            // Re-solving the same level keeps the user's per-space choices.
            var previous = new Dictionary<string, LayoutSession>();
            foreach (var level in Workspace.Levels)
                previous[level.Snapshot.Metadata.LevelElementId + "|" + level.LevelName] = level.Session;

            IsBusy = true;
            StatusMessage = "Solving the layout...";

            Task.Run(() => snapshots
                    .Select(snapshot =>
                    {
                        previous.TryGetValue(
                            snapshot.Metadata.LevelElementId + "|" + snapshot.Metadata.LevelName, out var earlier);
                        return new LayoutSession(snapshot, profile, earlier);
                    })
                    .ToList())
                .ContinueWith(task => dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _solveGeneration)
                        return;

                    IsBusy = false;
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        StatusMessage = $"Solver failed: {task.Exception?.GetBaseException().Message ?? "cancelled."}";
                        return;
                    }

                    ApplyResults(task.Result, timer);
                })), TaskScheduler.Default);
        }

        private void ApplyResults(IReadOnlyList<LayoutSession> sessions, Stopwatch timer)
        {
            Workspace.SetResults(sessions);
            Workspace.ShowUnderlay = false;

            var warnings = sessions.SelectMany(s => s.Plan.Warnings).Distinct().ToList();
            WarningsText = string.Join(Environment.NewLine, warnings);

            int rooms = sessions.Sum(s => s.Snapshot.Rooms.Count + s.Snapshot.Spaces.Count);
            int ceilings = sessions.Sum(s => s.Snapshot.Ceilings.Count);
            int walls = sessions.Sum(s => s.Snapshot.Walls.Count);
            string names = string.Join(", ", sessions.Select(s => s.Snapshot.Metadata.LevelName));
            string warningSuffix = warnings.Count > 0 ? $" {warnings.Count} model warning(s)." : string.Empty;
            StatusMessage =
                $"Solved {names} in {timer.Elapsed.TotalSeconds:F1} s: {walls} wall segments, {ceilings} ceilings, {rooms} rooms/spaces.{warningSuffix}";

            QueuePreviewRefresh();
        }

        // ------------------------------------------------------------------ preview

        [RelayCommand]
        private void OpenPreview()
        {
            if (_previewWindow != null)
            {
                _previewWindow.Activate();
                return;
            }

            _previewWindow = new LayoutPreviewWindow(Workspace);
            new WindowInteropHelper(_previewWindow).Owner = Application.MainWindowHandle;
            _previewWindow.Closed += (_, __) => _previewWindow = null;
            _previewWindow.Show();
        }

        public void QueuePreviewRefresh()
        {
            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                    return;

                // The in-view overlay follows the level of the view that was analysed.
                var viewId = _analysisViewId ?? uidoc.ActiveView.Id;
                string? levelKey = (uidoc.Document.GetElement(viewId) as View)?.GenLevel is Level level
                    ? RevitIds.GetKey(level.Id)
                    : null;
                var layout = Workspace.Levels.FirstOrDefault(l => l.Snapshot.Metadata.LevelElementId == levelKey)
                             ?? Workspace.SelectedLevel;

                Application.PreviewServer?.SetPreview(
                    layout?.Plan,
                    role => layout?.Session.GetSpacing(role).MaxWallDistanceMm ?? 0.0,
                    viewId,
                    IsPreviewEnabled);
                uidoc.RefreshActiveView();
            });
        }

        partial void OnIsPreviewEnabledChanged(bool value) => QueuePreviewRefresh();

        // ------------------------------------------------------------------ external assessment

        [RelayCommand]
        private void ExportPackage()
        {
            if (!Workspace.HasResults)
            {
                StatusMessage = "Run Analyze before exporting.";
                return;
            }

            string title = _lastSnapshots.FirstOrDefault()?.Metadata.DocumentTitle ?? "Model";
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "FireDetectionLayout_Exports",
                $"{SafeName(title)}_{DateTime.Now:yyyyMMdd_HHmmss}");

            try
            {
                Directory.CreateDirectory(folder);
                SnapshotSerializer.SaveToFile(CaptureProfile(), Path.Combine(folder, "profile.json"));
                foreach (var level in Workspace.Levels)
                {
                    string stem = SafeName(level.LevelName);
                    var image = level.Snapshot.ViewImage;
                    if (image != null && File.Exists(image.FilePath))
                    {
                        string copy = Path.Combine(folder, $"view_{stem}.png");
                        File.Copy(image.FilePath, copy, overwrite: true);
                        image.FilePath = copy;
                    }

                    SnapshotSerializer.SaveToFile(level.Snapshot, Path.Combine(folder, $"snapshot_{stem}.json"));
                    SnapshotSerializer.SaveToFile(level.Plan, Path.Combine(folder, $"plan_{stem}.json"));
                    File.WriteAllText(
                        Path.Combine(folder, $"plan_{stem}.svg"),
                        PlanSvgRenderer.Render(level.Snapshot, level.Plan));
                }

                StatusMessage = $"Assessment package exported: {folder}";
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                StatusMessage = $"Export failed: {error.Message}";
            }
        }

        [RelayCommand]
        private void ImportPlan()
        {
            if (!Workspace.HasResults)
            {
                StatusMessage = "Run Analyze before importing a plan.";
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Import externally assessed plan",
                Filter = "Plan JSON (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var external = SnapshotSerializer.LoadFromFile<PlacementPlan>(dialog.FileName);
                var level = Workspace.Levels.FirstOrDefault(l => l.LevelName == external.LevelName)
                            ?? Workspace.SelectedLevel;
                if (level == null)
                    return;

                var result = level.Session.ImportDevices(external);
                Workspace.SelectedLevel = level;
                Workspace.NotifyLayoutChanged(level);
                StatusMessage =
                    $"Imported {result.ImportedDeviceCount} device(s) into {result.AffectedCompartmentCount} space(s) on {level.LevelName} and re-checked them" +
                    (result.UnassignedDeviceCount > 0 ? $"; {result.UnassignedDeviceCount} were outside every space." : ".");
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                                          error is Newtonsoft.Json.JsonException)
            {
                StatusMessage = $"Import failed: {error.Message}";
            }
        }

        /// <summary>
        /// Lets the user click a real ceiling grid intersection for a ceiling whose grid origin
        /// Revit cannot report (before 2025.3), then re-solves the spaces under that ceiling.
        /// </summary>
        [RelayCommand]
        private void PickGridOrigin()
        {
            if (!Workspace.HasResults)
            {
                StatusMessage = "Run Analyze before picking a tile grid origin.";
                return;
            }

            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                    return;

                if (!string.Equals(ProjectSettingsStore.GetPath(uidoc.Document), _settingsPath, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "This layout was analysed in a different document. Make that document active first.";
                    return;
                }

                // The request usually comes from the preview window; hand the keyboard and mouse to Revit.
                SetForegroundWindow(Application.MainWindowHandle);
                StatusMessage = "In the Revit ceiling plan, click a ceiling grid intersection (Esc to cancel).";
                XYZ picked = uidoc.Selection.PickPoint(
                    ObjectSnapTypes.Intersections | ObjectSnapTypes.Endpoints,
                    "Pick a ceiling grid intersection (Esc to cancel)");
                var point = new Point2D(picked.X * FtToMm, picked.Y * FtToMm);

                string? levelKey = uidoc.ActiveView.GenLevel != null ? RevitIds.GetKey(uidoc.ActiveView.GenLevel.Id) : null;
                var level = Workspace.Levels.FirstOrDefault(l => l.Snapshot.Metadata.LevelElementId == levelKey)
                            ?? Workspace.SelectedLevel;
                if (level == null)
                    return;

                var modelPoint = new NetTopologySuite.Geometries.Point(point.X, point.Y);
                var ceiling = level.Snapshot.Ceilings
                    .Where(c => c.TileGrid != null && c.TileGrid.PitchUMm > 0 && c.TileGrid.PitchVMm > 0)
                    .Select(c => (Ceiling: c, Polygon: WallGraphPolygonizer.TryToValidNtsPolygon(c.Footprint)))
                    .Where(entry => entry.Polygon != null && entry.Polygon.Covers(modelPoint))
                    .OrderBy(entry => entry.Polygon!.Area)
                    .Select(entry => entry.Ceiling)
                    .FirstOrDefault();
                if (ceiling == null)
                {
                    StatusMessage = "No tiled ceiling with a readable tile pattern was found under that point.";
                    return;
                }

                var grid = new TileGridSpec
                {
                    OriginMm = point,
                    AxisU = ceiling.TileGrid!.AxisU,
                    AxisV = ceiling.TileGrid.AxisV,
                    PitchUMm = ceiling.TileGrid.PitchUMm,
                    PitchVMm = ceiling.TileGrid.PitchVMm,
                    IsOriginVerified = true,
                    Source = "UserPicked"
                };
                int affected = level.Session.UpdateCeilingTileGrid(ceiling.ElementId, ceiling.SourceModel, grid);
                _settings.TileGridOrigins[RevitSceneExtractor.GridOriginKey(ceiling)] = point;
                ProjectSettingsStore.Save(uidoc.Document, _settings);

                Workspace.NotifyLayoutChanged(level);
                StatusMessage = $"Tile grid origin set for ceiling '{ceiling.TypeName}'; {affected} space(s) re-solved.";
            });
        }

        // ------------------------------------------------------------------ commit

        [RelayCommand]
        private void CommitApproved()
        {
            if (!Workspace.HasResults)
            {
                StatusMessage = "Run Analyze first.";
                return;
            }

            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                    return;

                Document doc = uidoc.Document;
                if (!string.Equals(ProjectSettingsStore.GetPath(doc), _settingsPath, StringComparison.OrdinalIgnoreCase))
                {
                    TaskDialog.Show(DialogTitle, "This layout was analysed in a different document. Make that document active, or run Analyze again.");
                    return;
                }

                var plans = Workspace.Plans.ToList();
                int smoke = plans.Sum(p => p.Summary.TotalSmokeDetectors);
                int heat = plans.Sum(p => p.Summary.TotalHeatDetectors);
                int speakers = plans.Sum(p => p.Summary.TotalEwisSpeakers);
                if (smoke + heat + speakers == 0)
                {
                    TaskDialog.Show(DialogTitle, "There are no approved devices to place.");
                    return;
                }

                var families = new DeviceFamilyMap
                {
                    SmokeDetector = ResolveSymbol(doc, Row(DeviceRole.SmokeDetector).Family),
                    HeatDetector = ResolveSymbol(doc, Row(DeviceRole.HeatDetector).Family),
                    Speaker = ResolveSymbol(doc, Row(DeviceRole.EwisSpeaker).Family)
                };

                var unmapped = new List<string>();
                if (smoke > 0 && families.SmokeDetector == null) unmapped.Add("smoke detectors");
                if (heat > 0 && families.HeatDetector == null) unmapped.Add("heat detectors");
                if (speakers > 0 && families.Speaker == null) unmapped.Add("speakers");
                if (unmapped.Count > 0)
                {
                    TaskDialog.Show(DialogTitle,
                        $"Choose a family type for {string.Join(" and ", unmapped)} in the Fire Layout pane first " +
                        "(load a Fire Alarm Device family and click Refresh if the list is empty).");
                    return;
                }

                var lines = new List<string>();
                if (smoke > 0) lines.Add($"{smoke} smoke detector(s) - {Row(DeviceRole.SmokeDetector).Family?.DisplayName}");
                if (heat > 0) lines.Add($"{heat} heat detector(s) - {Row(DeviceRole.HeatDetector).Family?.DisplayName}");
                if (speakers > 0) lines.Add($"{speakers} speaker(s) - {Row(DeviceRole.EwisSpeaker).Family?.DisplayName}");
                int flagged = plans.Sum(p => p.Compartments.Count(c =>
                    c.IsApprovedForPlacement && c.Status == CompartmentStatus.Red));
                string flaggedText = flagged > 0
                    ? $"\n\n{flagged} approved space(s) do not meet the spacing rules (red) and will be placed as drawn."
                    : string.Empty;
                string replaceText = ReplaceToolOwned
                    ? "\n\nDevices this tool placed earlier in the same spaces will be replaced."
                    : string.Empty;

                var confirm = TaskDialog.Show(
                    "Confirm Fire Layout Placement",
                    $"Place on {plans.Count} level(s):\n\n{string.Join("\n", lines)}{flaggedText}{replaceText}\n\nEverything is placed in one transaction (Ctrl+Z undoes it).",
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);
                if (confirm != TaskDialogResult.Yes)
                    return;

                CaptureProfile();
                ProjectSettingsStore.Save(doc, _settings);
                CommitResult result = _committer.CommitPlans(doc, uidoc.ActiveView, plans, families, ReplaceToolOwned);

                string removed = result.RemovedCount > 0 ? $", replaced {result.RemovedCount} earlier device(s)" : string.Empty;
                string skipped = result.SkippedCount > 0 ? $", {result.SkippedCount} not placed" : string.Empty;
                StatusMessage =
                    $"Placed {result.PlacedDetectors} detector(s) and {result.PlacedSpeakers} speaker(s){removed}{skipped} (Ctrl+Z to undo).";

                var warnings = result.Warnings.Distinct().ToList();
                if (warnings.Count > 0)
                    TaskDialog.Show(DialogTitle, string.Join("\n", warnings.Take(12)));
            });
        }

        private static FamilySymbol? ResolveSymbol(Document doc, FamilySymbolOption? option)
        {
            if (option == null)
                return null;

            if (doc.GetElement(option.SymbolId) is FamilySymbol symbol)
                return symbol;

            return DeviceCategories
                .SelectMany(category => new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>())
                .FirstOrDefault(s =>
                    string.Equals(s.FamilyName, option.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Name, option.TypeName, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeName(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars()));

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
