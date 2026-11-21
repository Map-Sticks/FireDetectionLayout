using System.Collections.Generic;

namespace FireDetectionLayout.Core.Models
{
    /// <summary>
    /// Project-configurable AS 1670.1 / AS 1670.4 engineering and drafting rule profile.
    /// </summary>
    public sealed class RuleProfile
    {
        public string ProfileName { get; set; } = "AS 1670.1 / AS 1670.4 Default";
        public As1670_1Rules SmokeDetection { get; set; } = new As1670_1Rules();
        public As1670_4Rules Ewis { get; set; } = new As1670_4Rules();
        public CeilingClassificationRules Ceilings { get; set; } = new CeilingClassificationRules();
        public MepClearanceRules MepClearances { get; set; } = new MepClearanceRules();
        public RoomTypeRules RoomRules { get; set; } = new RoomTypeRules();

        public static RuleProfile CreateAs1670Default() => new RuleProfile();
    }

    public sealed class As1670_1Rules
    {
        /// <summary>Max spacing between adjacent point smoke detectors on a flat ceiling (mm).</summary>
        public double MaxDetectorSpacingMm { get; set; } = 10000.0;

        /// <summary>Max distance from any physical wall face to the nearest detector (mm).</summary>
        public double MaxWallDistanceMm { get; set; } = 5000.0;

        /// <summary>Preferred minimum clearance from walls/bulkheads (mm).</summary>
        public double MinWallClearanceMm { get; set; } = 500.0;

        /// <summary>Absolute minimum clearance from walls when nudging (mm).</summary>
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

    public sealed class As1670_4Rules
    {
        public bool EnablePairedEwisSpeakers { get; set; } = false;
        public double PairedDeviceOffsetMm { get; set; } = 600.0;
    }

    public sealed class CeilingClassificationRules
    {
        public List<string> TileKeywords { get; set; } = new List<string>
        {
            "tile", "grid", "acoustic", "1200x600", "1200 x 600", "600x600", "600 x 600", "t-bar", "suspended", "mineral"
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
        public List<string> ExcludeKeywords { get; set; } = new List<string>
        {
            "riser", "shaft", "chute", "lift", "elevator", "void", "duct"
        };

        public List<string> DetectorOnlyKeywords { get; set; } = new List<string>
        {
            "bedroom", "inpatient"
        };

        public List<string> SpeakerOnlyKeywords { get; set; } = new List<string>
        {
            "ensuite", "shower", "bath"
        };
    }
}
