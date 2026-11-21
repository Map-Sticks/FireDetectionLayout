using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;

namespace FireDetectionLayout.Core.Geometry
{
    public static class VisibilityGeometry
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        public static bool IsVisible(Polygon compartment, Point2D from, Point2D to)
        {
            double dist = from.DistanceTo(to);
            if (dist < 1.0)
                return true;

            // Pull the target endpoint slightly inward along the ray so boundary target points
            // on diagonal/rotated walls do not fail NTS Covers() due to floating-point drift.
            double inset = System.Math.Min(0.5, dist * 0.01);
            double scale = (dist - inset) / dist;
            var line = Factory.CreateLineString(new[]
            {
                new Coordinate(from.X, from.Y),
                new Coordinate(from.X + (to.X - from.X) * scale, from.Y + (to.Y - from.Y) * scale)
            });
            return compartment.Covers(line);
        }
    }
}
