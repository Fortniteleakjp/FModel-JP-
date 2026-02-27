using System.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Data;
using Microsoft.Win32;
using AdonisUI.Controls;
using System.Threading.Tasks;
using FortniteReplayReader;
using Unreal.Core.Models.Enums;
using Newtonsoft.Json;
using System.IO;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.Events;
using System.ComponentModel;
using System.Windows.Input;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.Views
{
    /// <summary>
    /// Interaction logic for ReplayAnalysisWindow.xaml
    /// </summary>
    public partial class ReplayAnalysisWindow : AdonisWindow
    {
        private FortniteReplay? _replay;
        private ICollectionView? _eliminationsView;

        public ReplayAnalysisWindow()
        {
            InitializeComponent();
        }

        private void OnSelectReplayFileClick(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Replay Files (*.replay)|*.replay|All files (*.*)|*.*",
                Title = "Select a Replay File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ReplayFilePathTextBox.Text = openFileDialog.FileName;
            }
        }

        private async void OnStartAnalysisClick(object sender, RoutedEventArgs e)
        {
            var replayPath = ReplayFilePathTextBox.Text;
            if (string.IsNullOrEmpty(replayPath) || !System.IO.File.Exists(replayPath))
            {
                MessageBox.Show("有効なリプレイファイルを選択してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StartAnalysisButton.IsEnabled = false;
            SaveJsonButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;
            var parseMode = GetSelectedParseMode();

            try
            {
                _replay = await Task.Run(() => new ReplayReader(null, parseMode).ReadReplay(replayPath));
                
                _eliminationsView = CollectionViewSource.GetDefaultView(_replay.Eliminations);
                _eliminationsView.Filter = FilterEliminations;
                AnalysisResultDataGrid.ItemsSource = _eliminationsView;
                SaveJsonButton.IsEnabled = true;

                // 統計情報の計算
                if (_replay.Eliminations != null)
                {
                    var killsPerPlayer = _replay.Eliminations
                        .Where(e => !string.IsNullOrEmpty(e.Eliminator))
                        .GroupBy(e => e.Eliminator)
                        .Select(g => new { Player = g.Key, Count = g.Count() })
                        .OrderByDescending(x => x.Count)
                        .FirstOrDefault();
                    MostKillsTextBlock.Text = killsPerPlayer != null ? $"{killsPerPlayer.Player} ({killsPerPlayer.Count} kills)" : "データなし";

                    var weaponStats = _replay.Eliminations
                        .GroupBy(e => e.GunType)
                        .Select(g => new KeyValuePair<string, int>(g.Key.ToString(), g.Count()))
                        .OrderByDescending(x => x.Value)
                        .ToList();
                    WeaponRankingDataGrid.ItemsSource = weaponStats;
                }

                // ヘッダー情報の表示
                if (_replay.Header != null)
                {
                    BranchTextBox.Text = _replay.Header.Branch;
                    LevelNamesListBox.ItemsSource = _replay.Header.LevelNamesAndTimes.Select(x => $"{x.Item1} ({x.Item2})");
                }

                MessageBox.Show($"解析が完了しました。\n{_replay.Eliminations.Count} 件の撃破データを取得しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"解析中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                _replay = null;
            }
            finally
            {
                StartAnalysisButton.IsEnabled = true;
                AnalysisProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void OnSaveJsonClick(object sender, RoutedEventArgs e)
        {
            if (_replay == null)
            {
                MessageBox.Show("先にリプレイを解析してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON File (*.json)|*.json",
                Title = "Save Replay as JSON",
                FileName = $"{Path.GetFileNameWithoutExtension(ReplayFilePathTextBox.Text)}.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(_replay, Formatting.Indented);
                    File.WriteAllText(saveFileDialog.FileName, json);
                    MessageBox.Show($"JSONファイルとして保存しました。\n{saveFileDialog.FileName}", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"JSONファイルの保存中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private bool FilterEliminations(object obj)
        {
            if (obj is PlayerElimination elimination)
            {
                var filterText = FilterTextBox.Text;
                if (string.IsNullOrWhiteSpace(filterText)) return true;

                bool matchEliminator = elimination.Eliminator?.Contains(filterText, System.StringComparison.OrdinalIgnoreCase) ?? false;
                bool matchEliminated = elimination.Eliminated?.Contains(filterText, System.StringComparison.OrdinalIgnoreCase) ?? false;

                return matchEliminator || matchEliminated;
            }
            return false;
        }

        private void OnFilterTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _eliminationsView?.Refresh();
        }

        private void OnDataGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AnalysisResultDataGrid.SelectedItem is PlayerElimination elimination)
            {
                new EliminationDetailWindow(elimination).Show();
            }
        }

        private ParseMode GetSelectedParseMode()
        {
            if (EventsOnlyRadioButton.IsChecked == true) return ParseMode.EventsOnly;
            if (MinimalRadioButton.IsChecked == true) return ParseMode.Minimal;
            if (NormalRadioButton.IsChecked == true) return ParseMode.Normal;
            if (FullRadioButton.IsChecked == true) return ParseMode.Full;
            return ParseMode.Minimal;
        }
    }
}