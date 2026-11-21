# FireDetectionLayout

Professional C# Revit Add-in for automated, auditable **AS 1670.1** (smoke detection) and **AS 1670.4** (optional paired EWIS speaker) placement and ceiling coordination.

Built with **Nice3point.Revit.Sdk** for Visual Studio 2022 (`Revit 2023–2027`) using a decoupled **Extract → Solve → Review → Commit** architecture.

## Solution Structure

- `src/FireDetectionLayout.Core` — Pure C# (`netstandard2.0`) geometry, physical wall-graph polygonization (`NetTopologySuite`), AS 1670.1/1670.4 spacing solver, ceiling classifier, tile-grid snapper, and JSON snapshot serializer. Zero Revit API references.
- `src/FireDetectionLayout.Revit` — Multi-targeted Revit add-in (`Nice3point.Revit.Sdk/6.2.2`, `Debug.R23`–`Debug.R27` / `Release.R23`–`Release.R27`) with linked-model geometry extraction, WPF dockable review panel, and atomic single-transaction placement.
- `tests/FireDetectionLayout.Tests` — xUnit test suite (`net8.0`) validating wall polygonization, ceiling classification, spacing rules, tile snapping, and snapshot serialization without launching Revit.
- `docs/ARCHITECTURE.md` — Full engineering and drafting specification.

## Building & Debugging in Visual Studio 2022

1. Open `FireDetectionLayout.sln` in Visual Studio 2022.
2. Select the build configuration matching your installed Revit version (for example `Debug.R25` for Revit 2025 or `Debug.R24` for Revit 2024).
   > `Debug.R27` / `Release.R27` (Revit 2027) target .NET 10, which needs Visual Studio 2026 and the .NET 10 SDK. In Visual Studio 2022, use `R23`–`R26`.
   > Nice3point.Revit.Sdk 6.2.2 also runs a .NET 10 MSBuild task for the older Revit configurations. If MSBuild reports it cannot load `System.Runtime, Version=10.0.0.0`, use a build host with .NET 10 available.
3. Press **F5** — `Nice3point.Revit.Sdk` automatically builds, deploys the `.addin` manifest and assemblies, and launches Revit with the debugger attached.
4. In Revit, open the **Fire Layout** ribbon tab → **Fire Detection Layout** to open the dockable review panel.

## Running Unit Tests

```powershell
dotnet test tests\FireDetectionLayout.Tests\FireDetectionLayout.Tests.csproj
```

## Roadmap

- [x] **Milestone 1 (Foundation):** Multi-targeted Nice3point VS22 solution, decoupled `FireDetectionLayout.Core`, JSON `SceneSnapshot` / `PlacementPlan` contracts, physical wall-graph polygonizer, ceiling classifier, baseline AS 1670.1 spacing + tile/plasterboard coordination, WPF dockable pane, and xUnit suite.
- [x] **Milestone 2 (Live Preview & Advanced Visibility Solver):** Visibility-constrained greedy set-cover with sampled boundary/interior reach verification and segment visibility through concave bays and holes; tile-first mixed-finish solving with verified plasterboard-margin spillover; and a non-destructive `DirectContext3D` overlay for compartment status, open edges, approved detector markers, and reach rings. The dockable pane can toggle the overlay and review compartment notes.
- [ ] **Milestone 3 (MEP Coordination & AS 1670.4 Pairing):** Supply-air diffuser / luminaire / sprinkler exclusion buffers, 1200×600 half-tile detector + EWIS speaker pairing, hold-open door triggers, and room-type overrides.
- [ ] **Milestone 4 (Production QA & Reporting):** Per-compartment interactive overrides, hosted family mapper, ownership-tagged updates, and exportable HTML/PDF compliance audit reports.