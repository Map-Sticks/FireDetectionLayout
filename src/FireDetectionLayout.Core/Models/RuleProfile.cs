using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FireDetectionLayout.Core.Models
{
    /// <summary>
    /// Project-configurable AS 1670.1 / AS 1670.4 engineering and drafting rule profile.
    /// </summary>
    public sealed class RuleProfile
    {
        public string ProfileName { get; set; } = "AS 1670.1 / AS 1670.4 Default";
        public LayoutOptions Layout { get; set; } = new LayoutOptions();
        public As1670_1Rules SmokeDetection { get; set; } = new As1670_1Rules();
        public HeatDetectionRules HeatDetection { get; set; } = new HeatDetectionRules();
        public As1670_4Rules Ewis { get; set; } = new As1670_4Rules();
        public CeilingClassificationRules Ceilings { get; set; } = new CeilingClassificationRules();
        public MepClearanceRules MepClearances { get; set; } = new MepClearanceRules();
        public RoomTypeRules RoomRules { get; set; } = new RoomTypeRules();

        public static RuleProfile CreateAs1670Default() => new RuleProfile();

        /// <summary>Resolves the spacing requirements that apply to one device kind.</summary>
        public DeviceSpacing GetSpacing(DeviceRole role)
        {
            double maxSpacing, maxWall, minWall;
            switch (role)
            {
                case DeviceRole.HeatDetector:
                    maxSpacing = HeatDetection.MaxDetectorSpacingMm;
                    maxWall = HeatDetection.MaxWallDistanceMm;
                    minWall = HeatDetection.MinWallClearanceMm;
                    break;
                case DeviceRole.EwisSpeaker:
                    maxSpacing = Ewis.MaxSpeakerSpacingMm;
                    maxWall = Ewis.MaxWallDistanceMm;
                    minWall = Ewis.MinWallClearanceMm;
                    break;
                default:
                    maxSpacing = SmokeDetection.MaxDetectorSpacingMm;
                    maxWall = SmokeDetection.MaxWallDistanceMm;
                    minWall = SmokeDetection.MinWallClearanceMm;
                    break;
            }

            minWall = Math.Max(0.0, minWall);
            return new DeviceSpacing
            {
                MaxSpacingMm = Math.Max(500.0, maxSpacing),
                MaxWallDistanceMm = Math.Max(250.0, maxWall),
                MinWallClearanceMm = minWall,
                HardMinWallClearanceMm = Math.Min(minWall, Math.Max(0.0, SmokeDetection.HardMinWallClearanceMm))
            };
        }
    }

    /// <summary>Resolved spacing requirements for one device kind (all millimetres).</summary>
    public sealed class DeviceSpacing
    {
        /// <summary>Maximum centre-to-centre spacing between adjacent devices.</summary>
        public double MaxSpacingMm { get; set; }

        /// <summary>Maximum distance from a device to a wall, measured along the room X and Y axes.</summary>
        public double MaxWallDistanceMm { get; set; }

        /// <summary>Required minimum distance from a device to a wall.</summary>
        public double MinWallClearanceMm { get; set; }

        /// <summary>Absolute floor used only when a space is too small to honour the required minimum.</summary>
        public double HardMinWallClearanceMm { get; set; }

        public static DeviceSpacing MostStringent(DeviceSpacing a, DeviceSpacing b) => new DeviceSpacing
        {
            MaxSpacingMm = Math.Min(a.MaxSpacingMm, b.MaxSpacingMm),
            MaxWallDistanceMm = Math.Min(a.MaxWallDistanceMm, b.MaxWallDistanceMm),
            MinWallClearanceMm = Math.Max(a.MinWallClearanceMm, b.MinWallClearanceMm),
            HardMinWallClearanceMm = Math.Max(a.HardMinWallClearanceMm, b.HardMinWallClearanceMm)
        };
    }

    public enum SpeakerLayoutMode
    {
        /// <summary>One speaker beside every detector on a common grid (most stringent spacing governs).</summary>
        PairedWithDetectors,

        /// <summary>Speakers solved on their own grid; coincident devices are tidied into adjacent pairs.</summary>
        IndependentGrid
    }

    public sealed class LayoutOptions
    {
        public bool PlaceDetectors { get; set; } = true;
        public bool PlaceSpeakers { get; set; } = false;

        /// <summary>Detector kind used for rooms that no room-type rule assigns to heat detection.</summary>
        public DeviceRole DefaultDetectorKind { get; set; } = DeviceRole.SmokeDetector;

        /// <summary>
        /// True: devices are coordinated with ceiling tile grids (tile centres).
        /// False: layout only - a symmetric room grid that ignores tile set-out.
        /// </summary>
        public bool CoordinateWithCeilingGrid { get; set; } = true;

        /// <summary>Prefer uncut tiles over cut perimeter tiles when coordinating with a tile grid.</summary>
        public bool PreferFullTiles { get; set; } = true;
    }

    public sealed class As1670_1Rules
    {
        /// <summary>Max spacing between adjacent point smoke detectors on a flat ceiling (mm).</summary>
        public double MaxDetectorSpacingMm { get; set; } = 10000.0;

        /// <summary>Max distance from any physical wall face to the nearest detector (mm).</summary>
        public double MaxWallDistanceMm { get; set; } = 5000.0;

        /// <summary>Required minimum clearance from walls/bulkheads (mm).</summary>
        public double MinWallClearanceMm { get; set; } = 500.0;

        /// <summary>Absolute minimum clearance from walls for spaces too small for the required minimum (mm).</summary>
        public double HardMinWallClearanceMm { get; set; } = 300.0;

        /// <summary>Minimum downstand depth (mm) or fraction of ceiling height that acts as a smoke barrier.</summary>
        public double MinBulkheadBarrierDepthMm { get; set; } = 250.0;
        public double BulkheadBarrierHeightRatio { get; set; } = 0.10;

        /// <summary>Tolerance for snapping wall endpoints when closing physical smoke compartments (mm).</summary>
        public double WallClosureToleranceMm { get; set; } = 150.0;

        /// <summary>Hold-open fire/smoke door detector proximity trigger (mm).</summary>
        public double FireDoorProximityMm { get; set; } = 1500.0;

        /// <summary>Concealed ceiling void depth threshold for engineer review flag (mm).</summary>
        public double ConcealedVoidFlagDepthMm { get; set; } = 800.0;

        /// <summary>Minimum physical compartment area (m^2) to receive detection.</summary>
        public double MinCompartmentAreaSqM { get; set; } = 1.5;

        /// <summary>Ceiling height above which AS 1670.1 height derating flag is raised (mm).</summary>
        public double StandardFlatCeilingMaxHeightMm { get; set; } = 6000.0;
    }

    public sealed class HeatDetectionRules
    {
        /// <summary>Max spacing between adjacent point heat detectors on a flat ceiling (mm).</summary>
        public double MaxDetectorSpacingMm { get; set; } = 7000.0;

        /// <summary>Max distance from any wall to the nearest heat detector (mm).</summary>
        public double MaxWallDistanceMm { get; set; } = 3500.0;

        /// <summary>Required minimum clearance from walls/bulkheads (mm).</summary>
        public double MinWallClearanceMm { get; set; } = 500.0;

        /// <summary>Rooms whose name matches one of these words default to heat detection.</summary>
        public List<string> RoomKeywords { get; set; } = new List<string>
        {
            "kitchen", "kitchenette", "car park", "carpark", "garage", "loading dock", "laundry"
        };
    }

    public sealed class As1670_4Rules
    {
        public SpeakerLayoutMode LayoutMode { get; set; } = SpeakerLayoutMode.PairedWithDetectors;

        /// <summary>Max spacing between adjacent ceiling speakers (mm). Defaults mirror the detector grid.</summary>
        public double MaxSpeakerSpacingMm { get; set; } = 10000.0;

        /// <summary>Max distance from any wall to the nearest speaker (mm).</summary>
        public double MaxWallDistanceMm { get; set; } = 5000.0;

        /// <summary>Required minimum clearance from walls/bulkheads (mm).</summary>
        public double MinWallClearanceMm { get; set; } = 500.0;

        /// <summary>Centre-to-centre distance between a co-located detector and speaker (mm).</summary>
        public double PairedDeviceOffsetMm { get; set; } = 600.0;
    }

    public sealed class CeilingClassificationRules
    {
        public List<string> TileKeywords { get; set; } = new List<string>
        {
            "tile", "grid", "acoustic", "1200x600", "1200 x 600", "600x600", "600 x 600",
            "1200x300", "1200 x 300", "300x300", "300 x 300", "t-bar", "suspended", "mineral"
        };

        public List<string> PlasterboardKeywords { get; set; } = new List<string>
        {
            "plaster", "gyprock", "flush", "gwb", "gypsum", "set", "villaboard", "fc", "fibre cement"
        };

        public double SlopedCeilingThresholdDegrees { get; set; } = 10.0;
        public double MinMixedFinishShareRatio { get; set; } = 0.10;
    }

    public sealed class MepClearanceRules
    {
        public double SupplyDiffuserClearanceMm { get; set; } = 1500.0;
        public double ReturnGrilleClearanceMm { get; set; } = 500.0;
        public double LuminaireClearanceMm { get; set; } = 300.0;
        public double SprinklerClearanceMm { get; set; } = 300.0;
    }

    public sealed class RoomTypeRules
    {
        /// <summary>Rooms matching these words receive no devices by default.</summary>
        public List<string> ExcludeKeywords { get; set; } = new List<string>
        {
            "riser", "shaft", "chute", "lift", "elevator", "void", "duct"
        };

        /// <summary>A room matching one of these words is never auto-excluded (e.g. "Lift Lobby").</summary>
        public List<string> ExcludeExceptionKeywords { get; set; } = new List<string>
        {
            "lobby", "foyer", "corridor", "motor room", "machine room"
        };

        public List<string> DetectorOnlyKeywords { get; set; } = new List<string>
        {
            "bedroom", "inpatient"
        };

        public List<string> SpeakerOnlyKeywords { get; set; } = new List<string>
        {
            "ensuite", "shower", "bath"
        };

        /// <summary>
        /// Whole-word, case-insensitive keyword match. A keyword also matches a word it prefixes
        /// ("bath" matches "Bathroom") but not one it is buried in ("lift" does not match "Uplift").
        /// </summary>
        public static bool MatchesAny(string text, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return keywords.Any(keyword =>
                !string.IsNullOrWhiteSpace(keyword) &&
                Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(keyword.Trim()), RegexOptions.IgnoreCase));
        }
    }
}
