# FireDetectionLayout

Revit add-in that lays out **fire detectors and EWIS / occupant-warning speakers** for a level, a set of levels or selected rooms in one pass, to **AS 1670.1 / AS 1670.4** style spacing rules that you control.

It reads the walls and ceilings (host and linked models), works out the closed and open-plan spaces, lays devices out on a **regular grid** coordinated with the ceiling tiles, shows the result in a **separate preview window** where it can be adjusted, and only then places the families in Revit.

Built with **Nice3point.Revit.Sdk** (`Revit 2023-2027`) on an **Extract -> Solve -> Review -> Commit** architecture.

## What it does

| | |
|---|---|
| **Scope** | Level of the active view, any ticked levels, or the Rooms / MEP Spaces selected in Revit. |
| **Spaces** | Closed rooms and wall-bounded open-plan areas from the physical walls (trimmed to the wall faces); Rooms, Spaces and ceilings recover anything the walls do not close. Deep downstands split exposed soffits. |
| **Devices** | Detectors only, speakers only, or both. By default every detector gets a speaker beside it on one common grid; speakers can instead have their own grid. Smoke and heat detectors carry separate spacing rules. |
| **Rules** | Per device kind: maximum spacing, maximum distance to a wall measured along the room X and Y axes, minimum distance to a wall. Each kind is mapped to a family type. |
| **Grid first** | Layouts are regular room-aligned grids. L / T / U shaped spaces and branching corridors are split into rectangular bays, each with its own grid; a grid that only misses the ceiling behind a core is topped up on its half-lines. A coverage-driven fallback is used only when no grid fits, and is flagged. |
| **Tile ceilings** | Devices sit on tile centres of uncut tiles not already occupied by a light or diffuser. `1200x600` and `1200x300`: a detector and speaker share one tile, each centred in its own half. `600x600` and `300x300`: one device per tile, the pair in neighbouring tiles. Works whichever way the tiles run in a corridor. |
| **Layout only** | Untick *Coordinate with ceiling tile grids* for a symmetric room grid that ignores the tile set-out. |
| **Preview** | Modeless window with the drawing and a per-space table. Regenerate one space or all for the next alternative, click a device to cancel it, drag to move it (snaps to tile centres), right-click to add, untick detectors or speakers per space, switch a space to heat. Every edit is re-checked against the rules. |
| **Placement** | One transaction. Devices are placed on the level and offset to the ceiling (or soffit) height under each device; earlier devices placed by this tool in the same spaces are replaced, manually placed ones are never touched. |
| **External assessment** | Export a package (snapshot, view capture, rule profile, plan, SVG drawing), assess it outside Revit with the CLI or viewer, and import a plan back for review and placement. |

## Using it in Revit

1. **Fire Detection** ribbon tab -> **Fire Layout** opens the pane.
2. Choose the scope, tick detectors / speakers, set the spacing rules and pick a family type for each device kind.
3. **Analyze**. A typical level solves in about a second.
4. **Open preview**, review and adjust.
5. **Place in Revit**.

Status colours: green meets the rules, amber needs a look (exposed soffit, tile grid origin not confirmed, device closer to a wall than required), red does not meet the rules and is held back unless you tick *Place*.

The rule profile, family mapping and any picked tile grid origins are remembered per project in `%APPDATA%\FireDetectionLayout\Projects`. Room-type rules (rooms that get no devices, detector-only, speaker-only or heat by name) are edited in that file.

### Tile grid origin

Revit 2025.3 and later report the real ceiling grid lines. Earlier versions only expose the tile size and direction, not where the architect aligned the pattern, so those spaces are amber until you use **Pick tile grid origin** and click a grid intersection in the ceiling plan. The origin is remembered for that ceiling.

## Solution structure

- `src/FireDetectionLayout.Core` - pure C# (`netstandard2.0`): wall-graph spaces, ceiling classification, grid solver, editable `LayoutSession`, SVG renderer, JSON contracts. No Revit references.
- `src/FireDetectionLayout.Preview` - WPF preview window and drawing (`net48`, `net8.0-windows`). No Revit references.
- `src/FireDetectionLayout.Revit` - the add-in: extraction, settings pane, placement, in-view overlay.
- `src/FireDetectionLayout.Cli` - `firelayout` command line for assessment outside Revit.
- `src/FireDetectionLayout.Viewer` - `FireLayoutViewer`, the preview window as a standalone program.
- `tests/FireDetectionLayout.Tests` - xUnit tests of the solver; no Revit needed.
- `docs/ARCHITECTURE.md` - engineering rules and design.

## Building

Open `FireDetectionLayout.sln` in Visual Studio 2022, pick the configuration for your Revit version (`Debug.R25` for Revit 2025) and press **F5**: the SDK builds, deploys the add-in and starts Revit. Close Revit before building, because a running Revit locks the deployed files.

From a terminal the add-in needs the Visual Studio MSBuild (the `dotnet` CLI cannot load the SDK's build task without .NET 10):

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  src\FireDetectionLayout.Revit\FireDetectionLayout.Revit.csproj -restore -p:Configuration=Debug.R25
```

Add `-p:DeployAddin=false` to compile without deploying. `R27` targets .NET 10 and needs Visual Studio 2026.

## Tests

```powershell
dotnet test tests\FireDetectionLayout.Tests\FireDetectionLayout.Tests.csproj
```

## Assessment outside Revit

**Export package** writes `snapshot_<level>.json`, `plan_<level>.json`, `plan_<level>.svg`, `profile.json` and the view capture to `Desktop\FireDetectionLayout_Exports`.

```powershell
dotnet run --project src\FireDetectionLayout.Cli -- solve snapshot_L3.json --speakers on --svg L3.svg
dotnet run --project src\FireDetectionLayout.Cli -- verify snapshot_L3.json plan_from_elsewhere.json
dotnet run --project src\FireDetectionLayout.Viewer -- snapshot_L3.json --speakers on
```

`solve` lays a snapshot out, `verify` re-checks device positions from any source against the rules, and the viewer gives the same interactive preview as the add-in and saves the reviewed plan. **Import plan** in the add-in loads a plan JSON as the proposed layout for that level: each device is assigned to the space that contains it and re-checked, and a space the plan leaves empty stays empty. Nothing is placed until you commit.

## Limits

- Spacing defaults (smoke 10 m / 5 m, heat 7 m / 3.5 m, 500 mm from walls) are starting values. Set them from the edition of the standard and the design basis your project adopts. AS 1670.4 prescribes no speaker spacing: speaker coverage is an acoustic design input, so the speaker row defaults to the detector grid.
- Sloped ceilings are reported and not laid out.
- Lights, diffusers and sprinklers are avoided on tile ceilings and otherwise reported, not coordinated: nudge devices in the preview.
- Ceiling-hosted families can only be hosted on ceilings in the host model; with linked ceilings use a level-based or face-based (work plane) family.
- Hold-open door detectors and concealed-space detection are not generated.
