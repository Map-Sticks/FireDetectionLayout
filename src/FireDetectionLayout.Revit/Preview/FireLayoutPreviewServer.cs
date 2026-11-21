using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Revit.Preview
{
    public sealed class FireLayoutPreviewServer : IDirectContext3DServer
    {
        private static readonly Guid ServerId = new Guid("E8D02E36-02F4-4BFC-A3AC-FD11F8A8F30D");
        private const double MmToFt = 1.0 / 304.8;
        private const double MarkerHalfSizeMm = 150.0;
        private const int CoverageCircleSegments = 48;

        private volatile PreviewLine[] _lines = Array.Empty<PreviewLine>();
        private volatile ElementId? _viewId;
        private volatile bool _enabled;

        public void SetPreview(PlacementPlan? plan, double maxWallDistanceMm, ElementId viewId, bool enabled)
        {
            _viewId = viewId;
            _enabled = enabled && plan != null;
            _lines = plan == null ? Array.Empty<PreviewLine>() : BuildLines(plan, maxWallDistanceMm);
        }

        public Guid GetServerId() => ServerId;
        public string GetVendorId() => "MAPS";
        public string GetName() => "Fire Detection Layout Preview";
        public string GetDescription() => "Non-destructive compartment and detector coverage preview.";
        public string GetApplicationId() => "FireDetectionLayout";
        public string GetSourceId() => "FireDetectionLayout";
        public ExternalServiceId GetServiceId() =>
            ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public bool UsesHandles() => false;
        public bool UseInTransparentPass(View view) => false;

        public bool CanExecute(View view) =>
            _enabled && _lines.Length > 0 && _viewId != null && view != null && view.Id.Equals(_viewId);

        public Outline GetBoundingBox(View view)
        {
            if (_lines.Length == 0)
                return new Outline(XYZ.Zero, XYZ.Zero);

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            foreach (var line in _lines)
            {
                minX = Math.Min(minX, Math.Min(line.X1, line.X2));
                minY = Math.Min(minY, Math.Min(line.Y1, line.Y2));
                minZ = Math.Min(minZ, Math.Min(line.Z1, line.Z2));
                maxX = Math.Max(maxX, Math.Max(line.X1, line.X2));
                maxY = Math.Max(maxY, Math.Max(line.Y1, line.Y2));
                maxZ = Math.Max(maxZ, Math.Max(line.Z1, line.Z2));
            }

            const double marginFt = 1.0;
            return new Outline(
                new XYZ(minX - marginFt, minY - marginFt, minZ - marginFt),
                new XYZ(maxX + marginFt, maxY + marginFt, maxZ + marginFt));
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            var lines = _lines;
            if (!_enabled || lines.Length == 0)
                return;

            foreach (var group in lines.GroupBy(line => line.ColorKey))
            {
                if (DrawContext.IsInterrupted())
                    return;
                DrawLines(group.ToList());
            }
        }

        private static PreviewLine[] BuildLines(PlacementPlan plan, double maxWallDistanceMm)
        {
            var lines = new List<PreviewLine>();
            foreach (var compartment in plan.Compartments)
            {
                if (compartment.Status == CompartmentStatus.Skipped)
                    continue;

                int color = compartment.Status switch
                {
                    CompartmentStatus.Green => ColorKey(35, 170, 80),
                    CompartmentStatus.Amber => ColorKey(240, 165, 30),
                    _ => ColorKey(220, 55, 55)
                };
                AddRing(lines, compartment.Boundary.OuterRing, compartment.CeilingElevationMm, color);
                foreach (var hole in compartment.Boundary.Holes)
                    AddRing(lines, hole, compartment.CeilingElevationMm, color);
            }

            double unresolvedEdgeElevation = plan.Compartments
                .Where(c => c.Status != CompartmentStatus.Skipped)
                .Select(c => c.CeilingElevationMm)
                .DefaultIfEmpty(0.0)
                .Average();
            foreach (var edge in plan.UnresolvedOpenEdges)
                AddLine(lines, edge.Start, edge.End, unresolvedEdgeElevation, ColorKey(255, 0, 180));

            var approvedIds = new HashSet<string>(
                plan.Compartments.Where(c => c.IsApprovedForPlacement).Select(c => c.CompartmentId));
            foreach (var device in plan.Devices.Where(d =>
                         d.Role == DeviceRole.SmokeDetector && approvedIds.Contains(d.CompartmentId)))
            {
                int markerColor = ColorKey(20, 190, 220);
                var location = device.Location;
                AddLine(lines,
                    new Point2D(location.X - MarkerHalfSizeMm, location.Y),
                    new Point2D(location.X + MarkerHalfSizeMm, location.Y),
                    device.ElevationMm, markerColor);
                AddLine(lines,
                    new Point2D(location.X, location.Y - MarkerHalfSizeMm),
                    new Point2D(location.X, location.Y + MarkerHalfSizeMm),
                    device.ElevationMm, markerColor);

                Point2D previous = new Point2D(
                    location.X + maxWallDistanceMm,
                    location.Y);
                for (int segment = 1; segment <= CoverageCircleSegments; segment++)
                {
                    double angle = 2.0 * Math.PI * segment / CoverageCircleSegments;
                    var next = new Point2D(
                        location.X + maxWallDistanceMm * Math.Cos(angle),
                        location.Y + maxWallDistanceMm * Math.Sin(angle));
                    AddLine(lines, previous, next, device.ElevationMm, ColorKey(40, 145, 210));
                    previous = next;
                }
            }

            return lines.ToArray();
        }

        private static void AddRing(List<PreviewLine> lines, IReadOnlyList<Point2D> ring, double elevationMm, int color)
        {
            if (ring.Count < 2)
                return;

            for (int i = 0; i < ring.Count; i++)
                AddLine(lines, ring[i], ring[(i + 1) % ring.Count], elevationMm, color);
        }

        private static void AddLine(List<PreviewLine> lines, Point2D start, Point2D end, double elevationMm, int color)
        {
            lines.Add(new PreviewLine(
                start.X * MmToFt, start.Y * MmToFt, elevationMm * MmToFt,
                end.X * MmToFt, end.Y * MmToFt, elevationMm * MmToFt,
                color));
        }

        private static void DrawLines(IReadOnlyList<PreviewLine> lines)
        {
            if (lines.Count == 0)
                return;

            int vertexCount = lines.Count * 2;
            int indexCount = vertexCount;
            var vertexBuffer = new VertexBuffer(vertexCount * 3);
            vertexBuffer.Map(vertexCount * 3);
            var vertexStream = vertexBuffer.GetVertexStreamPosition();
            var indexBuffer = new IndexBuffer(indexCount);
            indexBuffer.Map(indexCount);
            var indexStream = indexBuffer.GetIndexStreamLine();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                vertexStream.AddVertex(new VertexPosition(new XYZ(line.X1, line.Y1, line.Z1)));
                vertexStream.AddVertex(new VertexPosition(new XYZ(line.X2, line.Y2, line.Z2)));
                indexStream.AddLine(new IndexLine(i * 2, i * 2 + 1));
            }

            vertexBuffer.Unmap();
            indexBuffer.Unmap();
            var format = new VertexFormat(VertexFormatBits.Position);
            var effect = new EffectInstance(VertexFormatBits.Position);
            effect.SetColor(ColorFromKey(lines[0].ColorKey));
            DrawContext.FlushBuffer(
                vertexBuffer,
                vertexCount,
                indexBuffer,
                indexCount,
                format,
                effect,
                PrimitiveType.LineList,
                0,
                lines.Count);
        }

        private static int ColorKey(byte red, byte green, byte blue) =>
            red << 16 | green << 8 | blue;

        private static Color ColorFromKey(int key) =>
            new Color((byte)(key >> 16), (byte)(key >> 8), (byte)key);

        private sealed class PreviewLine
        {
            public double X1 { get; }
            public double Y1 { get; }
            public double Z1 { get; }
            public double X2 { get; }
            public double Y2 { get; }
            public double Z2 { get; }
            public int ColorKey { get; }

            public PreviewLine(double x1, double y1, double z1, double x2, double y2, double z2, int colorKey)
            {
                X1 = x1;
                Y1 = y1;
                Z1 = z1;
                X2 = x2;
                Y2 = y2;
                Z2 = z2;
                ColorKey = colorKey;
            }
        }
    }
}
