using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace FireDetectionLayout.Revit.Commands
{
    /// <summary>
    /// Runs queued work inside a valid Revit API context. Requests raised from the dockable pane
    /// or the modeless preview window are executed in order, none are dropped.
    /// </summary>
    public sealed class FireLayoutEventHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _pending = new ConcurrentQueue<Action<UIApplication>>();

        public void Raise(Action<UIApplication> action)
        {
            _pending.Enqueue(action);
            Application.RevitEvent.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_pending.TryDequeue(out var action))
            {
                try
                {
                    action(app);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // The user pressed Esc during a pick; nothing to report.
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Fire Detection Layout - Error", ex.Message);
                }
            }
        }

        public string GetName() => "FireDetectionLayout.ExternalEvent";
    }
}
