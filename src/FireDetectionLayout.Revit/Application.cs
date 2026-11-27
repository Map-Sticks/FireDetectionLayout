using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using FireDetectionLayout.Revit.Commands;
using FireDetectionLayout.Revit.Preview;
using FireDetectionLayout.Revit.UI;
using Autodesk.Revit.DB.ExternalService;
using JetBrains.Annotations;

namespace FireDetectionLayout.Revit
{
    [UsedImplicitly]
    public class Application : IExternalApplication
    {
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("C4B7E912-6A3F-4E88-B204-91D5A73E6C10"));

        public static FireLayoutPane? PaneInstance { get; private set; }
        public static FireLayoutPreviewServer? PreviewServer { get; private set; }
        public static FireLayoutEventHandler EventHandler { get; private set; } = null!;
        public static Autodesk.Revit.UI.ExternalEvent RevitEvent { get; private set; } = null!;

        /// <summary>Revit's main window, which owns the modeless preview window.</summary>
        public static IntPtr MainWindowHandle { get; private set; }

        private static MultiServerService? _previewService;

        public Result OnStartup(UIControlledApplication application)
        {
            MainWindowHandle = application.MainWindowHandle;
            RegisterPreviewServer();
            EventHandler = new FireLayoutEventHandler();
            RevitEvent = Autodesk.Revit.UI.ExternalEvent.Create(EventHandler);

            CreateRibbon(application);
            RegisterDockablePane(application);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            UnregisterPreviewServer();
            RevitEvent?.Dispose();
            return Result.Succeeded;
        }

        private static void RegisterPreviewServer()
        {
            var service = ExternalServiceRegistry.GetService(
                ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
            if (service == null)
                throw new InvalidOperationException("Revit DirectContext3D multi-server service is unavailable.");

            var server = new FireLayoutPreviewServer();
            service.AddServer(server);
            var activeServers = service.GetActiveServerIds().ToList();
            if (!activeServers.Contains(server.GetServerId()))
            {
                activeServers.Add(server.GetServerId());
                service.SetActiveServers(activeServers);
            }

            _previewService = service;
            PreviewServer = server;
        }

        private static void UnregisterPreviewServer()
        {
            if (_previewService == null || PreviewServer == null)
                return;

            var serverId = PreviewServer.GetServerId();
            var activeServers = _previewService.GetActiveServerIds()
                .Where(id => id != serverId)
                .ToList();
            _previewService.SetActiveServers(activeServers);
            _previewService.RemoveServer(serverId);
            PreviewServer = null;
            _previewService = null;
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
            panel.AddItem(new PushButtonData(
                nameof(OpenFireLayoutCommand),
                "Fire\nLayout",
                assemblyPath,
                typeof(OpenFireLayoutCommand).FullName)
            {
                ToolTip = "Open the Fire Detection Layout pane: choose the scope, devices and spacing rules, then analyse."
            });
            panel.AddItem(new PushButtonData(
                nameof(OpenLayoutPreviewCommand),
                "Layout\nPreview",
                assemblyPath,
                typeof(OpenLayoutPreviewCommand).FullName)
            {
                ToolTip = "Open the layout preview window to review, adjust and commit the proposed devices."
            });
        }

        private static void RegisterDockablePane(UIControlledApplication application)
        {
            PaneInstance = new FireLayoutPane();
            application.RegisterDockablePane(PaneId, "Fire Detection Layout", PaneInstance);
        }
    }
}
