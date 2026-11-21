using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using FireDetectionLayout.Revit.Commands;
using FireDetectionLayout.Revit.UI;
using JetBrains.Annotations;

namespace FireDetectionLayout.Revit
{
    [UsedImplicitly]
    public class Application : IExternalApplication
    {
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("C4B7E912-6A3F-4E88-B204-91D5A73E6C10"));

        public static FireLayoutPane? PaneInstance { get; private set; }
        public static FireLayoutEventHandler EventHandler { get; private set; } = null!;
        public static Autodesk.Revit.UI.ExternalEvent RevitEvent { get; private set; } = null!;

        public Result OnStartup(UIControlledApplication application)
        {
            EventHandler = new FireLayoutEventHandler();
            RevitEvent = Autodesk.Revit.UI.ExternalEvent.Create(EventHandler);

            CreateRibbon(application);
            RegisterDockablePane(application);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            RevitEvent?.Dispose();
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            const string tabName = "Fire Detection";
            const string panelName = "Fire Layout";

            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists
            }

            var panel = application
                .GetRibbonPanels(tabName)
                .FirstOrDefault(p => p.Name == panelName)
                ?? application.CreateRibbonPanel(tabName, panelName);

            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var buttonData = new PushButtonData(
                nameof(OpenFireLayoutCommand),
                "Fire\nLayout",
                assemblyPath,
                typeof(OpenFireLayoutCommand).FullName);

            panel.AddItem(buttonData);
        }

        private static void RegisterDockablePane(UIControlledApplication application)
        {
            PaneInstance = new FireLayoutPane();
            application.RegisterDockablePane(PaneId, "Fire Detection Layout (AS 1670)", PaneInstance);
        }
    }
}
