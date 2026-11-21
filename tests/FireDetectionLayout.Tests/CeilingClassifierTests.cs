using System.Collections.Generic;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using Xunit;

namespace FireDetectionLayout.Tests
{
    public class CeilingClassifierTests
    {
        [Fact]
        public void AnalyzeCompartment_DetectsMixedFinishTileAndPlasterboard()
        {
            var rules = RuleProfile.CreateAs1670Default().Ceilings;
            var compartment = RectPoly(0, 0, 8000, 6000);

            var ceilings = new List<CeilingSurface>
            {
                new CeilingSurface
                {
                    ElementId = "C1",
                    TypeName = "600x1200 Acoustic Tile Grid",
                    UndersideElevationMm = 2700,
                    Footprint = RectPoly(1000, 1000, 7000, 5000),
                    TileGrid = new TileGridSpec { PitchUMm = 1200, PitchVMm = 600, IsOriginVerified = true }
                },
                new CeilingSurface
                {
                    ElementId = "C2",
                    TypeName = "13mm Flush Plasterboard Margin",
                    UndersideElevationMm = 2700,
                    Footprint = RectPoly(0, 0, 8000, 1000)
                }
            };

            var analysis = new CeilingClassifier().AnalyzeCompartment(compartment, ceilings, 2700, rules);

            Assert.Equal(CeilingKind.MixedFinish, analysis.Kind);
            Assert.NotNull(analysis.PrimaryTileCeiling);
            Assert.Equal("C1", analysis.PrimaryTileCeiling!.ElementId);
        }

        [Fact]
        public void AnalyzeCompartment_DetectsExposedWhenNoCeilingPresent()
        {
            var rules = RuleProfile.CreateAs1670Default().Ceilings;
            var compartment = RectPoly(0, 0, 5000, 5000);

            var analysis = new CeilingClassifier().AnalyzeCompartment(compartment, new List<CeilingSurface>(), 2700, rules);

            Assert.Equal(CeilingKind.ExposedOrNone, analysis.Kind);
        }

        private static Polygon2D RectPoly(double x1, double y1, double x2, double y2) =>
            new Polygon2D
            {
                OuterRing = new List<Point2D>
                {
                    new Point2D(x1, y1),
                    new Point2D(x2, y1),
                    new Point2D(x2, y2),
                    new Point2D(x1, y2)
                }
            };
    }
}
