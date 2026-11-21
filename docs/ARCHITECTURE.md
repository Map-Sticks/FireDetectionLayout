# FireDetectionLayout — Architecture & Engineering Specification

## 1. Design Philosophy

`FireDetectionLayout` is a professional Revit add-in for automated, auditable **AS 1670.1** (smoke detection) and **AS 1670.4** (optional paired EWIS occupant warning speaker) layout and ceiling coordination.

It follows a strict **Extract → Solve → Review → Commit** architecture:

```text
Revit Host + Links  ──(SceneSnapshot)──▶  FireDetectionLayout.Core (Pure C#, No Revit API)
                                                │
                                                ├─ Stage 1: Physical Wall-Graph Smoke Compartments
                                                ├─ Stage 2: AS 1670.1 / 1670.4 Baseline Spacing
                                                ├─ Stage 3: Ceiling Recognition & Drafting Coordination
                                                └─ Stage 4: Post-Snap Compliance Re-Check & Grading
                                                │
Revit Atomic Commit ◀──(PlacementPlan)──  Dockable Review Panel (Green / Amber / Red)
```

### Key Architectural Decisions

1. **Decoupled Solver (`FireDetectionLayout.Core`, `netstandard2.0`):**
   - Zero Revit API dependencies.
   - Runs on a background thread so Revit stays responsive.
   - Supports JSON serialization of `SceneSnapshot`, `RuleProfile`, and `PlacementPlan` for offline debugging, QA archiving, and fast xUnit regression testing against real project floors.
2. **Native Multi-Targeted Revit Add-in (`FireDetectionLayout.Revit`, `Nice3point.Revit.Sdk`):**
   - Targets Revit 2023–2027 (`Debug.R23`..`Debug.R27` / `Release.R23`..`Release.R27`) in Visual Studio 2022 with automatic add-in deployment and F5 debugging.
   - Extracts host + linked Architectural, Structural, and Mechanical geometry using link transforms and active view phase mapping.
3. **Walls First, Ceilings Second (with Closed-Loop Re-Verification):**
   - Physical walls and downstands in the ceiling smoke-layer band define the true smoke compartments (ignoring Revit Room Separator lines and low partitions).
   - Baseline AS 1670.1 spacing is solved against the physical compartment first.
   - Ceilings are then classified (`SuspendedTile`, `Plasterboard`, `MixedFinish`, `ExposedOrNone`, `Sloped`) and coordinated with tile grids and MEP obstacles.
   - Every snapped/coordinated point is re-verified against wall-reach and neighbour-spacing limits; failures escalate automatically or mark the compartment `Red`.

---

## 2. Engineering Rules (AS 1670.1 & AS 1670.4)

All thresholds live in an editable `RuleProfile` (JSON per project), never hardcoded:

| Parameter | Default (mm) | Notes |
|---|---:|---|
| Max detector-to-detector spacing | `10,000` | Flat ceiling baseline (editable to 7,000–9,000 mm for project spec margin) |
| Max distance to physical wall | `5,000` | Half of max spacing |
| Min wall / bulkhead clearance | `500` | Hard floor `300` mm |
| Min AC supply diffuser clearance | `1,500` | Configurable `1,000`–`1,500` mm |
| Min luminaire / sprinkler clearance | `300` | Avoids clash with ceiling fixtures |
| Hold-open fire/smoke door trigger | `1,500` | Flags/seeds detector within 1.5 m of door |
| Downstand / bulkhead barrier depth | `max(250, 0.10 × H)` | Deep beams/bulkheads split smoke pockets |
| Concealed ceiling void review threshold | `800` | Flags voids > 800 mm for engineer review |
| Paired detector + EWIS speaker pitch | `600` | Centres of 600×600 halves on a 1200×600 tile |

---

## 3. Ceiling Coordination Strategy

- **Suspended Acoustic Tile Grid (`1200×600`, `600×600`):**
  - Revit 2025.3+: reads exact instance grid lines via `Ceiling.GetCeilingGridLines(False)`.
  - Revit 2023–2025.2 fallback: reads fill-pattern pitch/orientation + cached per-ceiling 2-click origin.
  - Snaps devices to full uncut tile centres (or 600×600 half-tile centres when paired with an EWIS speaker), never on T-bars or occupied MEP tiles.
- **Plasterboard / Flush-Set:**
  - Keeps architectural symmetry along room centrelines and dominant wall axes, nudging cleanly around luminaires/diffusers.
- **Mixed Finish (Tiled Field + Plasterboard Margin):**
  - Assesses AS 1670.1 reach over the full physical wall-bounded compartment (including the margin), prefers placing on full tiles, and only spills onto the plasterboard margin when wall reach requires it (flagged `Amber`).
- **Exposed Soffit / No Ceiling / Sloped:**
  - Explicitly flagged (`Amber`/`Red`) rather than silently placing at an assumed elevation.

---

## 4. User Control & Safety Guarantees

- **Traffic-Light Grading:** `Green` (auto-ready), `Amber` (review/confirm), `Red` (withheld—open wall edge, ambiguous ceiling, or coverage gap).
- **Explicit Confirmation:** No Revit elements are created until the user reviews the plan and confirms.
- **Single Atomic Transaction:** All placements occur in one Revit transaction (`Ctrl+Z` reverts the entire run).
- **Tool Ownership Tagging:** Tool-created elements carry a metadata tag so re-runs never overwrite manually placed devices.
