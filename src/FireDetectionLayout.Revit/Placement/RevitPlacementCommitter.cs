using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using FireDetectionLayout.Core.Geometry;
using FireDetectionLayout.Core.Models;
using FireDetectionLayout.Revit.Extraction;
using NetTopologySuite.Geometries.Prepared;

namespace FireDetectionLayout.Revit.Placement
{
    public sealed class CommitResult
    {
        public int PlacedDetectors { get; set; }
        public int PlacedSpeakers { get; set; }
        public int RemovedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        public int PlacedCount => PlacedDetectors + PlacedSpeakers;
    }

    /// <summary>Family type to place for each device kind. A kind without a type is not placed.</summary>
    public sealed class DeviceFamilyMap
    {
        public FamilySymbol? SmokeDetector { get; set; }
        public FamilySymbol? HeatDetector { get; set; }
        public FamilySymbol? Speaker { get; set; }

        public FamilySymbol? Get(DeviceRole role)
        {
            switch (role)
            {
                case DeviceRole.HeatDetector: return HeatDetector;
                case DeviceRole.EwisSpeaker: return Speaker;
                default: return SmokeDetector;
            }
        }
    }

    /// <summary>
    /// Commits reviewed plans into Revit in a single atomic transaction. Devices are placed on
    /// their Level with an elevation offset matching the solved ceiling / soffit height; a family
    /// that rejects level hosting falls back to a named horizontal reference plane.
    /// </summary>
    public sealed class RevitPlacementCommitter
    {
        public const string OwnershipTag = "FIRE_LAYOUT_AUTO";

        private const double MmToFt = 1.0 / 304.8;
        private const double FtToMm = 304.8;

        public CommitResult CommitPlans(
            Document doc,
            View activeView,
            IReadOnlyList<PlacementPlan> plans,
            DeviceFamilyMap families,
            bool replaceToolOwned)
        {
            var result = new CommitResult();
            var jobs = new List<(PlacementPlan Plan, Level Level, List<ProposedDevice> Devices)>();
            foreach (var plan in plans)
            {
                var level = doc.GetElement(RevitIds.FromKey(plan.LevelElementId)) as Level ?? activeView.GenLevel;
                if (level == null)
                {
                    result.Warnings.Add($"Level '{plan.LevelName}' was not found in the active document; its devices were not placed.");
                    continue;
                }

                var approved = new HashSet<string>(
                    plan.Compartments.Where(c => c.IsApprovedForPlacement).Select(c => c.CompartmentId));
                var devices = plan.Devices
                    .Where(d => !d.IsExcluded && approved.Contains(d.CompartmentId))
                    .ToList();
                jobs.Add((plan, level, devices));
            }

            if (jobs.All(job => job.Devices.Count == 0))
                return result;

            using (var tx = new Transaction(doc, "Fire Detection Layout"))
            {
                tx.Start();

                foreach (var symbol in new[] { families.SmokeDetector, families.HeatDetector, families.Speaker })
                {
                    if (symbol != null && !symbol.IsActive)
                        symbol.Activate();
                }

                doc.Regenerate();

                Dictionary<string, ReferencePlane>? referencePlanes = null;
                foreach (var (plan, level, devices) in jobs)
                {
                    if (replaceToolOwned)
                        result.RemovedCount += RemoveToolOwnedDevices(doc, plan, level, families);

                    var missingKinds = new HashSet<DeviceRole>();
                    var planesByElevation = new Dictionary<int, ReferencePlane>();
                    foreach (var device in devices)
                    {
                        var symbol = families.Get(device.Role);
                        if (symbol == null)
                        {
                            result.SkippedCount++;
                            if (missingKinds.Add(device.Role))
                                result.Warnings.Add($"No family type is mapped for {device.Role.ToDisplayName().ToLowerInvariant()}s; they were not placed.");
                            continue;
                        }

                        FamilyInstance? instance;
                        try
                        {
                            instance = PlaceDevice(doc, activeView, level, device, symbol, plan.RunId, planesByElevation, ref referencePlanes);
                        }
                        catch (Exception error) when (error is Autodesk.Revit.Exceptions.ApplicationException ||
                                                      error is InvalidOperationException)
                        {
                            instance = null;
                            result.Warnings.Add($"'{symbol.FamilyName} : {symbol.Name}' could not be placed: {error.Message}");
                        }

                        if (instance == null)
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        RotateToRoomGrid(doc, instance, device);
                        Parameter? comments = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (comments != null && !comments.IsReadOnly)
                            comments.Set($"{OwnershipTag}:{plan.RunId}:{device.CompartmentId}:{device.Role}");

                        if (device.Role == DeviceRole.EwisSpeaker)
                            result.PlacedSpeakers++;
                        else
                            result.PlacedDetectors++;
                    }
                }

                tx.Commit();
            }

            return result;
        }

        private static FamilyInstance? PlaceDevice(
            Document doc,
            View activeView,
            Level level,
            ProposedDevice device,
            FamilySymbol symbol,
            string runId,
            Dictionary<int, ReferencePlane> planesByElevation,
            ref Dictionary<string, ReferencePlane>? referencePlanes)
        {
            double levelElevMm = level.Elevation * FtToMm;
            int elevationMm = (int)Math.Round(device.ElevationMm);
            double offsetFt = (elevationMm - levelElevMm) * MmToFt;
            double x = device.Location.X * MmToFt;
            double y = device.Location.Y * MmToFt;
            var placementType = symbol.Family?.FamilyPlacementType ?? FamilyPlacementType.Invalid;

            if (placementType == FamilyPlacementType.OneLevelBased)
            {
                var instance = doc.Create.NewFamilyInstance(
                    new XYZ(x, y, 0.0), symbol, level, StructuralType.NonStructural);
                ApplyLevelElevationOffset(doc, instance, level.Elevation, offsetFt);
                return instance;
            }

            if (placementType == FamilyPlacementType.OneLevelBasedHosted)
            {
                // Ceiling-hosted families need a ceiling in this document; linked ceilings cannot host them.
                if (doc.GetElement(RevitIds.FromKey(device.HostCeilingElementId)) is Ceiling host)
                {
                    return doc.Create.NewFamilyInstance(
                        new XYZ(x, y, elevationMm * MmToFt), symbol, host, level, StructuralType.NonStructural);
                }

                throw new InvalidOperationException(
                    "this family is ceiling-hosted and the ceiling here is in a linked model or missing. Use a level-based or face-based (work plane) family");
            }

            Reference? levelPlane = level.GetPlaneReference();
            if (levelPlane != null)
            {
                try
                {
                    var instance = doc.Create.NewFamilyInstance(
                        levelPlane, new XYZ(x, y, level.Elevation), XYZ.BasisX, symbol);
                    TryFlipWorkPlane(instance);
                    ApplyLevelElevationOffset(doc, instance, level.Elevation, offsetFt);
                    return instance;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Family rejects the level plane; fall through to a dedicated reference plane.
                }
            }

            referencePlanes ??= CollectExistingRefPlanes(doc);
            if (!planesByElevation.TryGetValue(elevationMm, out ReferencePlane? plane))
            {
                plane = GetOrCreateHorizontalRefPlane(
                    doc, activeView, referencePlanes, elevationMm, elevationMm * MmToFt, runId);
                planesByElevation[elevationMm] = plane;
            }

            var hosted = doc.Create.NewFamilyInstance(
                plane.GetReference(), new XYZ(x, y, elevationMm * MmToFt), XYZ.BasisX, symbol);
            TryFlipWorkPlane(hosted);
            return hosted;
        }

        /// <summary>
        /// Deletes devices this tool placed earlier inside the spaces now being committed, so a
        /// re-run replaces its own work and never touches manually placed devices.
        /// </summary>
        private static int RemoveToolOwnedDevices(Document doc, PlacementPlan plan, Level level, DeviceFamilyMap families)
        {
            var spaces = plan.Compartments
                .Where(c => c.IsApprovedForPlacement)
                .Select(c => WallGraphPolygonizer.TryToValidNtsPolygon(c.Boundary))
                .Where(polygon => polygon != null)
                .Select(polygon => PreparedGeometryFactory.Prepare(polygon!))
                .ToList();
            if (spaces.Count == 0)
                return 0;

            var categories = new HashSet<ElementId> { new ElementId(BuiltInCategory.OST_FireAlarmDevices) };
            foreach (var symbol in new[] { families.SmokeDetector, families.HeatDetector, families.Speaker })
            {
                if (symbol?.Category != null)
                    categories.Add(symbol.Category.Id);
            }

            double levelElevFt = level.Elevation;
            double nextLevelFt = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Select(l => l.Elevation)
                .Where(z => z > levelElevFt + 1500.0 * MmToFt)
                .DefaultIfEmpty(levelElevFt + 10000.0 * MmToFt)
                .Min();

            var factory = new NetTopologySuite.Geometries.GeometryFactory();
            var toDelete = new List<ElementId>();
            foreach (var categoryId in categories)
            {
                var instances = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryId)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>();
                foreach (var instance in instances)
                {
                    string comments = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    if (comments.IndexOf(OwnershipTag, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!(instance.Location is LocationPoint location))
                        continue;

                    XYZ point = location.Point;
                    if (point.Z < levelElevFt - 0.01 || point.Z >= nextLevelFt)
                        continue;

                    var modelPoint = factory.CreatePoint(
                        new NetTopologySuite.Geometries.Coordinate(point.X * FtToMm, point.Y * FtToMm));
                    if (spaces.Any(space => space.Covers(modelPoint)))
                        toDelete.Add(instance.Id);
                }
            }

            if (toDelete.Count > 0)
                doc.Delete(toDelete);
            return toDelete.Count;
        }

        /// <summary>Turns the symbol to follow a rotated room so device text and pairs read square to the walls.</summary>
        private static void RotateToRoomGrid(Document doc, FamilyInstance instance, ProposedDevice device)
        {
            if (Math.Abs(device.RotationDegrees) < 0.01 || !(instance.Location is LocationPoint location))
                return;

            try
            {
                XYZ point = location.Point;
                ElementTransformUtils.RotateElement(
                    doc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ), device.RotationDegrees * Math.PI / 180.0);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Some hosted families cannot be rotated; the position is still correct.
            }
        }

        private static void TryFlipWorkPlane(FamilyInstance instance)
        {
            try
            {
                if (instance.CanFlipWorkPlane)
                    instance.IsWorkPlaneFlipped = true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Non-flippable family; keep default orientation.
            }
        }

        private static void ApplyLevelElevationOffset(
            Document doc,
            FamilyInstance instance,
            double levelElevationFt,
            double targetOffsetFt)
        {
            Parameter? elevParam = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (elevParam != null && !elevParam.IsReadOnly)
            {
                elevParam.Set(targetOffsetFt);
                return;
            }

            Parameter? hostOffsetParam = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (hostOffsetParam == null || hostOffsetParam.IsReadOnly)
                return;

            double signedOffset = instance.IsWorkPlaneFlipped ? -targetOffsetFt : targetOffsetFt;
            hostOffsetParam.Set(signedOffset);
            doc.Regenerate();

            // Verify actual Z elevation; if flipping inverted the offset direction, correct it.
            if (instance.Location is LocationPoint lp &&
                Math.Abs(lp.Point.Z - (levelElevationFt + targetOffsetFt)) > 0.05 &&
                Math.Abs(lp.Point.Z - (levelElevationFt - targetOffsetFt)) <= 0.05)
            {
                hostOffsetParam.Set(-signedOffset);
            }
        }

        private static Dictionary<string, ReferencePlane> CollectExistingRefPlanes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>()
                .Where(rp => !string.IsNullOrEmpty(rp.Name))
                .GroupBy(rp => rp.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static ReferencePlane GetOrCreateHorizontalRefPlane(
            Document doc,
            View activeView,
            Dictionary<string, ReferencePlane> existingRefPlanes,
            int elevKeyMm,
            double zFt,
            string runId)
        {
            string baseName = $"FireLayout_Z_{elevKeyMm}mm";
            if (existingRefPlanes.TryGetValue(baseName, out ReferencePlane? existing))
            {
                try
                {
                    Plane plane = existing.GetPlane();
                    if (Math.Abs(plane.Normal.Z) > 0.99 && Math.Abs(plane.Origin.Z - zFt) <= 0.005)
                        return existing;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Fall through and create a new uniquely named plane.
                }
            }

            ReferencePlane created = doc.Create.NewReferencePlane(
                new XYZ(1, 0, zFt),
                new XYZ(0, 0, zFt),
                new XYZ(0, 1, 0),
                activeView);

            string uniqueName = baseName;
            int suffix = 1;
            while (existingRefPlanes.ContainsKey(uniqueName))
            {
                uniqueName = suffix == 1 ? $"{baseName}_{runId}" : $"{baseName}_{runId}_{suffix}";
                suffix++;
            }

            created.Name = uniqueName;
            existingRefPlanes[uniqueName] = created;
            doc.Regenerate();
            return created;
        }
    }
}
