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
        EwisSpeaker,
        HeatDetector
    }

    public static class DeviceRoleExtensions
    {
        public static bool IsDetector(this DeviceRole role) =>
            role == DeviceRole.SmokeDetector || role == DeviceRole.HeatDetector;

        public static string ToShortCode(this DeviceRole role)
        {
            switch (role)
            {
                case DeviceRole.HeatDetector: return "H";
                case DeviceRole.EwisSpeaker: return "SPK";
                default: return "S";
            }
        }

        public static string ToDisplayName(this DeviceRole role)
        {
            switch (role)
            {
                case DeviceRole.HeatDetector: return "Heat detector";
                case DeviceRole.EwisSpeaker: return "Speaker";
                default: return "Smoke detector";
            }
        }
    }

    public sealed class PlacementPlan
    {
        public string RunId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
        public string ProfileName { get; set; } = string.Empty;
        public string LevelName { get; set; } = string.Empty;
        public string LevelElementId { get; set; } = string.Empty;
        public List<SmokeCompartment> Compartments { get; set; } = new List<SmokeCompartment>();
        public List<ProposedDevice> Devices { get; set; } = new List<ProposedDevice>();
        public List<Segment2D> UnresolvedOpenEdges { get; set; } = new List<Segment2D>();
        public List<string> Warnings { get; set; } = new List<string>();
        public PlacementSummary Summary { get; set; } = new PlacementSummary();
    }

    public sealed class SmokeCompartment
    {
        public string CompartmentId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SourceKind { get; set; } = "Walls";
        public List<string> RoomNames { get; set; } = new List<string>();
        public List<string> RoomElementIds { get; set; } = new List<string>();
        public Polygon2D Boundary { get; set; } = new Polygon2D();
        public double AreaSqM { get; set; }
        public double CeilingElevationMm { get; set; }
        public CeilingKind CeilingKind { get; set; } = CeilingKind.Unknown;

        /// <summary>Tile module of the coordinated ceiling grid, e.g. "1200x600"; empty when not tiled.</summary>
        public string TileSize { get; set; } = string.Empty;

        /// <summary>Tile grid the layout was coordinated with (null for flush ceilings or layout-only runs).</summary>
        public TileGridSpec? TileGrid { get; set; }

        public CompartmentStatus Status { get; set; } = CompartmentStatus.Green;
        public bool IsApprovedForPlacement { get; set; } = true;

        /// <summary>Whether detectors are laid out in this space (room-type rule default, user overridable).</summary>
        public bool IncludeDetectors { get; set; } = true;

        /// <summary>Whether speakers are laid out in this space (room-type rule default, user overridable).</summary>
        public bool IncludeSpeakers { get; set; } = true;

        /// <summary>Detector kind for this space (smoke or heat).</summary>
        public DeviceRole DetectorKind { get; set; } = DeviceRole.SmokeDetector;

        /// <summary>Zero-based index of the layout alternative currently shown.</summary>
        public int VariantIndex { get; set; }

        /// <summary>Number of distinct compliant layout alternatives found for this space.</summary>
        public int VariantCount { get; set; }

        /// <summary>Short description of the selected alternative, e.g. "2 x 1 grid, pair in tile".</summary>
        public string LayoutDescription { get; set; } = string.Empty;

        /// <summary>True once the user has moved, added or cancelled a device in this space.</summary>
        public bool IsUserModified { get; set; }

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

        /// <summary>Shared by a co-located detector and speaker.</summary>
        public string? PairId { get; set; }

        /// <summary>True when the user cancelled this device; it is kept for review but never placed.</summary>
        public bool IsExcluded { get; set; }

        /// <summary>True when the user moved or added this device, or it came from an imported plan.</summary>
        public bool IsManual { get; set; }

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
        public int TotalHeatDetectors { get; set; }
        public int TotalEwisSpeakers { get; set; }
        public int ExcludedDeviceCount { get; set; }
        public int UnresolvedOpenEdgeCount { get; set; }
    }
}
