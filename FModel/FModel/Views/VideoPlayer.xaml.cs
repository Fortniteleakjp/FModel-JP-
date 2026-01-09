using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FModel.Views
{
    public partial class VideoPlayer : UserControl
    {
        private bool _isDragging = false;
        private bool _isTimerUpdating = false;
        private bool _isPlaying = false;
        private DispatcherTimer _timer;

        public VideoPlayer(string filePath)
        {
            InitializeComponent();
            MediaPlayer.Source = new Uri(filePath);
            MediaPlayer.Volume = VolumeSlider.Value;
            MediaPlayer.Play();
            _isPlaying = true;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += Timer_Tick;
            _timer.Start();
            this.Unloaded += OnUnloaded;
            this.Loaded += (s, e) => this.Focus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            MediaPlayer.Stop();
            MediaPlayer.Source = null;
            _timer.Stop();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isDragging && MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                _isTimerUpdating = true;
                TimeSlider.Value = MediaPlayer.Position.TotalSeconds;
                _isTimerUpdating = false;
                UpdateTimeText();
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying)
            {
                MediaPlayer.Pause();
                _timer.Stop();
                _isPlaying = false;
                PlayPauseButton.Content = "再生";
            }
            else
            {
                MediaPlayer.Play();
                _timer.Start();
                _isPlaying = true;
                PlayPauseButton.Content = "一時停止";
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            MediaPlayer.Stop();
            _timer.Stop();
            _isPlaying = false;
            MediaPlayer.Position = TimeSpan.Zero;
            PlayPauseButton.Content = "再生";
            TimeSlider.Value = 0;
            UpdateTimeText();
        }

        private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                TimeSlider.Maximum = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                UpdateTimeText();
            }
        }

        private void MediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            MediaPlayer.Stop();
            TimeSlider.Value = 0;
            _isPlaying = false;
            PlayPauseButton.Content = "再生";
        }

        private void TimeSlider_ThumbDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isDragging = true;
        }

        private void TimeSlider_ThumbDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isDragging = false;
            MediaPlayer.Position = TimeSpan.FromSeconds(TimeSlider.Value);
        }

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isTimerUpdating)
            {
                if (!_isDragging)
                    MediaPlayer.Position = TimeSpan.FromSeconds(TimeSlider.Value);
                UpdateTimeText();
            }
        }

        private void UpdateTimeText()
        {
            var current = TimeSpan.FromSeconds(TimeSlider.Value);
            var total = MediaPlayer.NaturalDuration.HasTimeSpan ? MediaPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero;
            TimeText.Text = $"{current:mm\\:ss} / {total:mm\\:ss}";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MediaPlayer != null) MediaPlayer.Volume = VolumeSlider.Value;
        }

        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MediaPlayer != null && SpeedComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (double.TryParse(item.Tag.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double speed))
                {
                    MediaPlayer.SpeedRatio = speed;
                }
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                PlayPause_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}