using System.Windows.Controls;
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
    }
}
