using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Revit.Placement
{
    public sealed class CommitResult
    {
        public int PlacedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Commits approved ProposedDevice items into Revit in a single atomic transaction
    /// using horizontal reference planes at ceiling underside elevation so linked-ceiling
    /// hosting works reliably across Revit 2023-2027.
    /// </summary>
    public sealed class RevitPlacementCommitter
    {
        private const double MmToFt = 1.0 / 304.8;
        private const double FtToMm = 304.8;

        public CommitResult CommitPlan(
            Document doc,
            View activeView,
            PlacementPlan plan,
            FamilySymbol detectorSymbol)
        {
            var result = new CommitResult();
            var approvedIds = new HashSet<string>(
                plan.Compartments.Where(c => c.IsApprovedForPlacement).Select(c => c.CompartmentId));

            var toPlace = plan.Devices
                .Where(d => d.Role == DeviceRole.SmokeDetector && approvedIds.Contains(d.CompartmentId))
                .ToList();

            if (toPlace.Count == 0)
                return result;

            bool isOneLevelBased = detectorSymbol.Family?.FamilyPlacementType == FamilyPlacementType.OneLevelBased;
            Level? activeLevel = activeView.GenLevel;
            double levelElevMm = activeLevel != null ? activeLevel.Elevation * FtToMm : 0.0;

            using (var tx = new Transaction(doc, $"Fire Detection Layout ({plan.RunId})"))
            {
                tx.Start();

                if (!detectorSymbol.IsActive)
                {
                    detectorSymbol.Activate();
                    doc.Regenerate();
                }

                var refPlanesByZ = new Dictionary<int, ReferencePlane>();

                foreach (var device in toPlace)
                {
                    int elevKeyMm = (int)Math.Round(device.ElevationMm);
                    FamilyInstance instance;

                    if (isOneLevelBased && activeLevel != null)
                    {
                        var pt = new XYZ(device.Location.X * MmToFt, device.Location.Y * MmToFt, 0.0);
                        instance = doc.Create.NewFamilyInstance(
                            pt,
                            detectorSymbol,
                            activeLevel,
                            StructuralType.NonStructural);

                        double offsetFt = (elevKeyMm - levelElevMm) * MmToFt;
                        Parameter? elevParam = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)
                            ?? instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                        if (elevParam != null && !elevParam.IsReadOnly)
                            elevParam.Set(offsetFt);
                    }
                    else
                    {
                        if (!refPlanesByZ.TryGetValue(elevKeyMm, out ReferencePlane? refPlane))
                        {
                            double zFt = elevKeyMm * MmToFt;
                            refPlane = doc.Create.NewReferencePlane(
                                new XYZ(1, 0, zFt),
                                new XYZ(0, 0, zFt),
                                new XYZ(0, 1, 0),
                                activeView);
                            refPlane.Name = $"FireLayout_Z_{elevKeyMm}mm";
                            refPlanesByZ[elevKeyMm] = refPlane;
                            doc.Regenerate();
                        }

                        var pt = new XYZ(device.Location.X * MmToFt, device.Location.Y * MmToFt, elevKeyMm * MmToFt);
                        instance = doc.Create.NewFamilyInstance(
                            refPlane.GetReference(),
                            pt,
                            XYZ.BasisX,
                            detectorSymbol);

                        try
                        {
                            if (instance.CanFlipWorkPlane)
                                instance.IsWorkPlaneFlipped = true;
                        }
                        catch
                        {
                            // Non-flippable family; keep default orientation.
                        }
                    }

                    Parameter? comments = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (comments != null && !comments.IsReadOnly)
                    {
                        comments.Set($"FIRE_LAYOUT_AUTO:{plan.RunId}:{device.CompartmentId}");
                    }

                    result.PlacedCount++;
                }

                tx.Commit();
            }

            return result;
        }
    }
}
