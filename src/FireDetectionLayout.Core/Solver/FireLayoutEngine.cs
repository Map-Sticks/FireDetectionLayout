using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Core.Solver
{
    /// <summary>
    /// End-to-end deterministic AS 1670.1 / AS 1670.4 layout solver.
    /// Pipeline: Stage 1 (physical wall compartments) -> Stage 2 (grid-first spacing)
    /// -> Stage 3 (ceiling tile coordination & MEP clearance) -> Stage 4 (compliance grading).
    /// Use <see cref="CreateSession"/> when the result will be reviewed and edited before it is
    /// committed; <see cref="Solve"/> is the one-shot form.
    /// </summary>
    public sealed class FireLayoutEngine
    {
        public PlacementPlan Solve(SceneSnapshot snapshot, RuleProfile? profile = null) =>
            CreateSession(snapshot, profile).Plan;

        public LayoutSession CreateSession(
            SceneSnapshot snapshot,
            RuleProfile? profile = null,
            LayoutSession? previous = null) =>
            new LayoutSession(snapshot, profile, previous);
    }
}
