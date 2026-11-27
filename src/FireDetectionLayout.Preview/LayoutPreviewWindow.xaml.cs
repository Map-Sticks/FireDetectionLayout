using System.Windows;
using System.Windows.Input;

namespace FireDetectionLayout.Preview
{
    /// <summary>Modeless review window: the layout drawing beside the per-space table.</summary>
    public partial class LayoutPreviewWindow : Window
    {
        public LayoutPreviewWindow(LayoutWorkspace workspace)
        {
            InitializeComponent();
            DataContext = workspace;
        }

        public LayoutWorkspace Workspace => (LayoutWorkspace)DataContext;

        /// <summary>The drawing surface, exposed so a host can render or drive it.</summary>
        public LayoutCanvas Drawing => LayoutDrawing;

        private void SpaceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
            Workspace.ZoomToSelectedCommand.Execute(null);
    }
}
