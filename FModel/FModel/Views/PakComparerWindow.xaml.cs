using System.Collections.Generic;
using AdonisUI.Controls;
using FModel.ViewModels.CUE4Parse;

namespace FModel.Views
{
    public partial class PakComparerWindow : AdonisWindow
    {
        public IEnumerable<CUE4ParseViewModel.PakDiff> Diffs { get; }

        public PakComparerWindow(IEnumerable<CUE4ParseViewModel.PakDiff> diffs)
        {
            Diffs = diffs;
            InitializeComponent();
            DataContext = this;
        }
    }
}