using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Solver;

namespace FireDetectionLayout.Preview
{
    /// <summary>One analysed level: its snapshot, its editable layout session and the rows shown for it.</summary>
    public sealed class LevelLayout
    {
        private ImageSource? _underlay;
        private bool _underlayLoaded;

        public LevelLayout(LayoutWorkspace workspace, LayoutSession session)
        {
            Session = session;
            foreach (var compartment in session.Plan.Compartments)
                Rows.Add(new CompartmentRow(workspace, this, compartment));
        }

        public LayoutSession Session { get; }
        public SceneSnapshot Snapshot => Session.Snapshot;
        public PlacementPlan Plan => Session.Plan;
        public string LevelName => Session.Snapshot.Metadata.LevelName;
        public ObservableCollection<CompartmentRow> Rows { get; } = new ObservableCollection<CompartmentRow>();

        /// <summary>Capture of the Revit view the level was analysed from, or null when none was taken.</summary>
        public ImageSource? Underlay
        {
            get
            {
                if (_underlayLoaded)
                    return _underlay;

                _underlayLoaded = true;
                string? path = Snapshot.ViewImage?.FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    _underlay = image;
                }
                catch (Exception error) when (error is IOException || error is NotSupportedException ||
                                              error is UnauthorizedAccessException)
                {
                    _underlay = null;
                }

                return _underlay;
            }
        }

        public override string ToString() => LevelName;
    }

    /// <summary>A space in the review tables. Edits go straight to the layout session and re-solve the space.</summary>
    public sealed class CompartmentRow : ObservableObject
    {
        private static readonly Brush GreenBrush = Frozen(22, 163, 74);
        private static readonly Brush AmberBrush = Frozen(217, 119, 6);
        private static readonly Brush RedBrush = Frozen(220, 38, 38);
        private static readonly Brush GreyBrush = Frozen(156, 163, 175);

        private readonly LayoutWorkspace _workspace;

        public CompartmentRow(LayoutWorkspace workspace, LevelLayout level, SmokeCompartment model)
        {
            _workspace = workspace;
            Level = level;
            Model = model;
            RegenerateCommand = new RelayCommand(() =>
            {
                Level.Session.RegenerateCompartment(CompartmentId);
                _workspace.SelectedCompartment = this;
                _workspace.NotifyLayoutChanged(Level);
            });
        }

        public LevelLayout Level { get; }
        public SmokeCompartment Model { get; }

        /// <summary>Shows the next layout alternative for this space.</summary>
        public ICommand RegenerateCommand { get; }
        public string CompartmentId => Model.CompartmentId;
        public string DisplayName => Model.DisplayName;
        public string Status => Model.Status.ToString();
        public double ElevationMm => Model.CeilingElevationMm;
        public int Detectors => Model.ProposedDetectorCount;
        public int Speakers => Model.ProposedSpeakerCount;
        public double WallMaxMm => Model.WorstCaseWallDistanceMm;
        public string Layout => Model.LayoutDescription;
        public string Notes => Model.Notes.Count == 0 ? "No review notes." : string.Join(Environment.NewLine, Model.Notes);

        public Brush StatusBrush
        {
            get
            {
                switch (Model.Status)
                {
                    case CompartmentStatus.Green: return GreenBrush;
                    case CompartmentStatus.Amber: return AmberBrush;
                    case CompartmentStatus.Red: return RedBrush;
                    default: return GreyBrush;
                }
            }
        }

        public string Ceiling
        {
            get
            {
                if (!string.IsNullOrEmpty(Model.TileSize))
                    return Model.CeilingKind == CeilingKind.MixedFinish ? $"Mixed {Model.TileSize}" : $"Tile {Model.TileSize}";

                switch (Model.CeilingKind)
                {
                    case CeilingKind.SuspendedTile: return "Tile (no grid)";
                    case CeilingKind.Plasterboard: return "Flush";
                    case CeilingKind.MixedFinish: return "Mixed";
                    case CeilingKind.ExposedOrNone: return "Exposed";
                    case CeilingKind.Sloped: return "Sloped";
                    default: return "Unknown";
                }
            }
        }

        public string Variant => Model.VariantCount > 0
            ? $"{Model.VariantIndex + 1}/{Model.VariantCount}"
            : "-";

        public bool IsApproved
        {
            get => Model.IsApprovedForPlacement;
            set
            {
                Level.Session.SetCompartmentApproved(CompartmentId, value);
                _workspace.NotifyLayoutChanged(Level);
            }
        }

        public bool IncludeDetectors
        {
            get => Model.IncludeDetectors;
            set
            {
                Level.Session.SetCompartmentDevices(CompartmentId, value, Model.IncludeSpeakers);
                _workspace.NotifyLayoutChanged(Level);
            }
        }

        public bool IncludeSpeakers
        {
            get => Model.IncludeSpeakers;
            set
            {
                Level.Session.SetCompartmentDevices(CompartmentId, Model.IncludeDetectors, value);
                _workspace.NotifyLayoutChanged(Level);
            }
        }

        public bool IsHeat
        {
            get => Model.DetectorKind == DeviceRole.HeatDetector;
            set
            {
                Level.Session.SetCompartmentDetectorKind(
                    CompartmentId, value ? DeviceRole.HeatDetector : DeviceRole.SmokeDetector);
                _workspace.NotifyLayoutChanged(Level);
            }
        }

        /// <summary>Re-reads every column after the session changed the underlying space.</summary>
        public void Refresh() => OnPropertyChanged(string.Empty);

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
