using System;
using Autodesk.Revit.UI;

namespace FireDetectionLayout.Revit.Commands
{
    public sealed class FireLayoutEventHandler : IExternalEventHandler
    {
        private Action<UIApplication>? _pendingAction;
        private readonly object _sync = new object();

        public bool IsExecuting { get; private set; }

        public void Raise(Action<UIApplication> action)
        {
            lock (_sync)
            {
                _pendingAction = action;
            }
            Application.RevitEvent.Raise();
        }

        public void Execute(UIApplication app)
        {
            Action<UIApplication>? action;
            lock (_sync)
            {
                action = _pendingAction;
                _pendingAction = null;
            }

            if (action == null)
                return;

            IsExecuting = true;
            try
            {
                action(app);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fire Detection Layout - Error", ex.Message);
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public string GetName() => "FireDetectionLayout.ExternalEvent";
    }
}
