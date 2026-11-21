using System;
using System.Collections.Generic;
using System.Linq;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Valid;

namespace FireDetectionLayout.Core.Geometry
{
    public sealed class CompartmentCeilingAnalysis
    {
        public CeilingKind Kind { get; set; } = CeilingKind.ExposedOrNone;
        public double DominantElevationMm { get; set; }
        public CeilingSurface? PrimaryTileCeiling { get; set; }
        public CeilingSurface? PrimaryCeiling { get; set; }
        public CeilingSurface? PrimarySoffit { get; set; }
        public double CoverageRatio { get; set; }
        public double TileShareRatio { get; set; }
        public double PlasterboardShareRatio { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Classifies individual Revit ceilings and the combined ceiling finish inside
    /// a physical wall-bounded smoke compartment.
    /// </summary>
    public sealed class CeilingClassifier
    {
        public CeilingKind ClassifySurface(CeilingSurface ceiling, CeilingClassificationRules rules)
        {
            if (Math.Abs(ceiling.SlopeDegrees) >= rules.SlopedCeilingThresholdDegrees)
                return CeilingKind.Sloped;

            if (ceiling.TileGrid != null && ceiling.TileGrid.PitchUMm > 0 && ceiling.TileGrid.PitchVMm > 0)
                return CeilingKind.SuspendedTile;

            string text = $"{ceiling.TypeName} {ceiling.MaterialName}".ToLowerInvariant();

            if (rules.TileKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k.ToLowerInvariant())))
                return CeilingKind.SuspendedTile;

            if (rules.PlasterboardKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k.ToLowerInvariant())))
                return CeilingKind.Plasterboard;

            return CeilingKind.Unknown;
        }

        public CompartmentCeilingAnalysis AnalyzeCompartment(
            Polygon2D compartmentPolygon,
            IReadOnlyList<CeilingSurface> ceilings,
            double fallbackElevationMm,
            CeilingClassificationRules rules,
            IReadOnlyList<CeilingSurface>? soffits = null)
        {
            var analysis = new CompartmentCeilingAnalysis
            {
                DominantElevationMm = fallbackElevationMm
            };

            var compNts = CreateValidatedPolygon(compartmentPolygon, "compartment boundary", analysis);
            if (compNts == null)
                return analysis;

            double compArea = compNts.Area;
            if (compArea <= 1.0)
                return analysis;

            var compBounds = compartmentPolygon.GetBounds();
            if (ceilings.Count == 0)
            {
                analysis.Kind = CeilingKind.ExposedOrNone;
                ResolveExposedSoffit(compNts, compBounds, soffits, fallbackElevationMm, analysis);
                return analysis;
            }

            double tileArea = 0, plasterArea = 0, slopedArea = 0, unknownArea = 0;
            double bestTileArea = 0, bestAnyArea = 0;

            foreach (var ceiling in ceilings)
            {
                if (!compBounds.Intersects(ceiling.Footprint.GetBounds(), 10.0))
                    continue;

                string ceilingLabel = $"ceiling '{ceiling.SourceModel}:{ceiling.ElementId}'";
                var ceilNts = CreateValidatedPolygon(ceiling.Footprint, ceilingLabel, analysis);
                if (ceilNts == null)
                    return analysis;

                double overlap;
                try
                {
                    // Legacy overlay can fail on near-coincident edges after Revit unit/link transforms.
                    overlap = OverlayNGRobust.Overlay(compNts, ceilNts, SpatialFunction.Intersection).Area;
                }
                catch (TopologyException error)
                {
                    analysis.Kind = CeilingKind.Unknown;
                    analysis.Notes.Add(
                        $"Overlap failed for {ceilingLabel}: {error.Message}. Automatic placement withheld.");
                    return analysis;
                }

                if (overlap < 10_000.0) // < 0.01 m^2
                    continue;

                var kind = ClassifySurface(ceiling, rules);
                switch (kind)
                {
                    case CeilingKind.SuspendedTile:
                        tileArea += overlap;
                        if (overlap > bestTileArea)
                        {
                            bestTileArea = overlap;
                            analysis.PrimaryTileCeiling = ceiling;
                        }
                        break;
                    case CeilingKind.Plasterboard:
                        plasterArea += overlap;
                        break;
                    case CeilingKind.Sloped:
                        slopedArea += overlap;
                        break;
                    default:
                        unknownArea += overlap;
                        break;
                }

                if (overlap > bestAnyArea)
                {
                    bestAnyArea = overlap;
                    analysis.PrimaryCeiling = ceiling;
                    analysis.DominantElevationMm = ceiling.UndersideElevationMm;
                }
            }

            double totalCovered = tileArea + plasterArea + slopedArea + unknownArea;
            analysis.CoverageRatio = Math.Min(1.0, totalCovered / compArea);
            analysis.TileShareRatio = tileArea / compArea;
            analysis.PlasterboardShareRatio = plasterArea / compArea;

            if (analysis.CoverageRatio < 0.25)
            {
                analysis.Kind = CeilingKind.ExposedOrNone;
                analysis.PrimaryCeiling = null;
                analysis.PrimaryTileCeiling = null;
                ResolveExposedSoffit(compNts, compBounds, soffits, fallbackElevationMm, analysis);
            }
            else if (slopedArea > totalCovered * 0.25)
            {
                analysis.Kind = CeilingKind.Sloped;
                analysis.Notes.Add("Sloped ceiling detected; verify AS 1670.1 ridge/slope spacing.");
            }
            else if (analysis.TileShareRatio >= rules.MinMixedFinishShareRatio &&
                     analysis.PlasterboardShareRatio >= rules.MinMixedFinishShareRatio)
            {
                analysis.Kind = CeilingKind.MixedFinish;
                analysis.Notes.Add("Mixed finish (tiled field + plasterboard margin): assess full wall compartment, prefer tiles.");
            }
            else if (tileArea >= plasterArea && tileArea > 0)
            {
                analysis.Kind = CeilingKind.SuspendedTile;
            }
            else if (plasterArea > 0)
            {
                analysis.Kind = CeilingKind.Plasterboard;
            }
            else
            {
                analysis.Kind = CeilingKind.Unknown;
                analysis.Notes.Add("Ceiling type not recognized as tile or plasterboard; confirm classification.");
            }

            return analysis;
        }

        private static void ResolveExposedSoffit(
            Polygon compNts,
            BoundingBox2D compBounds,
            IReadOnlyList<CeilingSurface>? soffits,
            double fallbackElevationMm,
            CompartmentCeilingAnalysis analysis)
        {
            CeilingSurface? bestSoffit = null;
            double bestOverlap = 0.0;
            double lowestElevMm = double.PositiveInfinity;

            if (soffits != null)
            {
                foreach (var soffit in soffits)
                {
                    if (soffit.Footprint.OuterRing.Count < 3 ||
                        !compBounds.Intersects(soffit.Footprint.GetBounds(), 10.0))
                        continue;

                    Polygon soffitNts;
                    try
                    {
                        soffitNts = WallGraphPolygonizer.ToNtsPolygon(soffit.Footprint);
                        if (new IsValidOp(soffitNts).ValidationError != null)
                            continue;
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    double overlap;
                    try
                    {
                        overlap = OverlayNGRobust.Overlay(compNts, soffitNts, SpatialFunction.Intersection).Area;
                    }
                    catch (TopologyException)
                    {
                        continue;
                    }

                    if (overlap < 10_000.0)
                        continue;

                    // Prefer the lowest overhead soffit (e.g. floor slab directly above); break ties by overlap area.
                    const double elevTieToleranceMm = 50.0;
                    if (soffit.UndersideElevationMm < lowestElevMm - elevTieToleranceMm ||
                        (Math.Abs(soffit.UndersideElevationMm - lowestElevMm) <= elevTieToleranceMm && overlap > bestOverlap))
                    {
                        lowestElevMm = soffit.UndersideElevationMm;
                        bestOverlap = overlap;
                        bestSoffit = soffit;
                    }
                }
            }

            if (bestSoffit != null)
            {
                analysis.PrimarySoffit = bestSoffit;
                analysis.DominantElevationMm = bestSoffit.UndersideElevationMm;
                string soffitLabel = string.IsNullOrWhiteSpace(bestSoffit.TypeName)
                    ? "overhead soffit"
                    : $"overhead soffit '{bestSoffit.TypeName}'";
                analysis.Notes.Add(
                    $"No ceiling detected; using {soffitLabel} at {bestSoffit.UndersideElevationMm:F0} mm. Confirm exposed soffit mounting.");
            }
            else
            {
                analysis.DominantElevationMm = fallbackElevationMm;
                analysis.Notes.Add(
                    $"No ceiling or overhead soffit detected; using fallback elevation ({fallbackElevationMm:F0} mm). Confirm soffit height and mounting.");
            }
        }

        private static Polygon? CreateValidatedPolygon(
            Polygon2D footprint,
            string label,
            CompartmentCeilingAnalysis analysis)
        {
            if (footprint.OuterRing.Count < 3 || footprint.Holes.Any(hole => hole.Count < 3))
            {
                analysis.Kind = CeilingKind.Unknown;
                analysis.Notes.Add(
                    $"Invalid {label}: a boundary ring has fewer than three points. Automatic placement withheld.");
                return null;
            }

            Polygon polygon;
            try
            {
                polygon = WallGraphPolygonizer.ToNtsPolygon(footprint);
            }
            catch (ArgumentException error)
            {
                analysis.Kind = CeilingKind.Unknown;
                analysis.Notes.Add($"Invalid {label}: {error.Message}. Automatic placement withheld.");
                return null;
            }

            var validationError = new IsValidOp(polygon).ValidationError;
            if (validationError != null)
            {
                analysis.Kind = CeilingKind.Unknown;
                analysis.Notes.Add($"Invalid {label}: {validationError}. Automatic placement withheld.");
                return null;
            }

            return polygon;
        }
    }
}
