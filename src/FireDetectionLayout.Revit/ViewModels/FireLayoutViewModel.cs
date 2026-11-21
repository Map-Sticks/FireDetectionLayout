using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Solver;
using FireDetectionLayout.Core.Storage;
using FireDetectionLayout.Revit.Extraction;
using FireDetectionLayout.Revit.Placement;

namespace FireDetectionLayout.Revit.ViewModels
{
    public sealed class FamilySymbolOption
    {
        public FamilySymbolOption(ElementId symbolId, string familyName, string typeName)
        {
            SymbolId = symbolId;
            FamilyName = familyName;
            TypeName = typeName;
        }

        public ElementId SymbolId { get; }
        public string FamilyName { get; }
        public string TypeName { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(TypeName)
            ? FamilyName
            : $"{FamilyName} : {TypeName}";

        public override string ToString() => DisplayName;
    }

    public sealed partial class FireLayoutViewModel : ObservableObject
    {
        private readonly RevitSceneExtractor _extractor = new RevitSceneExtractor();
        private readonly FireLayoutEngine _engine = new FireLayoutEngine();
        private readonly RevitPlacementCommitter _committer = new RevitPlacementCommitter();

        private SceneSnapshot? _lastSnapshot;
        private PlacementPlan? _lastPlan;
        private ElementId? _analysisViewId;
        private int _solveGeneration;

        [ObservableProperty]
        private string _statusMessage = "Open an RCP or Floor Plan and click Analyze Active Level.";

        [ObservableProperty]
        private string _summaryLine = "No analysis run yet.";

        [ObservableProperty]
        private double _maxSpacingMm = 10000.0;

        [ObservableProperty]
        private double _maxWallReachMm = 5000.0;

        [ObservableProperty]
        private double _minWallOffsetMm = 500.0;

        [ObservableProperty]
        private bool _canCommit;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isPreviewEnabled;

        [ObservableProperty]
        private SmokeCompartment? _selectedCompartment;

        [ObservableProperty]
        private string _selectedCompartmentDetails = "Select a compartment to review its analysis notes.";

        [ObservableProperty]
        private FamilySymbolOption? _selectedDetectorFamily;

        public ObservableCollection<FamilySymbolOption> AvailableDetectorFamilies { get; } = new ObservableCollection<FamilySymbolOption>();

        public ObservableCollection<SmokeCompartment> Compartments { get; } = new ObservableCollection<SmokeCompartment>();

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

                var families = CollectDetectorFamilies(uidoc.Document);
                ApplyDetectorFamilies(families);
                StatusMessage = families.Count > 0
                    ? $"Loaded {families.Count} Fire Alarm Device type(s) from '{uidoc.Document.Title}'."
                    : "No Fire Alarm Device families loaded in the active project.";
            });
        }

        [RelayCommand]
        private void AnalyzeActiveLevel()
        {
            StatusMessage = "Extracting walls, ceilings, soffits, rooms, and MEP obstacles...";
            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    StatusMessage = "No active Revit document.";
                    return;
                }

                ApplyDetectorFamilies(CollectDetectorFamilies(uidoc.Document));
                _analysisViewId = uidoc.ActiveView.Id;
                _lastSnapshot = _extractor.ExtractActiveLevel(uidoc.Document, uidoc.ActiveView);
                StartSolver(_lastSnapshot);
            });
        }

        [RelayCommand]
        private void ReSolve()
        {
            if (_lastSnapshot == null)
            {
                StatusMessage = "Run Analyze Active Level first.";
                return;
            }
            StartSolver(_lastSnapshot);
        }

        [RelayCommand]
        private void ExportSnapshot()
        {
            if (_lastSnapshot == null)
            {
                StatusMessage = "Run Analyze Active Level first before exporting a snapshot.";
                return;
            }

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "FireDetectionLayout_Exports");
            string filePath = Path.Combine(folder, $"snapshot_{_lastSnapshot.Metadata.LevelName}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            SnapshotSerializer.SaveToFile(_lastSnapshot, filePath);
            StatusMessage = $"Snapshot exported: {filePath}";
        }

        [RelayCommand]
        private void CommitApproved()
        {
            if (_lastPlan == null)
                return;

            FamilySymbolOption? chosenFamily = SelectedDetectorFamily;
            if (chosenFamily == null)
            {
                TaskDialog.Show(
                    "Fire Detection Layout",
                    "Select an approved Smoke Detector family first (or load a Fire Alarm Device family into the project and click Refresh).");
                return;
            }

            int approvedCompartments = Compartments.Count(c => c.IsApprovedForPlacement);
            int approvedDetectors = Compartments.Where(c => c.IsApprovedForPlacement).Sum(c => c.ProposedDetectorCount);

            if (approvedDetectors == 0)
            {
                TaskDialog.Show("Fire Detection Layout", "No approved detectors to place.");
                return;
            }

            TaskDialogResult confirm = TaskDialog.Show(
                "Confirm Fire Detector Placement",
                $"Place {approvedDetectors} smoke detector(s) across {approvedCompartments} approved compartment(s) using:\n{chosenFamily.DisplayName}\n\nin a single Revit transaction?",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

            if (confirm != TaskDialogResult.Yes)
                return;

            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null || _lastPlan == null)
                    return;

                FamilySymbol? symbol = uidoc.Document.GetElement(chosenFamily.SymbolId) as FamilySymbol
                    ?? new FilteredElementCollector(uidoc.Document)
                        .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(s =>
                            string.Equals(s.FamilyName, chosenFamily.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.Name, chosenFamily.TypeName, StringComparison.OrdinalIgnoreCase));

                if (symbol == null)
                {
                    ApplyDetectorFamilies(CollectDetectorFamilies(uidoc.Document));
                    TaskDialog.Show(
                        "Fire Detection Layout",
                        $"The selected family '{chosenFamily.DisplayName}' is not available in the active document. Please choose a loaded Fire Alarm Device family.");
                    return;
                }

                CommitResult res = _committer.CommitPlan(uidoc.Document, uidoc.ActiveView, _lastPlan, symbol);
                StatusMessage = $"Placed {res.PlacedCount} smoke detector(s) using '{chosenFamily.DisplayName}' in 1 transaction (Ctrl+Z to undo).";
            });
        }

        private void StartSolver(SceneSnapshot snapshot)
        {
            int generation = ++_solveGeneration;
            var profile = RuleProfile.CreateAs1670Default();
            profile.SmokeDetection.MaxDetectorSpacingMm = MaxSpacingMm;
            profile.SmokeDetection.MaxWallDistanceMm = MaxWallReachMm;
            double clampedWallOffset = Math.Max(0.0, MinWallOffsetMm);
            profile.SmokeDetection.MinWallClearanceMm = clampedWallOffset;
            profile.SmokeDetection.HardMinWallClearanceMm = Math.Min(clampedWallOffset, profile.SmokeDetection.HardMinWallClearanceMm);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("The WPF dispatcher is unavailable for solver results.");

            IsBusy = true;
            CanCommit = false;
            StatusMessage = "Solving visibility-constrained detector coverage...";

            Task.Run(() => _engine.Solve(snapshot, profile)).ContinueWith(task =>
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _solveGeneration)
                        return;

                    IsBusy = false;
                    if (task.IsFaulted)
                    {
                        var error = task.Exception?.GetBaseException();
                        StatusMessage = $"Solver failed: {error?.Message ?? "Unknown error."}";
                        CanCommit = false;
                        return;
                    }

                    if (task.IsCanceled)
                    {
                        StatusMessage = "Solver was cancelled.";
                        CanCommit = false;
                        return;
                    }

                    ApplySolverResult(snapshot, task.Result);
                    QueuePreviewRefresh();
                }));
            }, TaskScheduler.Default);
        }

        private void ApplySolverResult(SceneSnapshot snapshot, PlacementPlan plan)
        {
            _lastPlan = plan;
            Compartments.Clear();
            foreach (var comp in _lastPlan.Compartments)
                Compartments.Add(comp);

            var s = _lastPlan.Summary;
            SummaryLine = $"Compartments: {s.TotalCompartments} (Green: {s.GreenCount}, Amber: {s.AmberCount}, Red: {s.RedCount}, Skipped: {s.SkippedCount}) | Detectors: {s.TotalSmokeDetectors} | Open edges: {s.UnresolvedOpenEdgeCount}";
            string warningSuffix = plan.Warnings.Count > 0 ? $", {plan.Warnings.Count} warning(s)" : string.Empty;
            StatusMessage = $"Solved '{snapshot.Metadata.LevelName}': {snapshot.Rooms.Count} rooms, {snapshot.Spaces.Count} spaces, {snapshot.Ceilings.Count} ceilings, {snapshot.Soffits.Count} soffits{warningSuffix}.";
            UpdateCanCommit();
            SelectedCompartment = Compartments.FirstOrDefault();
        }

        public void LoadFamiliesFromDocument(Document doc)
        {
            ApplyDetectorFamilies(CollectDetectorFamilies(doc));
        }

        private static List<FamilySymbolOption> CollectDetectorFamilies(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .OrderBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new FamilySymbolOption(s.Id, s.FamilyName ?? string.Empty, s.Name ?? string.Empty))
                .ToList();
        }

        private void ApplyDetectorFamilies(IReadOnlyList<FamilySymbolOption> families)
        {
            void Update()
            {
                string? previousDisplay = SelectedDetectorFamily?.DisplayName;
                ElementId? previousId = SelectedDetectorFamily?.SymbolId;

                AvailableDetectorFamilies.Clear();
                foreach (var family in families)
                    AvailableDetectorFamilies.Add(family);

                FamilySymbolOption? match = null;
                if (previousId != null)
                    match = AvailableDetectorFamilies.FirstOrDefault(f => f.SymbolId.Equals(previousId));
                if (match == null && !string.IsNullOrEmpty(previousDisplay))
                    match = AvailableDetectorFamilies.FirstOrDefault(f =>
                        string.Equals(f.DisplayName, previousDisplay, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    match = AvailableDetectorFamilies.FirstOrDefault(IsLikelySmokeDetector)
                        ?? AvailableDetectorFamilies.FirstOrDefault();

                SelectedDetectorFamily = match;
                UpdateCanCommit();
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(Update));
            else
                Update();
        }

        private static bool IsLikelySmokeDetector(FamilySymbolOption option)
        {
            string text = option.DisplayName.ToLowerInvariant();
            return text.Contains("smoke") || text.Contains("detector") || text.Contains("photoelectric");
        }

        private void UpdateCanCommit()
        {
            CanCommit = !IsBusy &&
                        _lastPlan != null &&
                        _lastPlan.Summary.TotalSmokeDetectors > 0 &&
                        SelectedDetectorFamily != null;
        }

        partial void OnSelectedDetectorFamilyChanged(FamilySymbolOption? value)
        {
            UpdateCanCommit();
        }

        public void QueuePreviewRefresh()
        {
            Application.EventHandler.Raise(uiApp =>
            {
                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                    return;

                UpdatePreview(uidoc);
            });
        }

        private void UpdatePreview(UIDocument uidoc)
        {
            var viewId = _analysisViewId ?? uidoc.ActiveView.Id;
            Application.PreviewServer?.SetPreview(_lastPlan, MaxWallReachMm, viewId, IsPreviewEnabled);
            uidoc.RefreshActiveView();
        }

        partial void OnIsPreviewEnabledChanged(bool value)
        {
            QueuePreviewRefresh();
        }

        partial void OnSelectedCompartmentChanged(SmokeCompartment? value)
        {
            SelectedCompartmentDetails = value == null
                ? "Select a compartment to review its analysis notes."
                : value.Notes.Count == 0
                    ? "No review notes."
                    : string.Join(Environment.NewLine, value.Notes);
        }
    }
}
