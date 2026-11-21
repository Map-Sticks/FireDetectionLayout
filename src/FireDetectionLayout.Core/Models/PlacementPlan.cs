using System;
using System.Collections.Generic;

namespace FireDetectionLayout.Core.Models
{
    public enum CompartmentStatus
    {
        Green,
        Amber,
        Red,
        Skipped
    }

    public enum CeilingKind
    {
        Unknown,
        SuspendedTile,
        Plasterboard,
        MixedFinish,
        ExposedOrNone,
        Sloped
    }

    public enum DeviceRole
    {
        SmokeDetector,
        EwisSpeaker
    }

    public sealed class PlacementPlan
    {
        public string RunId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
        public string ProfileName { get; set; } = string.Empty;
        public List<SmokeCompartment> Compartments { get; set; } = new List<SmokeCompartment>();
        public List<ProposedDevice> Devices { get; set; } = new List<ProposedDevice>();
        public List<Segment2D> UnresolvedOpenEdges { get; set; } = new List<Segment2D>();
        public PlacementSummary Summary { get; set; } = new PlacementSummary();
    }

    public sealed class SmokeCompartment
    {
        public string CompartmentId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> RoomNames { get; set; } = new List<string>();
        public Polygon2D Boundary { get; set; } = new Polygon2D();
        public double AreaSqM { get; set; }
        public double CeilingElevationMm { get; set; }
        public CeilingKind CeilingKind { get; set; } = CeilingKind.Unknown;
        public CompartmentStatus Status { get; set; } = CompartmentStatus.Green;
        public bool IsApprovedForPlacement { get; set; } = true;
        public int ProposedDetectorCount { get; set; }
        public int ProposedSpeakerCount { get; set; }
        public double WorstCaseWallDistanceMm { get; set; }
        public double MaxDeviceSpacingMm { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
    }

    public sealed class ProposedDevice
    {
        public string DeviceId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        public string CompartmentId { get; set; } = string.Empty;
        public DeviceRole Role { get; set; } = DeviceRole.SmokeDetector;
        public Point2D Location { get; set; } = new Point2D();
        public double ElevationMm { get; set; }
        public double RotationDegrees { get; set; }
        public string? HostCeilingElementId { get; set; }
        public CeilingKind HostCeilingKind { get; set; }
        public bool WasSnappedToTile { get; set; }
        public bool WasNudgedForObstacle { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class PlacementSummary
    {
        public int TotalCompartments { get; set; }
        public int GreenCount { get; set; }
        public int AmberCount { get; set; }
        public int RedCount { get; set; }
        public int SkippedCount { get; set; }
        public int TotalSmokeDetectors { get; set; }
        public int TotalEwisSpeakers { get; set; }
        public int UnresolvedOpenEdgeCount { get; set; }
    }
}
