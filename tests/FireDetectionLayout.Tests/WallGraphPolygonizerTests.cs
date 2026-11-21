using System.Collections.Generic;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using Xunit;

namespace FireDetectionLayout.Tests
{
    public class WallGraphPolygonizerTests
    {
        [Fact]
        public void BuildCompartments_ClosedRoomIgnoresLowPartition()
        {
            var rules = RuleProfile.CreateAs1670Default().SmokeDetection;
            var walls = new List<WallBarrier>
            {
                MakeWall(0, 0, 8000, 0, 0, 3000),
                MakeWall(8000, 0, 8000, 6000, 0, 3000),
                MakeWall(8000, 6000, 0, 6000, 0, 3000),
                MakeWall(0, 6000, 0, 0, 0, 3000),
                // Low half-height partition (1200 mm) must NOT split the physical smoke compartment.
                MakeWall(4000, 0, 4000, 6000, 0, 1200)
            };

            var result = new WallGraphPolygonizer().BuildCompartments(walls, 0, 2700, rules);

            Assert.Single(result.Compartments);
            Assert.Equal(1, result.FilteredOutLowWalls);
            Assert.Empty(result.DanglingEdges);
        }

        [Fact]
        public void BuildCompartments_ReportsUnclosedWallAsDanglingEdge()
        {
            var rules = RuleProfile.CreateAs1670Default().SmokeDetection;
            var walls = new List<WallBarrier>
            {
                MakeWall(0, 0, 6000, 0, 0, 3000),
                MakeWall(6000, 0, 6000, 6000, 0, 3000),
                MakeWall(6000, 6000, 0, 6000, 0, 3000)
            };

            var result = new WallGraphPolygonizer().BuildCompartments(walls, 0, 2700, rules);

            Assert.Empty(result.Compartments);
            Assert.NotEmpty(result.DanglingEdges);
        }

        private static WallBarrier MakeWall(double x1, double y1, double x2, double y2, double baseZ, double topZ) =>
            new WallBarrier
            {
                Centerline = new Segment2D(new Point2D(x1, y1), new Point2D(x2, y2)),
                BaseElevationMm = baseZ,
                TopElevationMm = topZ
            };
    }
}
