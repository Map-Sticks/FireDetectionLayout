using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Solver;

namespace FireDetectionLayout.Preview
{
    /// <summary>
    /// The reviewable result of an analysis: one editable layout per level, the current selection
    /// and the preview display options. It knows nothing about Revit - the host (the add-in or the
    /// standalone viewer) plugs in what Commit, Export, Import and Pick Grid Origin do.
    /// </summary>
    public sealed partial class LayoutWorkspace : ObservableObject
    {
        [ObservableProperty]
        private LevelLayout? _selectedLevel;

        [ObservableProperty]
        private CompartmentRow? _selectedCompartment;

        [ObservableProperty]
        private string _selectedCompartmentDetails = "Select a space to review its notes.";

        [ObservableProperty]
        private string _summaryLine = "No analysis run yet.";

        [ObservableProperty]
        private bool _hasResults;

        [ObservableProperty]
        private bool _showCoverage = true;

        [ObservableProperty]
        private bool _showTileGrid = true;

        [ObservableProperty]
        private bool _showFixtures = true;

        [ObservableProperty]
        private bool _showUnderlay;

        [ObservableProperty]
        private bool _snapToGrid = true;

        [ObservableProperty]
        private string _commitLabel = "Place in Revit";

        [ObservableProperty]
        private ICommand? _commitCommand;

        [ObservableProperty]
        private ICommand? _exportCommand;

        [ObservableProperty]
        private ICommand? _importCommand;

        [ObservableProperty]
        private ICommand? _pickGridOriginCommand;

        public ObservableCollection<LevelLayout> Levels { get; } = new ObservableCollection<LevelLayout>();

        /// <summary>Raised whenever devices, statuses or display options change and the drawing must repaint.</summary>
        public event EventHandler? LayoutChanged;

        /// <summary>Raised when new results arrive or the level changes and the drawing should zoom to fit.</summary>
        public event EventHandler? ViewResetRequested;

        /// <summary>Raised when the drawing should zoom to one space.</summary>
        public event EventHandler<CompartmentRow>? ZoomToCompartmentRequested;

        public IEnumerable<PlacementPlan> Plans => Levels.Select(level => level.Plan);

        /// <summary>Total devices that would be placed across every level.</summary>
        public int PlaceableDeviceCount => Plans.Sum(plan =>
            plan.Summary.TotalSmokeDetectors + plan.Summary.TotalHeatDetectors + plan.Summary.TotalEwisSpeakers);

        /// <summary>Replaces the results with freshly solved sessions, one per level.</summary>
        public void SetResults(IEnumerable<LayoutSession> sessions)
        {
            string? previousLevel = SelectedLevel?.LevelName;
            Levels.Clear();
            foreach (var session in sessions)
                Levels.Add(new LevelLayout(this, session));

            HasResults = Levels.Count > 0;
            SelectedLevel = Levels.FirstOrDefault(level => level.LevelName == previousLevel) ?? Levels.FirstOrDefault();
            UpdateSummary();
            ViewResetRequested?.Invoke(this, EventArgs.Empty);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Call after a session edit: refreshes the tables, the summary and the drawing.</summary>
        public void NotifyLayoutChanged(LevelLayout? level = null)
        {
            foreach (var row in (level ?? SelectedLevel)?.Rows ?? Enumerable.Empty<CompartmentRow>())
                row.Refresh();

            UpdateSummary();
            UpdateDetails();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ commands

        [RelayCommand]
        private void RegenerateSelected()
        {
            var row = SelectedCompartment;
            if (row == null)
                return;

            row.Level.Session.RegenerateCompartment(row.CompartmentId);
            NotifyLayoutChanged(row.Level);
        }

        [RelayCommand]
        private void RegenerateAll()
        {
            var level = SelectedLevel;
            if (level == null)
                return;

            level.Session.RegenerateAll();
            NotifyLayoutChanged(level);
        }

        [RelayCommand]
        private void ResetSelected()
        {
            var row = SelectedCompartment;
            if (row == null)
                return;

            row.Level.Session.ResetCompartment(row.CompartmentId);
            NotifyLayoutChanged(row.Level);
        }

        [RelayCommand]
        private void ZoomToFit() => ViewResetRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void ZoomToSelected()
        {
            if (SelectedCompartment != null)
                ZoomToCompartmentRequested?.Invoke(this, SelectedCompartment);
        }

        // ------------------------------------------------------------------ drawing interactions

        /// <summary>Cancels a device, or restores it when it was already cancelled.</summary>
        public bool ToggleDevice(string deviceId)
        {
            var level = SelectedLevel;
            var device = level?.Session.GetDevice(deviceId);
            if (level == null || device == null)
                return false;

            level.Session.SetDeviceExcluded(deviceId, !device.IsExcluded);
            SelectCompartment(level, device.CompartmentId);
            NotifyLayoutChanged(level);
            return true;
        }

        public bool MoveDevice(string deviceId, Point2D target)
        {
            var level = SelectedLevel;
            if (level == null || !level.Session.MoveDevice(deviceId, target, SnapToGrid))
                return false;

            NotifyLayoutChanged(level);
            return true;
        }

        public bool AddDevice(DeviceRole role, Point2D target)
        {
            var level = SelectedLevel;
            var device = level?.Session.AddDevice(role, target, SnapToGrid);
            if (level == null || device == null)
                return false;

            SelectCompartment(level, device.CompartmentId);
            NotifyLayoutChanged(level);
            return true;
        }

        /// <summary>Selects the space under a model point; returns false when the point is outside every space.</summary>
        public bool SelectCompartmentAt(Point2D point)
        {
            var level = SelectedLevel;
            var compartment = level?.Session.FindCompartmentAt(point);
            if (level == null || compartment == null)
                return false;

            SelectCompartment(level, compartment.CompartmentId);
            return true;
        }

        private void SelectCompartment(LevelLayout level, string compartmentId)
        {
            var row = level.Rows.FirstOrDefault(r => r.CompartmentId == compartmentId);
            if (row != null && !ReferenceEquals(row, SelectedCompartment))
                SelectedCompartment = row;
        }

        // ------------------------------------------------------------------ upkeep

        private void UpdateSummary()
        {
            if (Levels.Count == 0)
            {
                SummaryLine = "No analysis run yet.";
                return;
            }

            int spaces = 0, green = 0, amber = 0, red = 0, skipped = 0, smoke = 0, heat = 0, speakers = 0, cancelled = 0;
            foreach (var plan in Plans)
            {
                var summary = plan.Summary;
                spaces += summary.TotalCompartments;
                green += summary.GreenCount;
                amber += summary.AmberCount;
                red += summary.RedCount;
                skipped += summary.SkippedCount;
                smoke += summary.TotalSmokeDetectors;
                heat += summary.TotalHeatDetectors;
                speakers += summary.TotalEwisSpeakers;
                cancelled += summary.ExcludedDeviceCount;
            }

            string levels = Levels.Count > 1 ? $"{Levels.Count} levels, " : string.Empty;
            string cancelledText = cancelled > 0 ? $", {cancelled} cancelled" : string.Empty;
            SummaryLine =
                $"{levels}{spaces} spaces (green {green}, amber {amber}, red {red}, skipped {skipped})  |  " +
                $"To place: {smoke} smoke, {heat} heat, {speakers} speakers{cancelledText}";
        }

        private void UpdateDetails()
        {
            var row = SelectedCompartment;
            SelectedCompartmentDetails = row == null
                ? "Select a space to review its notes."
                : string.IsNullOrEmpty(row.Layout) ? row.Notes : $"Layout: {row.Layout}{Environment.NewLine}{row.Notes}";
        }

        partial void OnSelectedCompartmentChanged(CompartmentRow? value)
        {
            UpdateDetails();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        partial void OnSelectedLevelChanged(LevelLayout? value)
        {
            SelectedCompartment = value?.Rows.FirstOrDefault();
            ViewResetRequested?.Invoke(this, EventArgs.Empty);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        partial void OnShowCoverageChanged(bool value) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        partial void OnShowTileGridChanged(bool value) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        partial void OnShowFixturesChanged(bool value) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        partial void OnShowUnderlayChanged(bool value) => LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}
