using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Reporting;
using FireDetectionLayout.Core.Solver;
using FireDetectionLayout.Core.Storage;
using FireDetectionLayout.Preview;
using Microsoft.Win32;

namespace FireDetectionLayout.Viewer;

/// <summary>
/// Standalone layout review outside Revit. Opens a snapshot exported by the add-in, lays it out
/// with the same solver and preview the add-in uses, and saves the reviewed plan for import.
///
///   FireLayoutViewer [snapshot.json ...] [--profile profile.json] [--plan plan.json]
///                    [--detectors on|off] [--speakers on|off] [--layout-only]
///                    [--render image.png]   (draw the preview to an image and exit)
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        try
        {
            var snapshotPaths = args.Where((arg, index) =>
                    !arg.StartsWith("--", StringComparison.Ordinal) &&
                    (index == 0 || !OptionsWithValue.Contains(args[index - 1])))
                .ToList();
            if (snapshotPaths.Count == 0)
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Open exported snapshot(s)",
                    Filter = "Snapshot JSON (*.json)|*.json",
                    Multiselect = true
                };
                if (dialog.ShowDialog() != true)
                    return 0;
                snapshotPaths.AddRange(dialog.FileNames);
            }

            var profile = LoadProfile(args);
            var sessions = snapshotPaths
                .Select(path => new LayoutSession(LoadSnapshot(path), profile))
                .ToList();

            if (Option(args, "--plan") is { } planPath)
                sessions[0].ImportDevices(SnapshotSerializer.LoadFromFile<PlacementPlan>(planPath));

            var workspace = new LayoutWorkspace { CommitLabel = "Save plan..." };
            string folder = Path.GetDirectoryName(Path.GetFullPath(snapshotPaths[0])) ?? Environment.CurrentDirectory;
            workspace.CommitCommand = new RelayCommand(() => SavePlans(workspace, folder));
            workspace.ExportCommand = new RelayCommand(() => SaveDrawings(workspace, folder));
            workspace.ImportCommand = new RelayCommand(() => ImportPlan(workspace));
            workspace.SetResults(sessions);
            workspace.ShowUnderlay = workspace.Levels.Any(level => level.Underlay != null);

            var window = new LayoutPreviewWindow(workspace) { Title = "Fire Detection Layout - Viewer" };
            if (Option(args, "--render") is { } imagePath)
            {
                Render(window, imagePath);
                return 0;
            }

            return app.Run(window);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          InvalidDataException or Newtonsoft.Json.JsonException)
        {
            MessageBox.Show(error.Message, "Fire Layout Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static readonly string[] OptionsWithValue =
        { "--profile", "--plan", "--detectors", "--speakers", "--render" };

    private static SceneSnapshot LoadSnapshot(string path)
    {
        var snapshot = SnapshotSerializer.LoadFromFile<SceneSnapshot>(path);

        // An exported package can be moved; its view capture travels beside the snapshot.
        var image = snapshot.ViewImage;
        if (image != null && !File.Exists(image.FilePath))
        {
            string beside = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, Path.GetFileName(image.FilePath));
            if (File.Exists(beside))
                image.FilePath = beside;
        }

        return snapshot;
    }

    private static RuleProfile LoadProfile(string[] args)
    {
        var profile = Option(args, "--profile") is { } path
            ? SnapshotSerializer.LoadFromFile<RuleProfile>(path)
            : RuleProfile.CreateAs1670Default();
        if (Option(args, "--detectors") is { } detectors)
            profile.Layout.PlaceDetectors = detectors.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (Option(args, "--speakers") is { } speakers)
            profile.Layout.PlaceSpeakers = speakers.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (args.Contains("--layout-only"))
            profile.Layout.CoordinateWithCeilingGrid = false;
        return profile;
    }

    private static void SavePlans(LayoutWorkspace workspace, string folder)
    {
        foreach (var level in workspace.Levels)
        {
            var dialog = new SaveFileDialog
            {
                Title = $"Save reviewed plan - {level.LevelName}",
                Filter = "Plan JSON (*.json)|*.json",
                InitialDirectory = folder,
                FileName = $"plan_{SafeName(level.LevelName)}.json"
            };
            if (dialog.ShowDialog() == true)
                SnapshotSerializer.SaveToFile(level.Plan, dialog.FileName);
        }
    }

    private static void SaveDrawings(LayoutWorkspace workspace, string folder)
    {
        foreach (var level in workspace.Levels)
        {
            string path = Path.Combine(folder, $"plan_{SafeName(level.LevelName)}.svg");
            File.WriteAllText(path, PlanSvgRenderer.Render(level.Snapshot, level.Plan));
        }

        MessageBox.Show($"Drawings written to {folder}", "Fire Layout Viewer");
    }

    private static void ImportPlan(LayoutWorkspace workspace)
    {
        var level = workspace.SelectedLevel;
        if (level == null)
            return;

        var dialog = new OpenFileDialog { Title = "Import plan", Filter = "Plan JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
            return;

        level.Session.ImportDevices(SnapshotSerializer.LoadFromFile<PlacementPlan>(dialog.FileName));
        workspace.NotifyLayoutChanged(level);
    }

    /// <summary>Lays the window out off screen and saves what it would show.</summary>
    private static void Render(LayoutPreviewWindow window, string imagePath)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
        window.Drawing.ZoomToFit();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(imagePath))
            encoder.Save(stream);
        window.Close();
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string SafeName(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars()));
}
