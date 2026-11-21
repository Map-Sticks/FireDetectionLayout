using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

                _lastSnapshot = _extractor.ExtractActiveLevel(uidoc.Document, uidoc.ActiveView);
                RunSolver();
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
            RunSolver();
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

        private void RunSolver()
        {
            if (_lastSnapshot == null)
                return;

            var profile = RuleProfile.CreateAs1670Default();
            profile.SmokeDetection.MaxDetectorSpacingMm = MaxSpacingMm;
            profile.SmokeDetection.MaxWallDistanceMm = MaxWallReachMm;

            _lastPlan = _engine.Solve(_lastSnapshot, profile);

            Compartments.Clear();
            foreach (var comp in _lastPlan.Compartments)
                Compartments.Add(comp);

            var s = _lastPlan.Summary;
            SummaryLine = $"Compartments: {s.TotalCompartments} (Green: {s.GreenCount}, Amber: {s.AmberCount}, Red: {s.RedCount}, Skipped: {s.SkippedCount}) | Detectors: {s.TotalSmokeDetectors}";
            StatusMessage = $"Solved level '{_lastSnapshot.Metadata.LevelName}' ({_lastSnapshot.Walls.Count} wall segments, {_lastSnapshot.Ceilings.Count} ceilings).";
            CanCommit = s.TotalSmokeDetectors > 0;
        }
    }
}
