using System;
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
        private bool _canCommit;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _isPreviewEnabled;

        [ObservableProperty]
        private SmokeCompartment? _selectedCompartment;

        [ObservableProperty]
        private string _selectedCompartmentDetails = "Select a compartment to review its analysis notes.";

        public ObservableCollection<SmokeCompartment> Compartments { get; } = new ObservableCollection<SmokeCompartment>();

        [RelayCommand]
        private void AnalyzeActiveLevel()
        {
            StatusMessage = "Extracting walls, ceilings, rooms, and MEP obstacles...";
            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null)
                {
                    StatusMessage = "No active Revit document.";
                    return;
                }

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

            int approvedCompartments = Compartments.Count(c => c.IsApprovedForPlacement);
            int approvedDetectors = Compartments.Where(c => c.IsApprovedForPlacement).Sum(c => c.ProposedDetectorCount);

            if (approvedDetectors == 0)
            {
                TaskDialog.Show("Fire Detection Layout", "No approved detectors to place.");
                return;
            }

            TaskDialogResult confirm = TaskDialog.Show(
                "Confirm Fire Detector Placement",
                $"Place {approvedDetectors} smoke detector(s) across {approvedCompartments} approved compartment(s) in a single Revit transaction?",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

            if (confirm != TaskDialogResult.Yes)
                return;

            Application.EventHandler.Raise(uiApp =>
            {
                UIDocument? uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null || _lastPlan == null)
                    return;

                FamilySymbol? symbol = new FilteredElementCollector(uidoc.Document)
                    .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (symbol == null)
                {
                    TaskDialog.Show("Fire Detection Layout", "Load at least one Fire Alarm Device family into the project first.");
                    return;
                }

                CommitResult res = _committer.CommitPlan(uidoc.Document, uidoc.ActiveView, _lastPlan, symbol);
                StatusMessage = $"Placed {res.PlacedCount} smoke detector(s) in 1 transaction (Ctrl+Z to undo).";
            });
        }

        private void StartSolver(SceneSnapshot snapshot)
        {
            int generation = ++_solveGeneration;
            var profile = RuleProfile.CreateAs1670Default();
            profile.SmokeDetection.MaxDetectorSpacingMm = MaxSpacingMm;
            profile.SmokeDetection.MaxWallDistanceMm = MaxWallReachMm;
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
            StatusMessage = $"Solved level '{snapshot.Metadata.LevelName}' ({snapshot.Walls.Count} wall segments, {snapshot.Ceilings.Count} ceilings).";
            CanCommit = s.TotalSmokeDetectors > 0;
            SelectedCompartment = Compartments.FirstOrDefault();
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
