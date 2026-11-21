using System;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using FireDetectionLayout.Revit.ViewModels;

namespace FireDetectionLayout.Revit.UI
{
    public partial class FireLayoutPane : Page, IDockablePaneProvider
    {
        public FireLayoutViewModel ViewModel { get; }

        public FireLayoutPane()
        {
            InitializeComponent();
            ViewModel = new FireLayoutViewModel();
            DataContext = ViewModel;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }

        private void CompartmentGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(
                new Action(ViewModel.QueuePreviewRefresh),
                DispatcherPriority.Background);
        }
    }
}
