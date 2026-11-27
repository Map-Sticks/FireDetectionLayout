using System.Collections.ObjectModel;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Revit.ViewModels
{
    /// <summary>A loaded family type that can be mapped to a device kind.</summary>
    public sealed class FamilySymbolOption
    {
        public FamilySymbolOption(ElementId symbolId, string familyName, string typeName)
        {
            SymbolId = symbolId;
            FamilyName = familyName;
            TypeName = typeName;
        }

        public ElementId SymbolId { get; }
        public string FamilyName { get; }
        public string TypeName { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(TypeName)
            ? FamilyName
            : $"{FamilyName} : {TypeName}";

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// One device kind in the rules table: the family type it places and the spacing
    /// requirements that family must satisfy.
    /// </summary>
    public sealed partial class DeviceKindRow : ObservableObject
    {
        public DeviceKindRow(DeviceRole role, string label, ObservableCollection<FamilySymbolOption> families)
        {
            Role = role;
            Label = label;
            Families = families;
        }

        public DeviceRole Role { get; }
        public string Label { get; }
        public ObservableCollection<FamilySymbolOption> Families { get; }

        [ObservableProperty]
        private FamilySymbolOption? _family;

        /// <summary>Maximum spacing between two devices of this kind (mm).</summary>
        [ObservableProperty]
        private double _maxSpacingMm;

        /// <summary>Maximum distance from a device to a wall along the room X and Y axes (mm).</summary>
        [ObservableProperty]
        private double _maxWallDistanceMm;

        /// <summary>Minimum distance from a device to a wall (mm).</summary>
        [ObservableProperty]
        private double _minWallClearanceMm;
    }

    /// <summary>A project level that can be ticked for a multi-level analysis.</summary>
    public sealed partial class LevelOption : ObservableObject
    {
        public LevelOption(ElementId levelId, string name, double elevationMm)
        {
            LevelId = levelId;
            Name = name;
            ElevationMm = elevationMm;
        }

        public ElementId LevelId { get; }
        public string Name { get; }
        public double ElevationMm { get; }

        [ObservableProperty]
        private bool _isSelected;
    }
}
