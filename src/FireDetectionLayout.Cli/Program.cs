using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Core.Reporting;
using FireDetectionLayout.Core.Solver;
using FireDetectionLayout.Core.Storage;

namespace FireDetectionLayout.Cli;

/// <summary>
/// Assesses an exported Revit snapshot outside Revit. The resulting plan JSON can be imported
/// back into the add-in's preview for review and commit.
/// </summary>
internal static class Program
{
    private const string Usage = """
        firelayout - external fire detection / EWIS speaker layout assessment

        Usage:
          firelayout solve  <snapshot.json> [options]   Lay out a snapshot exported from Revit.
          firelayout verify <snapshot.json> <plan.json> [options]
                                                       Re-check device positions from any source.
          firelayout profile <profile.json>            Write the default rule profile to edit.

        Options:
          --profile <file>      Rule profile JSON (default: built-in AS 1670 profile).
          --out <file>          Plan JSON to write (default: <snapshot>.plan.json).
          --svg <file>          Also write an SVG drawing of the result.
          --detectors on|off    Lay out detectors (default: profile setting).
          --speakers on|off     Lay out speakers (default: profile setting).
          --layout-only         Ignore ceiling tile grids (symmetric room grid only).
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "solve" => Solve(args),
                "verify" => Verify(args),
                "profile" => WriteProfile(args),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          InvalidDataException or Newtonsoft.Json.JsonException or ArgumentException)
        {
            return Fail(error.Message);
        }
    }

    private static int Solve(string[] args)
    {
        if (args.Length < 2)
            return Fail("solve needs a snapshot file.");

        string snapshotPath = args[1];
        var snapshot = SnapshotSerializer.LoadFromFile<SceneSnapshot>(snapshotPath);
        var profile = LoadProfile(args);
        var session = new LayoutSession(snapshot, profile);

        string outPath = Option(args, "--out") ?? Path.ChangeExtension(snapshotPath, ".plan.json");
        SnapshotSerializer.SaveToFile(session.Plan, outPath);
        WriteSvg(args, snapshot, session.Plan);
        Report(session.Plan);
        Console.WriteLine($"Plan written to {outPath}");
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 3)
            return Fail("verify needs a snapshot file and a plan file.");

        var snapshot = SnapshotSerializer.LoadFromFile<SceneSnapshot>(args[1]);
        var external = SnapshotSerializer.LoadFromFile<PlacementPlan>(args[2]);
        var session = new LayoutSession(snapshot, LoadProfile(args));
        var result = session.ImportDevices(external);

        Console.WriteLine(
            $"Checked {result.ImportedDeviceCount} device(s) in {result.AffectedCompartmentCount} space(s); " +
            $"{result.UnassignedDeviceCount} outside every space.");
        string? outPath = Option(args, "--out");
        if (outPath != null)
        {
            SnapshotSerializer.SaveToFile(session.Plan, outPath);
            Console.WriteLine($"Verified plan written to {outPath}");
        }

        WriteSvg(args, snapshot, session.Plan);
        Report(session.Plan);
        return session.Plan.Summary.RedCount > 0 ? 2 : 0;
    }

    private static int WriteProfile(string[] args)
    {
        if (args.Length < 2)
            return Fail("profile needs an output file.");

        SnapshotSerializer.SaveToFile(RuleProfile.CreateAs1670Default(), args[1]);
        Console.WriteLine($"Default rule profile written to {args[1]}");
        return 0;
    }

    private static RuleProfile LoadProfile(string[] args)
    {
        string? path = Option(args, "--profile");
        var profile = path != null
            ? SnapshotSerializer.LoadFromFile<RuleProfile>(path)
            : RuleProfile.CreateAs1670Default();

        if (Option(args, "--detectors") is { } detectors)
            profile.Layout.PlaceDetectors = IsOn(detectors);
        if (Option(args, "--speakers") is { } speakers)
            profile.Layout.PlaceSpeakers = IsOn(speakers);
        if (args.Contains("--layout-only"))
            profile.Layout.CoordinateWithCeilingGrid = false;
        return profile;
    }

    private static void WriteSvg(string[] args, SceneSnapshot snapshot, PlacementPlan plan)
    {
        string? svgPath = Option(args, "--svg");
        if (svgPath == null)
            return;

        File.WriteAllText(svgPath, PlanSvgRenderer.Render(snapshot, plan));
        Console.WriteLine($"Drawing written to {svgPath}");
    }

    private static void Report(PlacementPlan plan)
    {
        var summary = plan.Summary;
        Console.WriteLine(
            $"{plan.LevelName}: {summary.TotalCompartments} space(s) - " +
            $"{summary.GreenCount} green, {summary.AmberCount} amber, {summary.RedCount} red, {summary.SkippedCount} skipped");
        Console.WriteLine(
            $"Devices to place: {summary.TotalSmokeDetectors} smoke, {summary.TotalHeatDetectors} heat, " +
            $"{summary.TotalEwisSpeakers} speaker(s)");

        foreach (var compartment in plan.Compartments.Where(c => c.Status is CompartmentStatus.Amber or CompartmentStatus.Red))
        {
            Console.WriteLine($"  [{compartment.Status}] {compartment.DisplayName}");
            foreach (string note in compartment.Notes)
                Console.WriteLine($"      {note}");
        }

        foreach (string warning in plan.Warnings)
            Console.WriteLine($"  warning: {warning}");
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool IsOn(string value) =>
        value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine("Run 'firelayout --help' for usage.");
        return 1;
    }
}
