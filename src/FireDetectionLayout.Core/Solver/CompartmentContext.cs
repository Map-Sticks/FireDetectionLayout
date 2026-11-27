using System;
using System.Collections.Generic;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace FireDetectionLayout.Core.Solver
{
    /// <summary>One ceiling element clipped against a space, used to host devices at the right height.</summary>
    internal class CeilingZone
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        public CeilingZone(CeilingSurface ceiling, CeilingKind kind, Polygon footprint)
        {
            Ceiling = ceiling;
            Kind = kind;
            Footprint = footprint;
            Prepared = PreparedGeometryFactory.Prepare(footprint);
        }

        public CeilingSurface Ceiling { get; }
        public CeilingKind Kind { get; }
        public Polygon Footprint { get; }
        public IPreparedGeometry Prepared { get; }

        public bool Contains(Point2D point) => Prepared.Covers(Factory.CreatePoint(new Coordinate(point.X, point.Y)));
    }

    /// <summary>A suspended tile ceiling with a readable grid.</summary>
    internal sealed class TileZone : CeilingZone
    {
        public TileZone(CeilingSurface ceiling, Polygon footprint, TileGridSpec grid)
            : base(ceiling, CeilingKind.SuspendedTile, footprint)
        {
            double lenU = Math.Sqrt(grid.AxisU.X * grid.AxisU.X + grid.AxisU.Y * grid.AxisU.Y);
            AxisU = lenU > 1e-9 ? new Point2D(grid.AxisU.X / lenU, grid.AxisU.Y / lenU) : new Point2D(1, 0);
            AxisV = new Point2D(-AxisU.Y, AxisU.X);

            // Keep V pointing the way the source grid declared it so tile indices stay stable.
            if (grid.AxisV.X * AxisV.X + grid.AxisV.Y * AxisV.Y < 0)
                AxisV = new Point2D(-AxisV.X, -AxisV.Y);

            Origin = grid.OriginMm;
            PitchU = grid.PitchUMm;
            PitchV = grid.PitchVMm;
            Grid = grid;
        }

        public TileGridSpec Grid { get; }
        public Point2D Origin { get; }
        public Point2D AxisU { get; }
        public Point2D AxisV { get; }
        public double PitchU { get; }
        public double PitchV { get; }

        public double GridU(Point2D p) => (p.X - Origin.X) * AxisU.X + (p.Y - Origin.Y) * AxisU.Y;
        public double GridV(Point2D p) => (p.X - Origin.X) * AxisV.X + (p.Y - Origin.Y) * AxisV.Y;

        public Point2D FromGrid(double u, double v) => new Point2D(
            Origin.X + u * AxisU.X + v * AxisV.X,
            Origin.Y + u * AxisU.Y + v * AxisV.Y);

        public string SizeLabel
        {
            get
            {
                double longSide = Math.Round(Math.Max(PitchU, PitchV));
                double shortSide = Math.Round(Math.Min(PitchU, PitchV));
                return $"{longSide:F0}x{shortSide:F0}";
            }
        }
    }

    /// <summary>A sampled patch of ceiling that must be within wall reach of a device.</summary>
    internal sealed class CoverageTarget
    {
        public Point2D Point = new Point2D();
        public double U;
        public double V;

        // How far the patch extends from the sample point along each frame direction (mm).
        public double ExtentUPlus;
        public double ExtentUMinus;
        public double ExtentVPlus;
        public double ExtentVMinus;
    }

    /// <summary>Everything the solver caches about one space between solves and edits.</summary>
    internal sealed class CompartmentContext
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        public CompartmentContext(SmokeCompartment compartment, Polygon geometry)
        {
            Compartment = compartment;
            Geometry = geometry;
            Boundary = geometry.Boundary;
            Prepared = PreparedGeometryFactory.Prepare(geometry);
            Visibility = BuildVisibilityPolygon(geometry);
            PreparedVisibility = PreparedGeometryFactory.Prepare(Visibility);
            VisibilityBoundary = Visibility.Boundary;
            IsConvex = geometry.NumInteriorRings == 0 &&
                       Math.Abs(geometry.ConvexHull().Area - geometry.Area) <= geometry.Area * 1e-6;
        }

        public SmokeCompartment Compartment { get; }
        public Polygon Geometry { get; }
        public NtsGeometry Boundary { get; }
        public IPreparedGeometry Prepared { get; }

        /// <summary>The space with small column/riser holes filled, which do not block smoke travel.</summary>
        public Polygon Visibility { get; }
        public IPreparedGeometry PreparedVisibility { get; }
        public NtsGeometry VisibilityBoundary { get; }

        /// <summary>Convex spaces need no line-of-sight tests.</summary>
        public bool IsConvex { get; }

        public RoomMetadata? SourceRoom { get; set; }
        public string? DiscoveryNote { get; set; }
        public string? SolveError { get; set; }
        public CompartmentCeilingAnalysis Ceiling { get; set; } = new CompartmentCeilingAnalysis();
        public List<CeilingZone> CeilingZones { get; } = new List<CeilingZone>();
        public List<TileZone> TileZones { get; } = new List<TileZone>();

        /// <summary>Plan outlines of ceiling fixtures (lights, diffusers, grilles) a device cannot share a spot with.</summary>
        public List<BoundingBox2D> FixtureBoxes { get; } = new List<BoundingBox2D>();
        public RoomFrame Frame { get; set; } = new RoomFrame();
        public List<CoverageTarget> Targets { get; set; } = new List<CoverageTarget>();
        public double TargetStepMm { get; set; } = 1500.0;
        public double TargetEdgeInsetMm { get; set; } = 240.0;

        /// <summary>Status and notes that come from the building model rather than from the layout.</summary>
        public CompartmentStatus BaseStatus { get; set; } = CompartmentStatus.Green;
        public List<string> BaseNotes { get; } = new List<string>();

        /// <summary>Set when the geometry cannot be laid out automatically (sloped / invalid ceiling).</summary>
        public bool IsWithheld { get; set; }

        public bool DefaultIncludeDetectors { get; set; } = true;
        public bool DefaultIncludeSpeakers { get; set; } = true;
        public DeviceRole DefaultDetectorKind { get; set; } = DeviceRole.SmokeDetector;
        public bool HasUserDeviceOverride { get; set; }
        public bool HasUserDetectorKindOverride { get; set; }
        public List<string> RuleNotes { get; } = new List<string>();

        public List<LayoutOption>? Options { get; set; }

        /// <summary>Speaker alternatives when speakers are solved on their own grid.</summary>
        public List<LayoutOption>? SpeakerOptions { get; set; }
        public string OptionsSignature { get; set; } = string.Empty;
        public LayoutOption? CurrentOption { get; set; }
        public LayoutOption? CurrentSpeakerOption { get; set; }
        public List<ProposedDevice> Devices { get; } = new List<ProposedDevice>();

        public bool Covers(Point2D point) => Prepared.Covers(ToPoint(point));

        /// <summary>True when the point sits on top of a ceiling fixture.</summary>
        public bool IsOnFixture(Point2D point)
        {
            foreach (var box in FixtureBoxes)
            {
                if (box.Contains(point, 50.0))
                    return true;
            }

            return false;
        }

        public double BoundaryDistance(Point2D point) => Boundary.Distance(ToPoint(point));

        public bool IsVisible(Point2D from, Point2D to) =>
            IsConvex || VisibilityGeometry.IsVisible(PreparedVisibility, from, to);

        public static Point ToPoint(Point2D point) => Factory.CreatePoint(new Coordinate(point.X, point.Y));

        private static Polygon BuildVisibilityPolygon(Polygon compartment)
        {
            if (compartment.NumInteriorRings == 0)
                return compartment;

            // Small interior holes (< 1 m^2 and < 1000 mm wide, e.g. structural columns/risers)
            // do not obstruct smoke travel, whereas full rooms/cores do.
            var blockingHoles = new List<LinearRing>();
            var factory = compartment.Factory;
            for (int i = 0; i < compartment.NumInteriorRings; i++)
            {
                var ring = (LinearRing)compartment.GetInteriorRingN(i);
                var holePoly = factory.CreatePolygon(ring);
                var env = holePoly.EnvelopeInternal;
                if (holePoly.Area >= 1_000_000.0 || Math.Max(env.Width, env.Height) >= 1000.0)
                    blockingHoles.Add(ring);
            }

            if (blockingHoles.Count == compartment.NumInteriorRings)
                return compartment;

            return factory.CreatePolygon((LinearRing)compartment.ExteriorRing, blockingHoles.ToArray());
        }
    }
}
