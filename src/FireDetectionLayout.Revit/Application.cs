using System;
using Autodesk.Revit.UI;
using FireDetectionLayout.Revit.Commands;
using FireDetectionLayout.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;

namespace FireDetectionLayout.Revit
{
    [UsedImplicitly]
    public class Application : ExternalApplication
    {
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("C4B7E912-6A3F-4E88-B204-91D5A73E6C10"));

        public static FireLayoutPane? PaneInstance { get; private set; }
        public static FireLayoutEventHandler EventHandler { get; private set; } = null!;
        public static Autodesk.Revit.UI.ExternalEvent RevitEvent { get; private set; } = null!;

        public override void OnStartup()
        {
            EventHandler = new FireLayoutEventHandler();
            RevitEvent = Autodesk.Revit.UI.ExternalEvent.Create(EventHandler);

            CreateRibbon();
            RegisterDockablePane();
        }

        public override void OnShutdown()
        {
            RevitEvent?.Dispose();
        }

        private void CreateRibbon()
        {
            var panel = Application.CreatePanel("Fire Layout", "Fire Detection");
            panel.AddPushButton<OpenFireLayoutCommand>("Fire\nLayout");
        }

        private void RegisterDockablePane()
        {
            PaneInstance = new FireLayoutPane();
            Application.RegisterDockablePane(PaneId, "Fire Detection Layout (AS 1670)", PaneInstance);
        }
    }
}
