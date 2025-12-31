using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace FModel.Views.ReleaseNotes
{
    public partial class ReleaseNotesView : UserControl
    {
        public ReleaseNotesView()
        {
            InitializeComponent();
        }

        public ReleaseNotesView(string version) : this()
        {
            VersionRun.Text = $"v{version}";
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}