using System;
using System.Collections.Generic;

namespace FireDetectionLayout.Core.Models
{
    /// <summary>
    /// Self-contained, Revit-independent snapshot of a floor or selected area.
    /// All linear coordinates and elevations are stored in millimetres (host model coordinates).
    /// </summary>
    public sealed class SceneSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public SnapshotMetadata Metadata { get; set; } = new SnapshotMetadata();
        public List<WallBarrier> Walls { get; set; } = new List<WallBarrier>();
        public List<CeilingSurface> Ceilings { get; set; } = new List<CeilingSurface>();
        public List<CeilingSurface> Soffits { get; set; } = new List<CeilingSurface>();
        public List<RoomMetadata> Rooms { get; set; } = new List<RoomMetadata>();
        public List<RoomMetadata> Spaces { get; set; } = new List<RoomMetadata>();
        public List<MepObstacle> Obstacles { get; set; } = new List<MepObstacle>();
        public List<DoorFeature> Doors { get; set; } = new List<DoorFeature>();
        public List<ExistingFireDevice> ExistingDevices { get; set; } = new List<ExistingFireDevice>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    public sealed class SnapshotMetadata
    {
        public string DocumentTitle { get; set; } = string.Empty;
        public string RevitVersion { get; set; } = string.Empty;
        public string ActiveViewName { get; set; } = string.Empty;
        public string LevelName { get; set; } = string.Empty;
        public double LevelElevationMm { get; set; }
        public double DefaultCeilingHeightMm { get; set; } = 2700.0;
        public string PhaseName { get; set; } = string.Empty;
        public DateTime ExtractedUtc { get; set; } = DateTime.UtcNow;
        public List<string> IncludedLinks { get; set; } = new List<string>();
    }

    public sealed class WallBarrier
    {
        public string ElementId { get; set; } = string.Empty;
        public string SourceModel { get; set; } = "Host";
        public string TypeName { get; set; } = string.Empty;
        public Segment2D Centerline { get; set; } = new Segment2D();
        public double ThicknessMm { get; set; }
        public double BaseElevationMm { get; set; }
        public double TopElevationMm { get; set; }
        public bool IsStructuralBeamOrBulkhead { get; set; }
        public bool IsCurtainWall { get; set; }
        public bool IsRoomBounding { get; set; } = true;
    }

    public sealed class CeilingSurface
    {
        public string ElementId { get; set; } = string.Empty;
        public string SourceModel { get; set; } = "Host";
        public string TypeName { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public double UndersideElevationMm { get; set; }
        public double TopElevationMm { get; set; }
        public double SlopeDegrees { get; set; }
        public Polygon2D Footprint { get; set; } = new Polygon2D();
        public TileGridSpec? TileGrid { get; set; }
    }

    public sealed class RoomMetadata
    {
        public string ElementId { get; set; } = string.Empty;
        public string SourceModel { get; set; } = "Host";
        public string Number { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsSpace { get; set; }
        public bool IsEnclosed { get; set; } = true;
        public double AreaSqM { get; set; }
        public double UnboundedHeightMm { get; set; }
        public Point2D LocationPoint { get; set; } = new Point2D();
        public Polygon2D? Footprint { get; set; }
    }

    public enum MepObstacleCategory
    {
        LightingFixture,
        SupplyAirDiffuser,
        ReturnAirGrille,
        Sprinkler,
        AccessPanel,
        Other
    }

    public sealed class MepObstacle
    {
        public string ElementId { get; set; } = string.Empty;
        public string SourceModel { get; set; } = "Host";
        public MepObstacleCategory Category { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public Point2D Center { get; set; } = new Point2D();
        public BoundingBox2D Bounds { get; set; } = new BoundingBox2D();
        public double ElevationMm { get; set; }
    }

    public sealed class DoorFeature
    {
        public string ElementId { get; set; } = string.Empty;
        public string SourceModel { get; set; } = "Host";
        public string TypeName { get; set; } = string.Empty;
        public Point2D Location { get; set; } = new Point2D();
        public bool IsFireOrSmokeDoor { get; set; }
    }

    public sealed class ExistingFireDevice
    {
        public string ElementId { get; set; } = string.Empty;
        public string FamilyName { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public Point2D Location { get; set; } = new Point2D();
        public double ElevationMm { get; set; }
        public bool IsToolOwned { get; set; }
    }
}
