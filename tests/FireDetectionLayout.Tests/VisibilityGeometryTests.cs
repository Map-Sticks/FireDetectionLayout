using System.Linq;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using Xunit;

namespace FireDetectionLayout.Tests
{
    public class VisibilityGeometryTests
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        [Fact]
        public void IsVisible_ReturnsFalseWhenLineCrossesReentrantCorner()
        {
            var compartment = CreatePolygon(new[]
            {
                new Coordinate(0, 0), new Coordinate(10000, 0), new Coordinate(10000, 4000),
                new Coordinate(4000, 4000), new Coordinate(4000, 10000), new Coordinate(0, 10000),
                new Coordinate(0, 0)
            });

            Assert.False(VisibilityGeometry.IsVisible(
                compartment, new Point2D(2000, 8000), new Point2D(8000, 2000)));
        }

        [Fact]
        public void IsVisible_ReturnsFalseWhenLineCrossesInteriorHole()
        {
            var shell = Factory.CreateLinearRing(new[]
            {
                new Coordinate(0, 0), new Coordinate(10000, 0), new Coordinate(10000, 10000),
                new Coordinate(0, 10000), new Coordinate(0, 0)
            });
            var hole = Factory.CreateLinearRing(new[]
            {
                new Coordinate(4000, 4000), new Coordinate(6000, 4000), new Coordinate(6000, 6000),
                new Coordinate(4000, 6000), new Coordinate(4000, 4000)
            });
            var compartment = Factory.CreatePolygon(shell, new[] { hole });

            Assert.False(VisibilityGeometry.IsVisible(
                compartment, new Point2D(2000, 5000), new Point2D(8000, 5000)));
            Assert.True(VisibilityGeometry.IsVisible(
                compartment, new Point2D(2000, 2000), new Point2D(3000, 3000)));
        }

        private static Polygon CreatePolygon(IReadOnlyList<Coordinate> coordinates) =>
            Factory.CreatePolygon(Factory.CreateLinearRing(coordinates.ToArray()));
    }
}
