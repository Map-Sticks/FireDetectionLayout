# FireDetectionLayout - Architecture & Engineering Specification

## 1. Pipeline

`FireDetectionLayout` lays out point detectors (smoke, heat) and EWIS speakers to **AS 1670.1 / AS 1670.4** style rules on a strict **Extract -> Solve -> Review -> Commit** pipeline:

```text
Revit host + links --(SceneSnapshot)--> FireDetectionLayout.Core  (pure C#, no Revit API)
                                          |- Stage 1: spaces from physical walls (+ Rooms / Spaces / ceilings)
                                          |- Stage 2: grid-first layout per space, per device kind
                                          |- Stage 3: ceiling tile coordination and hosting height
                                          '- Stage 4: re-verification and grading
                                          |
Revit commit  <--(PlacementPlan)--  LayoutSession  <-->  Preview window (edit, regenerate, cancel)
```

| Project | Role |
|---|---|
| `FireDetectionLayout.Core` (`netstandard2.0`) | Geometry, solver, `LayoutSession`, JSON contracts, SVG renderer. Runs off the UI thread and outside Revit. |
| `FireDetectionLayout.Preview` (`net48`, `net8.0-windows`) | `LayoutWorkspace`, `LayoutCanvas`, `LayoutPreviewWindow`. Knows nothing about Revit; the host plugs in Commit / Export / Import / Pick grid origin. |
| `FireDetectionLayout.Revit` | Extraction, settings pane, placement, `DirectContext3D` overlay. Every Revit API call runs through one queued `ExternalEvent`. |
| `FireDetectionLayout.Cli`, `FireDetectionLayout.Viewer` | The solver and the preview window hosted outside Revit for external assessment. |

`SceneSnapshot` (what was read), `RuleProfile` (what is required) and `PlacementPlan` (what is proposed) are plain JSON, in millimetres in host model coordinates.

## 2. Rules

All thresholds live in an editable `RuleProfile`, stored per project. Nothing below is hard-coded.

| Parameter | Default (mm) | Notes |
|---|---:|---|
| Smoke: max spacing / max to wall / min to wall | `10,000` / `5,000` / `500` | |
| Heat: max spacing / max to wall / min to wall | `7,000` / `3,500` / `500` | Rooms matching the heat keywords (kitchen, car park, ...) default to heat |
| Speaker: max spacing / max to wall / min to wall | `10,000` / `5,000` / `500` | AS 1670.4 prescribes none; defaults mirror the detector grid |
| Absolute minimum to wall | `300` | Used only where a space is too small for the required minimum; flagged amber |
| Detector to paired speaker | `600` | Flush ceilings; on tiles the pair follows the tile module |
| Downstand / bulkhead barrier depth | `max(250, 0.10 x H)` | Deep downstands split exposed soffits |
| Wall closure tolerance | `150` | Endpoint snapping when closing wall loops |
| Luminaire / sprinkler / supply diffuser clearance | `300` / `300` / `1,500` | Reported, not enforced |
| Flat-ceiling height limit | `6,000` | Above this the space is flagged for derating |

**Maximum distance to a wall** is measured along the room's own X and Y axes (a square reach, not a circle), so a rotated room is judged in its own frame. It is measured to the wall face: an 11 m room needs two detectors, not one.

**Spacing** is the distance from each device to its nearest neighbour of the same kind.

Room-type rules match whole words in the Room / Space name: exclude (riser, shaft, lift, ... but never "Lift Lobby"), detector only, speaker only, heat. They set the per-space defaults the user can override in the preview.

## 3. Spaces

1. **Walls first.** Walls and downstands that reach the smoke-layer band below the ceiling are noded and polygonised into closed loops. Low partitions and room-separation lines are ignored, so a wall-bounded open-plan area is one space however many Rooms sit in it.
2. **Wall faces.** Loops are built on wall centrelines and then trimmed by each wall's thickness, so every distance is measured from the wall surface.
3. **Exposed downstands.** A deep downstand above ceiling level is hidden where a ceiling passes beneath it and acts as a barrier where the soffit is exposed.
4. **Recovery.** Any Room, MEP Space or ceiling footprint not already claimed by a wall loop becomes a space (with walls standing inside it cut out), so an unclosed loop never loses a room.
5. **Scope.** When rooms are selected in Revit only the spaces containing them are kept.

## 4. Layout

Every candidate is built from **regular room-aligned grids**. For a span `L` the node count per axis starts at `ceil(L / min(spacing, 2 x wall distance))`, with equal bays and a half bay at each wall.

| Strategy | Used for |
|---|---|
| One grid over the whole space | Rectangular and near-rectangular rooms |
| That grid plus infill on its half-lines | Open plan with a core or an odd corner (at most a quarter of the grid again) |
| One grid per rectangular bay | L / T / U shapes and branching corridors, cut along the walls that meet at internal corners |
| Coverage-driven set cover | Last resort; flagged amber |

Candidates are ranked: required wall distance met, then fewest devices, then no fixture clashes, then the simpler arrangement. The ranked list is what **Regenerate** steps through.

Coverage is checked on a room-aligned sampling of the ceiling. Each sampled patch is tested at its centre and at its far end in each of the four frame directions (the wall face for a perimeter patch); every probe must be within wall reach of a device that can see it, so devices do not get credit round corners or behind cores. Small column-sized holes do not block the view.

### Detector + speaker

- **Paired (default):** one grid, a speaker beside every detector; the tighter of the two rule sets governs.
- **Independent:** each kind gets its own grid; where the two land on the same spot one is shifted beside the other, whichever keeps its own coverage compliant.

### Ceiling coordination

| Ceiling | Single device | Detector + speaker |
|---|---|---|
| `1200x600`, `1200x300` | Tile centre | Same tile, each centred in its own half (tile centre +/- 300 along the long side) |
| `600x600` | Tile centre | Neighbouring tiles, each on its tile centre |
| `300x300` | Tile centre | Tile centres 600 apart |
| Flush / plasterboard | Grid node | Straddling the node along the room, 600 apart |
| Tile field with flush margin | Tile centres first | Margin positions only when the tiles cannot reach the walls (amber) |
| Exposed soffit / no ceiling | Grid node at soffit height | As flush (amber: confirm mounting) |
| Sloped | Not laid out | |

Tile snapping prefers, in order: required wall distance, a tile not occupied by a light or diffuser, an uncut tile. Equidistant tiles resolve towards the middle of the room so the layout stays symmetric. A tile arrangement that breaches the wall distance in a narrow corridor loses to one that does not, which is how the pair turns to follow the corridor whichever way the tiles run.

Tile grid geometry comes from `Ceiling.GetCeilingGridLines` (Revit 2025.3+). Before that only the pattern size and direction are available; the origin is unverified (amber) until picked in the ceiling plan.

Each device takes the underside height of the ceiling directly above it, so a bulkhead margin and the main ceiling are both hosted correctly.

## 5. Review and safety

- **Nothing is placed until committed.** The preview and the in-view overlay create no Revit elements.
- **Every edit is re-verified.** Moving, adding or cancelling a device re-runs the same checks as the solver and updates the space's status and notes.
- **Grading.** Green meets the rules. Amber needs a look. Red does not meet the rules: it is held back by default and placed only if the user ticks it.
- **One transaction.** A whole commit is a single undo step.
- **Ownership.** Placed devices are tagged `FIRE_LAYOUT_AUTO:<run>:<space>:<kind>` in Comments. A re-run replaces only its own devices in the spaces being placed.
- **Placement.** Level-based families are placed on the level with an elevation offset; face-based families on the level plane, flipped to face down, with a host offset; a named reference plane is the fallback. Ceiling-hosted families need a ceiling in the host model.

## 6. External assessment

`Export package` writes the snapshot, the capture of the analysed view (with its model extents), the rule profile, the plan and an SVG. `firelayout solve` and `firelayout verify` run the same solver and checks headless; `FireLayoutViewer` is the same preview window. A plan from any source is imported by assigning each device to the space containing it and re-verifying it, so an externally produced layout is held to the same rules as a generated one.
