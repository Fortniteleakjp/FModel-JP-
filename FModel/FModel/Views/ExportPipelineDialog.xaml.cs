using System.Windows;

namespace FModel.Views
{
    /// <summary>
    /// このバージョンの初回起動時にのみ表示し、エクスポート方式（旧/新パイプライン）を選択させるダイアログ。
    /// </summary>
    public partial class ExportPipelineDialog : AdonisUI.Controls.AdonisWindow
    {
        /// <summary>選択されたエクスポート方式（既定は安全側の旧パイプライン）。</summary>
        public EExportPipeline SelectedPipeline { get; private set; } = EExportPipeline.Legacy;

        public ExportPipelineDialog()
        {
            InitializeComponent();
        }

        private void OnUseLegacy(object sender, RoutedEventArgs e)
        {
            SelectedPipeline = EExportPipeline.Legacy;
            DialogResult = true;
            Close();
        }

        private void OnUseNew(object sender, RoutedEventArgs e)
        {
            SelectedPipeline = EExportPipeline.New;
            DialogResult = true;
            Close();
        }
    }
}
