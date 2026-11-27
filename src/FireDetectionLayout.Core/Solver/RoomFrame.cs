using System;
using System.Collections.Generic;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Core.Solver
{
    /// <summary>
    /// Orthogonal (U, V) layout frame of a space. Grids, wall distances and device pairs are all
    /// measured along these two axes, so a rotated room still receives a room-aligned layout.
    /// </summary>
    internal sealed class RoomFrame
    {
        public Point2D AxisU { get; private set; } = new Point2D(1, 0);
        public Point2D AxisV { get; private set; } = new Point2D(0, 1);
        public double MinU { get; private set; }
        public double MaxU { get; private set; }
        public double MinV { get; private set; }
        public double MaxV { get; private set; }
        public double SpanU => MaxU - MinU;
        public double SpanV => MaxV - MinV;

        /// <summary>Axis along the longer side of the space (the travel direction of a corridor).</summary>
        public Point2D LongAxis => SpanU >= SpanV ? AxisU : AxisV;
        public Point2D ShortAxis => SpanU >= SpanV ? AxisV : AxisU;

        public double AngleDegrees
        {
            get
            {
                double angle = Math.Atan2(AxisU.Y, AxisU.X) * 180.0 / Math.PI;
                while (angle > 45.0) angle -= 90.0;
                while (angle <= -45.0) angle += 90.0;
                return Math.Abs(angle) < 1e-6 ? 0.0 : angle;
            }
        }

        public Point2D ToWorld(double u, double v) =>
            new Point2D(u * AxisU.X + v * AxisV.X, u * AxisU.Y + v * AxisV.Y);

        public double U(Point2D p) => p.X * AxisU.X + p.Y * AxisU.Y;
        public double V(Point2D p) => p.X * AxisV.X + p.Y * AxisV.Y;

        public static RoomFrame Compute(Polygon2D compartmentPolygon, TileGridSpec? preferredGrid)
        {
            Point2D axisU = new Point2D(1, 0);

            if (preferredGrid != null &&
                (Math.Abs(preferredGrid.AxisU.X) > 1e-6 || Math.Abs(preferredGrid.AxisU.Y) > 1e-6))
            {
                double len = Math.Sqrt(
                    preferredGrid.AxisU.X * preferredGrid.AxisU.X + preferredGrid.AxisU.Y * preferredGrid.AxisU.Y);
                axisU = new Point2D(preferredGrid.AxisU.X / len, preferredGrid.AxisU.Y / len);
            }
            else if (compartmentPolygon.OuterRing.Count >= 3)
            {
                axisU = ComputeDominantEdgeAxis(compartmentPolygon.OuterRing) ?? axisU;
            }

            return FromAxis(compartmentPolygon, axisU);
        }

        /// <summary>Frame with a prescribed U axis, sized to the polygon.</summary>
        public static RoomFrame FromAxis(Polygon2D compartmentPolygon, Point2D axisU)
        {
            var frame = new RoomFrame
            {
                AxisU = axisU,
                AxisV = new Point2D(-axisU.Y, axisU.X)
            };

            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            foreach (var pt in compartmentPolygon.OuterRing)
            {
                double u = frame.U(pt);
                double v = frame.V(pt);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            frame.MinU = minU;
            frame.MaxU = maxU;
            frame.MinV = minV;
            frame.MaxV = maxV;
            return frame;
        }

        /// <summary>Length-weighted dominant wall direction, folded into a single 90 degree quadrant.</summary>
        private static Point2D? ComputeDominantEdgeAxis(IReadOnlyList<Point2D> ring)
        {
            var binWeights = new double[90];
            var edges = new List<(double Angle, double Length)>();

            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 100.0)
                    continue;

                double angle = Math.Atan2(dy, dx);
                while (angle < 0) angle += Math.PI * 0.5;
                while (angle >= Math.PI * 0.5) angle -= Math.PI * 0.5;

                int bin = Math.Min(89, (int)Math.Floor(angle * 180.0 / Math.PI));
                binWeights[bin] += length;
                edges.Add((angle, length));
            }

            int bestBin = 0;
            double bestWeight = -1.0;
            for (int b = 0; b < 90; b++)
            {
                double neighborhood = binWeights[(b + 89) % 90] + binWeights[b] + binWeights[(b + 1) % 90];
                if (neighborhood > bestWeight)
                {
                    bestWeight = neighborhood;
                    bestBin = b;
                }
            }

            if (bestWeight <= 0)
                return null;

            double targetRad = (bestBin + 0.5) * Math.PI / 180.0;
            double sumSin = 0, sumCos = 0;
            foreach (var edge in edges)
            {
                double diff = Math.Abs(edge.Angle - targetRad);
                diff = Math.Min(diff, Math.PI * 0.5 - diff);
                if (diff <= 3.0 * Math.PI / 180.0)
                {
                    // 4x angle circular mean handles the 90 degree periodicity of wall directions.
                    sumSin += edge.Length * Math.Sin(4.0 * edge.Angle);
                    sumCos += edge.Length * Math.Cos(4.0 * edge.Angle);
                }
            }

            double dominantAngle = (Math.Abs(sumSin) > 1e-9 || Math.Abs(sumCos) > 1e-9)
                ? 0.25 * Math.Atan2(sumSin, sumCos)
                : targetRad;
            if (dominantAngle < 0) dominantAngle += Math.PI * 0.5;

            // Snap near-axis-aligned rooms (within 0.25 deg) cleanly to world axes.
            if (dominantAngle < 0.25 * Math.PI / 180.0 ||
                Math.Abs(dominantAngle - Math.PI * 0.5) < 0.25 * Math.PI / 180.0)
                dominantAngle = 0.0;

            return new Point2D(Math.Cos(dominantAngle), Math.Sin(dominantAngle));
        }
    }
}
