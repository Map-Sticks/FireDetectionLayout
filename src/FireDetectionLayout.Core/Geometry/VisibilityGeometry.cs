using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;

namespace FireDetectionLayout.Core.Geometry
{
    public static class VisibilityGeometry
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        public static bool IsVisible(Polygon compartment, Point2D from, Point2D to)
        {
            if (from.DistanceTo(to) < 1.0)
                return true;

            var line = Factory.CreateLineString(new[]
            {
                new Coordinate(from.X, from.Y),
                new Coordinate(to.X, to.Y)
            });
            return compartment.Covers(line);
        }
    }
}
