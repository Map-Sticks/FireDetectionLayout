using System;
using System.Collections.Generic;

namespace FireDetectionLayout.Core.Models
{
    public sealed class Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2D() { }

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double DistanceTo(Point2D other)
        {
            var dx = X - other.X;
            var dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public sealed class Segment2D
    {
        public Point2D Start { get; set; } = new Point2D();
        public Point2D End { get; set; } = new Point2D();

        public Segment2D() { }

        public Segment2D(Point2D start, Point2D end)
        {
            Start = start;
            End = end;
        }

        public double Length => Start.DistanceTo(End);
    }

    public sealed class BoundingBox2D
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        public double Width => Math.Max(0, MaxX - MinX);
        public double Height => Math.Max(0, MaxY - MinY);
        public Point2D Center => new Point2D((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

        public bool Contains(Point2D p, double tolerance = 0.0) =>
            p.X >= MinX - tolerance && p.X <= MaxX + tolerance &&
            p.Y >= MinY - tolerance && p.Y <= MaxY + tolerance;

        public bool Intersects(BoundingBox2D other, double tolerance = 0.0) =>
            MinX <= other.MaxX + tolerance && MaxX >= other.MinX - tolerance &&
            MinY <= other.MaxY + tolerance && MaxY >= other.MinY - tolerance;

        public static BoundingBox2D FromPoints(IEnumerable<Point2D> points)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            bool any = false;

            foreach (var p in points)
            {
                any = true;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            return any
                ? new BoundingBox2D { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY }
                : new BoundingBox2D();
        }
    }

    public sealed class Polygon2D
    {
        public List<Point2D> OuterRing { get; set; } = new List<Point2D>();
        public List<List<Point2D>> Holes { get; set; } = new List<List<Point2D>>();

        public BoundingBox2D GetBounds() => BoundingBox2D.FromPoints(OuterRing);
    }

    /// <summary>
    /// Instance-aligned ceiling tile grid in host model coordinates (millimetres).
    /// </summary>
    public sealed class TileGridSpec
    {
        public Point2D OriginMm { get; set; } = new Point2D();
        public Point2D AxisU { get; set; } = new Point2D(1, 0);
        public Point2D AxisV { get; set; } = new Point2D(0, 1);
        public double PitchUMm { get; set; }
        public double PitchVMm { get; set; }
        public bool IsOriginVerified { get; set; }
        public string Source { get; set; } = "Unknown";
    }
}
