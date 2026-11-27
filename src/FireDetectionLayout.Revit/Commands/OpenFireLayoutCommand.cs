using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JetBrains.Annotations;

namespace FireDetectionLayout.Revit.Commands
{
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class OpenFireLayoutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            if (uiApp.ActiveUIDocument == null)
            {
                TaskDialog.Show("Fire Detection Layout", "Open a Revit project first.");
                return Result.Cancelled;
            }

            var pane = FireDetectionLayout.Revit.Application.PaneInstance
                       ?? throw new InvalidOperationException("Fire Detection Layout dockable pane was not registered.");

            pane.ViewModel.LoadDocument(uiApp.ActiveUIDocument.Document);
            uiApp.GetDockablePane(FireDetectionLayout.Revit.Application.PaneId).Show();
            return Result.Succeeded;
        }
    }

    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class OpenLayoutPreviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            if (uiApp.ActiveUIDocument == null)
            {
                TaskDialog.Show("Fire Detection Layout", "Open a Revit project first.");
                return Result.Cancelled;
            }

            var pane = FireDetectionLayout.Revit.Application.PaneInstance
                       ?? throw new InvalidOperationException("Fire Detection Layout dockable pane was not registered.");

            pane.ViewModel.LoadDocument(uiApp.ActiveUIDocument.Document);
            pane.ViewModel.OpenPreviewCommand.Execute(null);
            return Result.Succeeded;
        }
    }
}
