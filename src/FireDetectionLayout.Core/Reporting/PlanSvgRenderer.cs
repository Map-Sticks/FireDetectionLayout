using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Core.Reporting
{
    /// <summary>
    /// Renders a solved plan as a standalone SVG drawing (model millimetres, north up) for
    /// external review, QA records and offline assessment outside Revit.
    /// </summary>
    public static class PlanSvgRenderer
    {
        private const double MarginMm = 1500.0;
        private const int MaxTileLinesPerCeiling = 1500;

        public static string Render(SceneSnapshot snapshot, PlacementPlan plan)
        {
            var bounds = ComputeBounds(snapshot, plan);
            double width = bounds.Width + 2 * MarginMm;
            double height = bounds.Height + 2 * MarginMm;
            double originX = bounds.MinX - MarginMm;
            double originY = bounds.MaxY + MarginMm;

            // SVG Y runs down the page; model Y runs up.
            string X(double x) => F(x - originX);
            string Y(double y) => F(originY - y);

            void AppendRing(StringBuilder builder, IReadOnlyList<Point2D> ring)
            {
                for (int i = 0; i < ring.Count; i++)
                    builder.Append(i == 0 ? "M" : "L").Append(X(ring[i].X)).Append(' ').Append(Y(ring[i].Y)).Append(' ');
                if (ring.Count > 0)
                    builder.Append("Z ");
            }

            string Path(Polygon2D polygon)
            {
                var builder = new StringBuilder();
                AppendRing(builder, polygon.OuterRing);
                foreach (var hole in polygon.Holes)
                    AppendRing(builder, hole);
                return builder.ToString();
            }

            double unit = Math.Max(width, height) / 1000.0;
            double textSize = Math.Max(120.0, unit * 9.0);
            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
                .Append(F(width)).Append(' ').Append(F(height))
                .Append("\" font-family=\"Segoe UI, Arial, sans-serif\">\n");
            svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");

            // Ceilings and their tile grids.
            int clipIndex = 0;
            foreach (var ceiling in snapshot.Ceilings)
            {
                if (ceiling.Footprint.OuterRing.Count < 3)
                    continue;

                bool tiled = ceiling.TileGrid != null && ceiling.TileGrid.PitchUMm > 0 && ceiling.TileGrid.PitchVMm > 0;
                string path = Path(ceiling.Footprint);
                svg.Append("<path d=\"").Append(path).Append("\" fill-rule=\"evenodd\" fill=\"")
                    .Append(tiled ? "#f3f7fb" : "#faf7f0").Append("\" stroke=\"none\"/>\n");
                if (!tiled)
                    continue;

                string clipId = "ceiling" + clipIndex++;
                svg.Append("<clipPath id=\"").Append(clipId).Append("\"><path d=\"").Append(path)
                    .Append("\" clip-rule=\"evenodd\"/></clipPath>\n");
                svg.Append("<g clip-path=\"url(#").Append(clipId).Append(")\" stroke=\"#c5d3e0\" stroke-width=\"")
                    .Append(F(Math.Max(6.0, unit * 0.5))).Append("\">\n");
                foreach (var line in TileGridLines(ceiling.TileGrid!, ceiling.Footprint.GetBounds()))
                {
                    svg.Append("<line x1=\"").Append(X(line.Start.X)).Append("\" y1=\"").Append(Y(line.Start.Y))
                        .Append("\" x2=\"").Append(X(line.End.X)).Append("\" y2=\"").Append(Y(line.End.Y)).Append("\"/>\n");
                }

                svg.Append("</g>\n");
            }

            // Spaces, coloured by review status.
            foreach (var compartment in plan.Compartments)
            {
                string colour = StatusColour(compartment.Status);
                svg.Append("<path d=\"").Append(Path(compartment.Boundary)).Append("\" fill-rule=\"evenodd\" fill=\"")
                    .Append(colour).Append("\" fill-opacity=\"0.10\" stroke=\"").Append(colour)
                    .Append("\" stroke-width=\"").Append(F(Math.Max(15.0, unit * 1.2))).Append("\"/>\n");
            }

            foreach (var wall in snapshot.Walls)
            {
                svg.Append("<line x1=\"").Append(X(wall.Centerline.Start.X)).Append("\" y1=\"").Append(Y(wall.Centerline.Start.Y))
                    .Append("\" x2=\"").Append(X(wall.Centerline.End.X)).Append("\" y2=\"").Append(Y(wall.Centerline.End.Y))
                    .Append("\" stroke=\"#374151\" stroke-linecap=\"square\" stroke-width=\"")
                    .Append(F(Math.Max(wall.ThicknessMm, Math.Max(25.0, unit * 2.0)))).Append("\"/>\n");
            }

            foreach (var edge in plan.UnresolvedOpenEdges)
            {
                svg.Append("<line x1=\"").Append(X(edge.Start.X)).Append("\" y1=\"").Append(Y(edge.Start.Y))
                    .Append("\" x2=\"").Append(X(edge.End.X)).Append("\" y2=\"").Append(Y(edge.End.Y))
                    .Append("\" stroke=\"#ff00b4\" stroke-dasharray=\"").Append(F(unit * 6)).Append("\" stroke-width=\"")
                    .Append(F(Math.Max(20.0, unit * 1.5))).Append("\"/>\n");
            }

            foreach (var obstacle in snapshot.Obstacles)
            {
                svg.Append("<rect x=\"").Append(X(obstacle.Bounds.MinX)).Append("\" y=\"").Append(Y(obstacle.Bounds.MaxY))
                    .Append("\" width=\"").Append(F(obstacle.Bounds.Width)).Append("\" height=\"").Append(F(obstacle.Bounds.Height))
                    .Append("\" fill=\"none\" stroke=\"#9ca3af\" stroke-width=\"").Append(F(Math.Max(8.0, unit * 0.6))).Append("\"/>\n");
            }

            // Device symbols.
            // Capped so a detector and the speaker 600 mm beside it never overlap.
            double symbol = Math.Min(240.0, Math.Max(110.0, unit * 7.0));
            var approved = new HashSet<string>(
                plan.Compartments.Where(c => c.IsApprovedForPlacement).Select(c => c.CompartmentId));
            foreach (var device in plan.Devices)
            {
                bool placed = !device.IsExcluded && approved.Contains(device.CompartmentId);
                string colour = device.Role == DeviceRole.EwisSpeaker ? "#7c3aed"
                    : device.Role == DeviceRole.HeatDetector ? "#ea580c" : "#0e7490";
                string cx = X(device.Location.X), cy = Y(device.Location.Y);
                svg.Append("<g opacity=\"").Append(placed ? "1" : "0.35").Append("\">");
                if (device.Role == DeviceRole.EwisSpeaker)
                {
                    svg.Append("<rect x=\"").Append(X(device.Location.X - symbol)).Append("\" y=\"").Append(Y(device.Location.Y + symbol))
                        .Append("\" width=\"").Append(F(symbol * 2)).Append("\" height=\"").Append(F(symbol * 2))
                        .Append("\" fill=\"#ffffff\" stroke=\"").Append(colour).Append("\" stroke-width=\"").Append(F(symbol * 0.22)).Append("\"/>");
                }
                else
                {
                    svg.Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\" r=\"").Append(F(symbol))
                        .Append("\" fill=\"#ffffff\" stroke=\"").Append(colour).Append("\" stroke-width=\"").Append(F(symbol * 0.22)).Append("\"/>");
                }

                svg.Append("<text x=\"").Append(cx).Append("\" y=\"").Append(F(originY - device.Location.Y + symbol * 0.38))
                    .Append("\" text-anchor=\"middle\" font-size=\"").Append(F(symbol * (device.Role == DeviceRole.EwisSpeaker ? 0.8 : 1.1)))
                    .Append("\" font-weight=\"700\" fill=\"").Append(colour).Append("\">").Append(device.Role.ToShortCode()).Append("</text>");
                if (device.IsExcluded)
                {
                    svg.Append("<line x1=\"").Append(X(device.Location.X - symbol)).Append("\" y1=\"").Append(Y(device.Location.Y - symbol))
                        .Append("\" x2=\"").Append(X(device.Location.X + symbol)).Append("\" y2=\"").Append(Y(device.Location.Y + symbol))
                        .Append("\" stroke=\"#dc2626\" stroke-width=\"").Append(F(symbol * 0.25)).Append("\"/>");
                }

                svg.Append("</g>\n");
            }

            foreach (var compartment in plan.Compartments)
            {
                if (compartment.Boundary.OuterRing.Count < 3)
                    continue;

                var box = compartment.Boundary.GetBounds();
                svg.Append("<text x=\"").Append(X(box.MinX + textSize * 0.5)).Append("\" y=\"")
                    .Append(F(originY - box.MaxY + textSize * 1.3)).Append("\" font-size=\"").Append(F(textSize))
                    .Append("\" fill=\"#111827\">").Append(Escape(compartment.DisplayName)).Append("</text>\n");
            }

            svg.Append("</svg>\n");
            return svg.ToString();
        }

        /// <summary>Tile joints crossing a bounding box, in model coordinates.</summary>
        public static IEnumerable<Segment2D> TileGridLines(TileGridSpec grid, BoundingBox2D bounds)
        {
            if (grid.PitchUMm <= 0 || grid.PitchVMm <= 0)
                yield break;

            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            foreach (var corner in new[]
                     {
                         new Point2D(bounds.MinX, bounds.MinY), new Point2D(bounds.MaxX, bounds.MinY),
                         new Point2D(bounds.MaxX, bounds.MaxY), new Point2D(bounds.MinX, bounds.MaxY)
                     })
            {
                double dx = corner.X - grid.OriginMm.X, dy = corner.Y - grid.OriginMm.Y;
                double u = dx * grid.AxisU.X + dy * grid.AxisU.Y;
                double v = dx * grid.AxisV.X + dy * grid.AxisV.Y;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            Point2D World(double u, double v) => new Point2D(
                grid.OriginMm.X + u * grid.AxisU.X + v * grid.AxisV.X,
                grid.OriginMm.Y + u * grid.AxisU.Y + v * grid.AxisV.Y);

            int firstU = (int)Math.Floor(minU / grid.PitchUMm), lastU = (int)Math.Ceiling(maxU / grid.PitchUMm);
            int firstV = (int)Math.Floor(minV / grid.PitchVMm), lastV = (int)Math.Ceiling(maxV / grid.PitchVMm);
            if ((long)(lastU - firstU) + (lastV - firstV) > MaxTileLinesPerCeiling)
                yield break;

            for (int i = firstU; i <= lastU; i++)
                yield return new Segment2D(World(i * grid.PitchUMm, minV), World(i * grid.PitchUMm, maxV));
            for (int j = firstV; j <= lastV; j++)
                yield return new Segment2D(World(minU, j * grid.PitchVMm), World(maxU, j * grid.PitchVMm));
        }

        private static BoundingBox2D ComputeBounds(SceneSnapshot snapshot, PlacementPlan plan)
        {
            var points = new List<Point2D>();
            foreach (var wall in snapshot.Walls)
            {
                points.Add(wall.Centerline.Start);
                points.Add(wall.Centerline.End);
            }

            foreach (var compartment in plan.Compartments)
                points.AddRange(compartment.Boundary.OuterRing);
            foreach (var ceiling in snapshot.Ceilings)
                points.AddRange(ceiling.Footprint.OuterRing);
            foreach (var device in plan.Devices)
                points.Add(device.Location);

            return points.Count > 0
                ? BoundingBox2D.FromPoints(points)
                : new BoundingBox2D { MaxX = 1000, MaxY = 1000 };
        }

        private static string StatusColour(CompartmentStatus status)
        {
            switch (status)
            {
                case CompartmentStatus.Green: return "#16a34a";
                case CompartmentStatus.Amber: return "#d97706";
                case CompartmentStatus.Red: return "#dc2626";
                default: return "#9ca3af";
            }
        }

        private static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static string Escape(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
