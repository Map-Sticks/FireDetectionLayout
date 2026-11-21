using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;

namespace FireDetectionLayout.Revit.Commands
{
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class OpenFireLayoutCommand : ExternalCommand
    {
        public override void Execute()
        {
            if (Application.ActiveUIDocument == null)
            {
                TaskDialog.Show("Fire Detection Layout", "Open a Revit project first.");
                return;
            }

            if (FireDetectionLayout.Revit.Application.PaneInstance == null)
                throw new InvalidOperationException("Fire Detection Layout dockable pane was not registered.");

            Application.GetDockablePane(FireDetectionLayout.Revit.Application.PaneId).Show();
        }
    }
}
