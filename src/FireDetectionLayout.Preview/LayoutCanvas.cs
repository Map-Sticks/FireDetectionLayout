using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Reporting;

namespace FireDetectionLayout.Preview
{
    /// <summary>
    /// Interactive plan drawing of one level's layout.
    ///   Wheel = zoom, drag empty space or middle button = pan, double-click = zoom to space.
    ///   Click a device = cancel / restore it, drag a device = move it, right-click = add devices.
    /// </summary>
    public sealed class LayoutCanvas : FrameworkElement
    {
        public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(
            nameof(Workspace), typeof(LayoutWorkspace), typeof(LayoutCanvas),
            new PropertyMetadata(null, OnWorkspaceChanged));

        private const double DragThresholdPixels = 4.0;
        private const double MinScale = 0.0005;
        private const double MaxScale = 2.0;

        private static readonly Typeface LabelTypeface = new Typeface("Segoe UI");
        private static readonly Typeface SymbolTypeface =
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(255, 255, 255));
        private static readonly Brush TileFill = Frozen(Color.FromRgb(240, 246, 251));
        private static readonly Brush FlushFill = Frozen(Color.FromRgb(250, 247, 238));
        private static readonly Brush TileLineBrush = Frozen(Color.FromRgb(190, 205, 220));
        private static readonly Brush WallBrush = Frozen(Color.FromRgb(55, 65, 81));
        private static readonly Brush FixtureBrush = Frozen(Color.FromRgb(156, 163, 175));
        private static readonly Brush OpenEdgeBrush = Frozen(Color.FromRgb(255, 0, 180));
        private static readonly Brush LabelBrush = Frozen(Color.FromRgb(17, 24, 39));
        private static readonly Brush HintBrush = Frozen(Color.FromRgb(107, 114, 128));
        private static readonly Brush SmokeBrush = Frozen(Color.FromRgb(14, 116, 144));
        private static readonly Brush HeatBrush = Frozen(Color.FromRgb(234, 88, 12));
        private static readonly Brush SpeakerBrush = Frozen(Color.FromRgb(124, 58, 237));
        private static readonly Brush CancelBrush = Frozen(Color.FromRgb(220, 38, 38));
        private static readonly Brush ExistingBrush = Frozen(Color.FromRgb(120, 113, 108));
        private static readonly Brush HoverBrush = Frozen(Color.FromArgb(70, 37, 99, 235));
        private static readonly Brush PairBrush = Frozen(Color.FromRgb(148, 163, 184));

        private readonly List<CeilingVisual> _ceilings = new List<CeilingVisual>();
        private readonly List<CompartmentVisual> _compartments = new List<CompartmentVisual>();
        private bool _cacheValid;
        private bool _fitPending = true;
        private LayoutWorkspace? _listeningTo;

        // View: the model point shown at the centre of the control, and pixels per millimetre.
        private Point _centre;
        private double _scale = 0.02;

        private ProposedDevice? _pressedDevice;
        private ProposedDevice? _hoverDevice;
        private bool _draggingDevice;
        private Point2D? _dragLocation;
        private bool _panning;
        private bool _pressedOnEmpty;
        private Point _pressScreen;
        private Point _pressCentre;

        public LayoutCanvas()
        {
            ClipToBounds = true;
            Focusable = true;
            SizeChanged += (_, __) =>
            {
                if (_fitPending)
                    ZoomToFit();
            };

            // The workspace outlives the window, so the drawing only listens to it while shown.
            Loaded += (_, __) => Listen(Workspace);
            Unloaded += (_, __) => Listen(null);
        }

        public LayoutWorkspace? Workspace
        {
            get => (LayoutWorkspace?)GetValue(WorkspaceProperty);
            set => SetValue(WorkspaceProperty, value);
        }

        private LevelLayout? Level => Workspace?.SelectedLevel;

        private static void OnWorkspaceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var canvas = (LayoutCanvas)sender;
            if (canvas.IsLoaded)
                canvas.Listen(args.NewValue as LayoutWorkspace);

            canvas._cacheValid = false;
            canvas.ZoomToFit();
        }

        private void Listen(LayoutWorkspace? workspace)
        {
            if (ReferenceEquals(workspace, _listeningTo))
                return;

            if (_listeningTo != null)
            {
                _listeningTo.LayoutChanged -= OnLayoutChanged;
                _listeningTo.ViewResetRequested -= OnViewResetRequested;
                _listeningTo.ZoomToCompartmentRequested -= OnZoomToCompartmentRequested;
            }

            _listeningTo = workspace;
            if (workspace != null)
            {
                workspace.LayoutChanged += OnLayoutChanged;
                workspace.ViewResetRequested += OnViewResetRequested;
                workspace.ZoomToCompartmentRequested += OnZoomToCompartmentRequested;
                _cacheValid = false;
                InvalidateVisual();
            }
        }

        private void OnLayoutChanged(object? sender, EventArgs e)
        {
            _cacheValid = false;
            InvalidateVisual();
        }

        private void OnViewResetRequested(object? sender, EventArgs e)
        {
            _cacheValid = false;
            ZoomToFit();
        }

        private void OnZoomToCompartmentRequested(object? sender, CompartmentRow row) =>
            ZoomTo(row.Model.Boundary.GetBounds(), 0.25);

        // ------------------------------------------------------------------ view

        public void ZoomToFit()
        {
            var level = Level;
            if (level == null || ActualWidth < 10 || ActualHeight < 10)
            {
                _fitPending = true;
                InvalidateVisual();
                return;
            }

            var points = new List<Point2D>();
            foreach (var compartment in level.Plan.Compartments)
                points.AddRange(compartment.Boundary.OuterRing);
            if (points.Count == 0)
            {
                foreach (var wall in level.Snapshot.Walls)
                {
                    points.Add(wall.Centerline.Start);
                    points.Add(wall.Centerline.End);
                }
            }

            if (points.Count == 0)
            {
                _fitPending = true;
                InvalidateVisual();
                return;
            }

            ZoomTo(BoundingBox2D.FromPoints(points), 0.06);
        }

        private void ZoomTo(BoundingBox2D bounds, double marginRatio)
        {
            if (ActualWidth < 10 || ActualHeight < 10)
                return;

            double width = Math.Max(1000.0, bounds.Width) * (1.0 + 2.0 * marginRatio);
            double height = Math.Max(1000.0, bounds.Height) * (1.0 + 2.0 * marginRatio);
            _scale = Clamp(Math.Min(ActualWidth / width, ActualHeight / height), MinScale, MaxScale);
            _centre = new Point(bounds.Center.X, bounds.Center.Y);
            _fitPending = false;
            InvalidateVisual();
        }

        private Point ToScreen(double x, double y) => new Point(
            ActualWidth * 0.5 + (x - _centre.X) * _scale,
            ActualHeight * 0.5 - (y - _centre.Y) * _scale);

        private Point ToScreen(Point2D point) => ToScreen(point.X, point.Y);

        private Point2D ToModel(Point screen) => new Point2D(
            _centre.X + (screen.X - ActualWidth * 0.5) / _scale,
            _centre.Y - (screen.Y - ActualHeight * 0.5) / _scale);

        private double SymbolRadius => Clamp(150.0 * _scale, 4.5, 13.0);

        // ------------------------------------------------------------------ rendering

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(BackgroundBrush, null, bounds);

            var workspace = Workspace;
            var level = Level;
            if (workspace == null || level == null)
            {
                DrawText(dc, "Run Analyze to preview the layout.", new Point(16, 14), 13, HintBrush);
                return;
            }

            if (_fitPending)
                ZoomToFit();
            if (!_cacheValid)
                RebuildCache(level);

            dc.PushClip(new RectangleGeometry(bounds));
            DrawUnderlay(dc, workspace, level);

            double pixel = 1.0 / _scale;
            var modelToScreen = new MatrixTransform(new Matrix(
                _scale, 0, 0, -_scale,
                ActualWidth * 0.5 - _centre.X * _scale,
                ActualHeight * 0.5 + _centre.Y * _scale));
            dc.PushTransform(modelToScreen);

            bool underlayShown = workspace.ShowUnderlay && level.Underlay != null;
            DrawCeilings(dc, workspace, pixel, underlayShown);
            DrawCompartments(dc, workspace, pixel);
            DrawWalls(dc, level, pixel, underlayShown);
            if (workspace.ShowFixtures)
                DrawFixtures(dc, level, pixel);
            if (workspace.ShowCoverage)
                DrawCoverage(dc, workspace, level, pixel);

            dc.Pop();

            DrawLabels(dc);
            DrawDevices(dc, level);
            dc.Pop();
        }

        private void DrawUnderlay(DrawingContext dc, LayoutWorkspace workspace, LevelLayout level)
        {
            var image = level.Snapshot.ViewImage;
            if (!workspace.ShowUnderlay || image == null || level.Underlay == null)
                return;

            var topLeft = ToScreen(image.MinXMm, image.MaxYMm);
            var rect = new Rect(
                topLeft.X, topLeft.Y,
                (image.MaxXMm - image.MinXMm) * _scale,
                (image.MaxYMm - image.MinYMm) * _scale);
            dc.PushOpacity(0.55);
            dc.DrawImage(level.Underlay, rect);
            dc.Pop();
        }

        private void DrawCeilings(DrawingContext dc, LayoutWorkspace workspace, double pixel, bool underlayShown)
        {
            var gridPen = FrozenPen(TileLineBrush, pixel);
            foreach (var ceiling in _ceilings)
            {
                if (!underlayShown)
                    dc.DrawGeometry(ceiling.Tiled ? TileFill : FlushFill, null, ceiling.Footprint);

                // Tile joints are drawn once they are far enough apart to read.
                if (workspace.ShowTileGrid && ceiling.GridLines != null && ceiling.MinPitchMm * _scale >= 3.0)
                {
                    dc.PushClip(ceiling.Footprint);
                    dc.DrawGeometry(null, gridPen, ceiling.GridLines);
                    dc.Pop();
                }
            }
        }

        private void DrawCompartments(DrawingContext dc, LayoutWorkspace workspace, double pixel)
        {
            foreach (var visual in _compartments)
            {
                bool selected = ReferenceEquals(visual.Row, workspace.SelectedCompartment);
                var brush = visual.Row.StatusBrush;
                var fill = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = selected ? 0.20 : 0.07 };
                dc.DrawGeometry(fill, FrozenPen(brush, (selected ? 3.0 : 1.5) * pixel), visual.Outline);
            }
        }

        private void DrawWalls(DrawingContext dc, LevelLayout level, double pixel, bool underlayShown)
        {
            var pens = new Dictionary<int, Pen>();
            foreach (var wall in level.Snapshot.Walls)
            {
                // Over a view capture the walls are already drawn; keep only a hairline.
                double thickness = underlayShown ? pixel : Math.Max(wall.ThicknessMm, 1.5 * pixel);
                int key = (int)Math.Round(thickness * 10.0);
                if (!pens.TryGetValue(key, out var pen))
                {
                    pen = new Pen(WallBrush, thickness) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
                    pen.Freeze();
                    pens[key] = pen;
                }

                dc.DrawLine(pen,
                    new Point(wall.Centerline.Start.X, wall.Centerline.Start.Y),
                    new Point(wall.Centerline.End.X, wall.Centerline.End.Y));
            }

            var openPen = new Pen(OpenEdgeBrush, 1.2 * pixel) { DashStyle = new DashStyle(new[] { 5.0, 4.0 }, 0) };
            openPen.Freeze();
            foreach (var edge in level.Plan.UnresolvedOpenEdges)
                dc.DrawLine(openPen, new Point(edge.Start.X, edge.Start.Y), new Point(edge.End.X, edge.End.Y));
        }

        private void DrawFixtures(DrawingContext dc, LevelLayout level, double pixel)
        {
            var pen = FrozenPen(FixtureBrush, pixel);
            foreach (var obstacle in level.Snapshot.Obstacles)
            {
                var box = obstacle.Bounds;
                if (box.Width * _scale < 2.0 && box.Height * _scale < 2.0)
                    continue;
                dc.DrawRectangle(null, pen, new Rect(box.MinX, box.MinY, box.Width, box.Height));
            }

            var existingPen = FrozenPen(ExistingBrush, 1.5 * pixel);
            double half = SymbolRadius * 0.7 * pixel;
            foreach (var existing in level.Snapshot.ExistingDevices)
            {
                var p = existing.Location;
                dc.DrawLine(existingPen, new Point(p.X - half, p.Y - half), new Point(p.X + half, p.Y + half));
                dc.DrawLine(existingPen, new Point(p.X - half, p.Y + half), new Point(p.X + half, p.Y - half));
            }
        }

        /// <summary>
        /// Wall reach of the devices in the selected space (and of the device under the cursor),
        /// drawn square because it is measured along the room axes.
        /// </summary>
        private void DrawCoverage(DrawingContext dc, LayoutWorkspace workspace, LevelLayout level, double pixel)
        {
            string? selectedId = workspace.SelectedCompartment?.CompartmentId;
            foreach (var device in level.Plan.Devices)
            {
                bool inFocus = device.CompartmentId == selectedId ||
                               ReferenceEquals(device, _hoverDevice) || ReferenceEquals(device, _pressedDevice);
                if (device.IsExcluded || !inFocus)
                    continue;

                var colour = ((SolidColorBrush)RoleBrush(device.Role)).Color;
                var pen = new Pen(new SolidColorBrush(colour) { Opacity = 0.45 }, pixel)
                {
                    DashStyle = new DashStyle(new[] { 6.0, 5.0 }, 0)
                };
                double reach = level.Session.GetSpacing(device.Role).MaxWallDistanceMm;
                var location = ReferenceEquals(device, _pressedDevice) && _dragLocation != null
                    ? _dragLocation
                    : device.Location;

                dc.PushTransform(new RotateTransform(device.RotationDegrees, location.X, location.Y));
                dc.DrawRectangle(null, pen, new Rect(location.X - reach, location.Y - reach, reach * 2, reach * 2));
                dc.Pop();
            }
        }

        private void DrawLabels(DrawingContext dc)
        {
            foreach (var visual in _compartments)
            {
                double widthPixels = visual.Bounds.Width * _scale;
                if (widthPixels < 70.0 || visual.Bounds.Height * _scale < 22.0)
                    continue;

                var anchor = ToScreen(visual.Bounds.MinX, visual.Bounds.MaxY);
                DrawText(dc, visual.Row.DisplayName, new Point(anchor.X + 5, anchor.Y + 3), 11, LabelBrush, widthPixels - 10);
            }
        }

        private void DrawDevices(DrawingContext dc, LevelLayout level)
        {
            double radius = SymbolRadius;
            var approved = new HashSet<string>(
                level.Plan.Compartments.Where(c => c.IsApprovedForPlacement).Select(c => c.CompartmentId));

            Point Position(ProposedDevice device) =>
                ReferenceEquals(device, _pressedDevice) && _dragLocation != null
                    ? ToScreen(_dragLocation)
                    : ToScreen(device.Location);

            // Thin tie between a detector and the speaker it is paired with.
            var pairPen = FrozenPen(PairBrush, 1.0);
            foreach (var pair in level.Plan.Devices.Where(d => d.PairId != null && !d.IsExcluded).GroupBy(d => d.PairId))
            {
                var members = pair.ToList();
                if (members.Count == 2)
                    dc.DrawLine(pairPen, Position(members[0]), Position(members[1]));
            }

            foreach (var device in level.Plan.Devices)
            {
                var centre = Position(device);
                if (centre.X < -30 || centre.Y < -30 || centre.X > ActualWidth + 30 || centre.Y > ActualHeight + 30)
                    continue;

                bool live = !device.IsExcluded && approved.Contains(device.CompartmentId);
                var brush = RoleBrush(device.Role);
                var pen = FrozenPen(brush, Math.Max(1.5, radius * 0.2));

                if (ReferenceEquals(device, _hoverDevice))
                    dc.DrawEllipse(HoverBrush, null, centre, radius + 6, radius + 6);

                dc.PushOpacity(live ? 1.0 : 0.35);
                if (device.Role == DeviceRole.EwisSpeaker)
                    dc.DrawRectangle(Brushes.White, pen, new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2));
                else
                    dc.DrawEllipse(Brushes.White, pen, centre, radius, radius);

                if (radius >= 7.5)
                {
                    string code = device.Role.ToShortCode();
                    double size = radius * (code.Length > 1 ? 0.72 : 1.15);
                    var text = CreateText(code, size, brush, SymbolTypeface);
                    dc.DrawText(text, new Point(centre.X - text.Width * 0.5, centre.Y - text.Height * 0.5));
                }

                dc.Pop();

                if (device.IsExcluded)
                {
                    var cross = FrozenPen(CancelBrush, Math.Max(1.5, radius * 0.22));
                    dc.DrawLine(cross, new Point(centre.X - radius, centre.Y - radius), new Point(centre.X + radius, centre.Y + radius));
                    dc.DrawLine(cross, new Point(centre.X - radius, centre.Y + radius), new Point(centre.X + radius, centre.Y - radius));
                }
                else if (device.IsManual)
                {
                    // Marks a position the user set by hand.
                    dc.DrawEllipse(LabelBrush, null, new Point(centre.X + radius, centre.Y - radius), 2.5, 2.5);
                }
            }
        }

        private void RebuildCache(LevelLayout level)
        {
            _ceilings.Clear();
            _compartments.Clear();

            foreach (var ceiling in level.Snapshot.Ceilings)
            {
                if (ceiling.Footprint.OuterRing.Count < 3)
                    continue;

                var visual = new CeilingVisual { Footprint = BuildGeometry(ceiling.Footprint) };
                var grid = ceiling.TileGrid;
                if (grid != null && grid.PitchUMm > 0 && grid.PitchVMm > 0)
                {
                    visual.Tiled = true;
                    visual.MinPitchMm = Math.Min(grid.PitchUMm, grid.PitchVMm);
                    var lines = new StreamGeometry();
                    using (var context = lines.Open())
                    {
                        foreach (var line in PlanSvgRenderer.TileGridLines(grid, ceiling.Footprint.GetBounds()))
                        {
                            context.BeginFigure(new Point(line.Start.X, line.Start.Y), false, false);
                            context.LineTo(new Point(line.End.X, line.End.Y), true, false);
                        }
                    }

                    lines.Freeze();
                    visual.GridLines = lines;
                }

                _ceilings.Add(visual);
            }

            foreach (var row in level.Rows)
            {
                if (row.Model.Boundary.OuterRing.Count < 3)
                    continue;

                _compartments.Add(new CompartmentVisual
                {
                    Row = row,
                    Outline = BuildGeometry(row.Model.Boundary),
                    Bounds = row.Model.Boundary.GetBounds()
                });
            }

            _cacheValid = true;
        }

        private static Geometry BuildGeometry(Polygon2D polygon)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var context = geometry.Open())
            {
                AddRing(context, polygon.OuterRing);
                foreach (var hole in polygon.Holes)
                    AddRing(context, hole);
            }

            geometry.Freeze();
            return geometry;
        }

        private static void AddRing(StreamGeometryContext context, IReadOnlyList<Point2D> ring)
        {
            if (ring.Count < 3)
                return;

            context.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
            for (int i = 1; i < ring.Count; i++)
                context.LineTo(new Point(ring[i].X, ring[i].Y), true, false);
        }

        // ------------------------------------------------------------------ interaction

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var screen = e.GetPosition(this);
            var anchor = ToModel(screen);
            _scale = Clamp(_scale * Math.Pow(1.15, e.Delta / 120.0), MinScale, MaxScale);

            // Keep the model point under the cursor where it is.
            _centre = new Point(
                anchor.X - (screen.X - ActualWidth * 0.5) / _scale,
                anchor.Y + (screen.Y - ActualHeight * 0.5) / _scale);
            _fitPending = false;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _pressScreen = e.GetPosition(this);
            _pressCentre = _centre;

            if (e.ChangedButton == MouseButton.Middle)
            {
                _panning = true;
                CaptureMouse();
                return;
            }

            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.ClickCount == 2)
            {
                var workspace = Workspace;
                if (workspace != null && HitDevice(_pressScreen) == null &&
                    workspace.SelectCompartmentAt(ToModel(_pressScreen)) && workspace.SelectedCompartment != null)
                    ZoomTo(workspace.SelectedCompartment.Model.Boundary.GetBounds(), 0.25);
                return;
            }

            _pressedDevice = HitDevice(_pressScreen);
            _pressedOnEmpty = _pressedDevice == null;
            _draggingDevice = false;
            _dragLocation = null;
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var screen = e.GetPosition(this);
            bool moved = Math.Abs(screen.X - _pressScreen.X) > DragThresholdPixels ||
                         Math.Abs(screen.Y - _pressScreen.Y) > DragThresholdPixels;

            if (_pressedDevice != null && e.LeftButton == MouseButtonState.Pressed)
            {
                if (!_draggingDevice && !moved)
                    return;

                _draggingDevice = true;
                var target = ToModel(screen);
                var workspace = Workspace;
                var level = Level;
                _dragLocation = workspace != null && level != null && workspace.SnapToGrid
                    ? level.Session.SnapPoint(target)
                    : target;
                InvalidateVisual();
                return;
            }

            if ((_pressedOnEmpty && e.LeftButton == MouseButtonState.Pressed && moved) ||
                (_panning && e.MiddleButton == MouseButtonState.Pressed))
            {
                _panning = true;
                _centre = new Point(
                    _pressCentre.X - (screen.X - _pressScreen.X) / _scale,
                    _pressCentre.Y + (screen.Y - _pressScreen.Y) / _scale);
                _fitPending = false;
                InvalidateVisual();
                return;
            }

            var hover = HitDevice(screen);
            if (!ReferenceEquals(hover, _hoverDevice))
            {
                _hoverDevice = hover;
                Cursor = hover != null ? Cursors.Hand : Cursors.Arrow;
                ToolTip = hover != null ? DescribeDevice(hover) : null;
                InvalidateVisual();
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            var workspace = Workspace;

            if (e.ChangedButton == MouseButton.Left && workspace != null)
            {
                if (_pressedDevice != null && _draggingDevice && _dragLocation != null)
                    workspace.MoveDevice(_pressedDevice.DeviceId, _dragLocation);
                else if (_pressedDevice != null)
                    workspace.ToggleDevice(_pressedDevice.DeviceId);
                else if (_pressedOnEmpty && !_panning)
                    workspace.SelectCompartmentAt(ToModel(e.GetPosition(this)));
            }

            _pressedDevice = null;
            _draggingDevice = false;
            _dragLocation = null;
            _pressedOnEmpty = false;
            _panning = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            var workspace = Workspace;
            if (workspace == null || Level == null)
                return;

            var screen = e.GetPosition(this);
            var model = ToModel(screen);
            var device = HitDevice(screen);
            bool insideSpace = workspace.SelectCompartmentAt(model);

            var menu = new ContextMenu();
            void Add(string header, Action action, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, __) => action();
                menu.Items.Add(item);
            }

            if (device != null)
            {
                Add(device.IsExcluded ? "Restore this device" : "Cancel this device",
                    () => workspace.ToggleDevice(device.DeviceId));
                menu.Items.Add(new Separator());
            }

            Add("Add smoke detector here", () => workspace.AddDevice(DeviceRole.SmokeDetector, model), insideSpace);
            Add("Add heat detector here", () => workspace.AddDevice(DeviceRole.HeatDetector, model), insideSpace);
            Add("Add speaker here", () => workspace.AddDevice(DeviceRole.EwisSpeaker, model), insideSpace);
            menu.Items.Add(new Separator());
            Add("Regenerate this space", () => workspace.RegenerateSelectedCommand.Execute(null), insideSpace);
            Add("Reset this space", () => workspace.ResetSelectedCommand.Execute(null), insideSpace);
            Add("Zoom to this space", () => workspace.ZoomToSelectedCommand.Execute(null), insideSpace);
            Add("Zoom to fit", ZoomToFit);

            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ProposedDevice? HitDevice(Point screen)
        {
            var level = Level;
            if (level == null)
                return null;

            double reach = SymbolRadius + 3.0;
            ProposedDevice? best = null;
            double bestDistance = reach;
            foreach (var device in level.Plan.Devices)
            {
                var centre = ToScreen(device.Location);
                double distance = Math.Max(Math.Abs(centre.X - screen.X), Math.Abs(centre.Y - screen.Y));
                if (distance <= bestDistance)
                {
                    best = device;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private string DescribeDevice(ProposedDevice device)
        {
            string space = Level?.Session.GetCompartment(device.CompartmentId)?.DisplayName ?? device.CompartmentId;
            string state = device.IsExcluded ? "Cancelled - click to restore" : "Click to cancel, drag to move";
            return $"{device.Role.ToDisplayName()} - {space}\n" +
                   $"Ceiling height {device.ElevationMm:F0} mm\n{device.Notes}\n{state}";
        }

        // ------------------------------------------------------------------ helpers

        private static Brush RoleBrush(DeviceRole role)
        {
            switch (role)
            {
                case DeviceRole.EwisSpeaker: return SpeakerBrush;
                case DeviceRole.HeatDetector: return HeatBrush;
                default: return SmokeBrush;
            }
        }

        private FormattedText CreateText(string text, double size, Brush brush, Typeface typeface) =>
            new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private void DrawText(DrawingContext dc, string text, Point origin, double size, Brush brush, double maxWidth = 0)
        {
            var formatted = CreateText(text, size, brush, LabelTypeface);
            if (maxWidth > 0)
            {
                formatted.MaxTextWidth = Math.Max(10.0, maxWidth);
                formatted.MaxLineCount = 1;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
            }

            dc.DrawText(formatted, origin);
        }

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : value > max ? max : value;

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness);
            pen.Freeze();
            return pen;
        }

        private sealed class CeilingVisual
        {
            public Geometry Footprint = Geometry.Empty;
            public Geometry? GridLines;
            public bool Tiled;
            public double MinPitchMm;
        }

        private sealed class CompartmentVisual
        {
            public CompartmentRow Row = null!;
            public Geometry Outline = Geometry.Empty;
            public BoundingBox2D Bounds = new BoundingBox2D();
        }
    }
}
