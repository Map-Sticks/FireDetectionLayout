using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FireDetectionLayout.Core.Models;

namespace FireDetectionLayout.Revit.Extraction
{
    /// <summary>
    /// Captures the visible region of the active plan view as an image located in model
    /// coordinates, so the walls and ceiling patterns Revit shows can sit under the layout
    /// preview and travel with an exported assessment package.
    /// </summary>
    public static class ViewSnapshotExporter
    {
        private const double FtToMm = 304.8;
        private const int ImageWidthPixels = 3000;

        public static ViewImageReference? Capture(UIDocument uidoc, string folder, string fileStem)
        {
            View view = uidoc.ActiveView;
            if (!(view is ViewPlan))
                return null;

            // A view rotated away from model X/Y cannot be mapped with a simple rectangle.
            if (Math.Abs(view.RightDirection.X) < 0.9999 || Math.Abs(view.UpDirection.Y) < 0.9999)
                return null;

            UIView? uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            if (uiView == null)
                return null;

            IList<XYZ> corners = uiView.GetZoomCorners();
            if (corners.Count < 2)
                return null;

            Directory.CreateDirectory(folder);
            string basePath = Path.Combine(folder, fileStem);
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.VisibleRegionOfCurrentView,
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = ImageWidthPixels
            };

            try
            {
                uidoc.Document.ExportImage(options);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }

            // Revit may decorate the file name with the view name; take the newest matching image.
            string? imagePath = File.Exists(basePath + ".png")
                ? basePath + ".png"
                : Directory.GetFiles(folder, fileStem + "*.png")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            if (imagePath == null)
                return null;

            return new ViewImageReference
            {
                FilePath = imagePath,
                MinXMm = Math.Min(corners[0].X, corners[1].X) * FtToMm,
                MinYMm = Math.Min(corners[0].Y, corners[1].Y) * FtToMm,
                MaxXMm = Math.Max(corners[0].X, corners[1].X) * FtToMm,
                MaxYMm = Math.Max(corners[0].Y, corners[1].Y) * FtToMm
            };
        }
    }
}
