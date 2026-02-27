using AdonisUI.Controls;
using FortniteReplayReader.Models.Events;
using Newtonsoft.Json;

namespace FModel.Views
{
    /// <summary>
    /// Interaction logic for EliminationDetailWindow.xaml
    /// </summary>
    public partial class EliminationDetailWindow : AdonisWindow
    {
        public EliminationDetailWindow(PlayerElimination elimination)
        {
            InitializeComponent();
            DetailsTextBox.Text = JsonConvert.SerializeObject(elimination, Formatting.Indented);
        }
    }
}
