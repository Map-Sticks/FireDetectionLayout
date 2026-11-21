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

        [Fact]
        public void AnalyzeCompartment_ExposedSpaceUsesLowestOverlappingSoffitElevation()
        {
            var rules = RuleProfile.CreateAs1670Default().Ceilings;
            var compartment = RectPoly(0, 0, 8000, 6000);
            var soffits = new List<CeilingSurface>
            {
                new CeilingSurface
                {
                    ElementId = "Roof-Upper",
                    TypeName = "250mm Concrete Roof Slab",
                    UndersideElevationMm = 7400,
                    Footprint = RectPoly(0, 0, 8000, 6000)
                },
                new CeilingSurface
                {
                    ElementId = "Slab-L2",
                    TypeName = "200mm Concrete Slab",
                    UndersideElevationMm = 3800,
                    Footprint = RectPoly(0, 0, 8000, 6000)
                }
            };

            var analysis = new CeilingClassifier().AnalyzeCompartment(
                compartment, new List<CeilingSurface>(), 2700, rules, soffits);

            Assert.Equal(CeilingKind.ExposedOrNone, analysis.Kind);
            Assert.NotNull(analysis.PrimarySoffit);
            Assert.Equal("Slab-L2", analysis.PrimarySoffit!.ElementId);
            Assert.Equal(3800, analysis.DominantElevationMm);
            Assert.Contains(analysis.Notes, note => note.Contains("200mm Concrete Slab") && note.Contains("3800"));
        }

        [Fact]
        public void AnalyzeCompartment_NearlyCoincidentEdgesPreserveCeilingHoleArea()
        {
            double x = -1520.8249999998925;
            double y = 2744.7875000000017;
            var compartment = RectPoly(x, y, x + 8000, y + 6000);
            var footprint = RectPoly(x - 1e-9, y - 1e-9, x + 8000 + 1e-9, y + 6000 + 1e-9);
            footprint.Holes.Add(RectPoly(x + 2000, y + 2000, x + 4000, y + 4000).OuterRing);
            var ceiling = new CeilingSurface
            {
                ElementId = "C-Precision",
                TypeName = "Plasterboard",
                UndersideElevationMm = 2850,
                Footprint = footprint
            };

            var analysis = new CeilingClassifier().AnalyzeCompartment(
                compartment, new[] { ceiling }, 2700, RuleProfile.CreateAs1670Default().Ceilings);

            Assert.Equal(CeilingKind.Plasterboard, analysis.Kind);
            Assert.Equal(11.0 / 12.0, analysis.CoverageRatio, 8);
            Assert.Equal(11.0 / 12.0, analysis.PlasterboardShareRatio, 8);
            Assert.Same(ceiling, analysis.PrimaryCeiling);
            Assert.Equal(2850, analysis.DominantElevationMm);
        }

        [Fact]
        public void AnalyzeCompartment_SelfIntersectingCeilingIsWithheldWithSourceDiagnostic()
        {
            var ceiling = new CeilingSurface
            {
                ElementId = "C-Invalid",
                SourceModel = "Architecture",
                TypeName = "Plasterboard",
                Footprint = new Polygon2D
                {
                    OuterRing = new List<Point2D>
                    {
                        new Point2D(0, 0), new Point2D(6000, 6000),
                        new Point2D(0, 6000), new Point2D(6000, 0)
                    }
                }
            };

            var analysis = new CeilingClassifier().AnalyzeCompartment(
                RectPoly(0, 0, 6000, 6000), new[] { ceiling }, 2700,
                RuleProfile.CreateAs1670Default().Ceilings);

            Assert.Equal(CeilingKind.Unknown, analysis.Kind);
            Assert.Null(analysis.PrimaryCeiling);
            Assert.Contains(analysis.Notes, note =>
                note.Contains("Architecture") && note.Contains("C-Invalid") && note.Contains("Self-intersection"));
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
