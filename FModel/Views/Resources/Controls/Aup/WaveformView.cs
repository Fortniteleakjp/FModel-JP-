using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FModel.Views.Resources.Controls.Aup;

/// <summary>
/// draws the waveform of the played file, colors what has already been played and seeks on click
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private enum EState
    {
        Empty,
        Loading,
        Ready,
        Failed
    }

    private ISource _source;
    private EState _state = EState.Empty;
    private float[] _peaks;
    private double _progress;
    private double? _hoverX;

    private StreamGeometry _geometry;
    private Size _geometrySize;

    public ISource Source
    {
        get => (ISource) GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ISource), typeof(WaveformView),
            new FrameworkPropertyMetadata(null, (o, e) => ((WaveformView) o).OnSourceChanged((ISource) e.OldValue, (ISource) e.NewValue)));

    public Brush WaveBrush
    {
        get => (Brush) GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }
    public static readonly DependencyProperty WaveBrushProperty =
        DependencyProperty.Register(nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush PlayedBrush
    {
        get => (Brush) GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }
    public static readonly DependencyProperty PlayedBrushProperty =
        DependencyProperty.Register(nameof(PlayedBrush), typeof(Brush), typeof(WaveformView),
            new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush CursorBrush
    {
        get => (Brush) GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }
    public static readonly DependencyProperty CursorBrushProperty =
        DependencyProperty.Register(nameof(CursorBrush), typeof(Brush), typeof(WaveformView),
            new FrameworkPropertyMetadata(Brushes.Brown, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush) GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }
    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(WaveformView),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public WaveformView()
    {
        Cursor = Cursors.Hand;
        ClipToBounds = true;

        // the audio player view model outlives its window, don't keep closed windows alive through its events
        Loaded += (_, _) => Attach(Source);
        Unloaded += (_, _) => Detach();
    }

    private void OnSourceChanged(ISource oldValue, ISource newValue)
    {
        Detach();
        if (IsLoaded) Attach(newValue);
    }

    private void Attach(ISource source)
    {
        if (source == null || _source == source) return;
        Detach();

        _source = source;
        _source.SourceEvent += OnSourceEvent;
        _source.SourcePropertyChangedEvent += OnSourcePropertyChangedEvent;
        // the file may already be playing when the window gets loaded, its peaks still coming
        var peaks = _source.WaveformPeaks;
        var state = peaks switch
        {
            { Length: > 0 } => EState.Ready,
            { Length: 0 } => EState.Failed,
            _ => _source.PlayedFile?.Id >= 0 ? EState.Loading : EState.Empty
        };
        SetPeaks(peaks, state);
    }

    private void Detach()
    {
        if (_source == null) return;

        _source.SourceEvent -= OnSourceEvent;
        _source.SourcePropertyChangedEvent -= OnSourcePropertyChangedEvent;
        _source = null;
    }

    private void OnSourceEvent(object sender, SourceEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _progress = 0;
            // the peaks are computed in the background, they come later through WaveformData
            SetPeaks(null, e.Event == ESourceEventType.Loading ? EState.Loading : EState.Empty);
        });
    }

    private void OnSourcePropertyChangedEvent(object sender, SourcePropertyChangedEventArgs e)
    {
        switch (e.Property)
        {
            case ESourceProperty.WaveformData:
            {
                var peaks = e.Value as float[];
                Dispatcher.BeginInvoke(() => SetPeaks(peaks, peaks is { Length: > 0 } ? EState.Ready : EState.Failed));
                break;
            }
            case ESourceProperty.Position:
            {
                var position = (TimeSpan) e.Value;
                Dispatcher.BeginInvoke(() =>
                {
                    var duration = _source?.PlayedFile.Duration.TotalMilliseconds ?? 0;
                    var progress = duration > 0 ? Math.Clamp(position.TotalMilliseconds / duration, 0, 1) : 0;

                    // the player ticks every 10ms, only redraw when the play head actually moves a pixel
                    if (Math.Abs((progress - _progress) * ActualWidth) < 0.5) return;
                    _progress = progress;
                    InvalidateVisual();
                });
                break;
            }
        }
    }

    private void SetPeaks(float[] peaks, EState state)
    {
        _peaks = peaks;
        _state = state;
        _geometry = null;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_source == null || ActualWidth < 1 || _state != EState.Ready) return;

        _source.SkipTo(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1));
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _geometry = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 1 || height < 1) return;

        // transparent background so the whole area is clickable
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var mid = Math.Round(height / 2) + 0.5;
        dc.DrawLine(new Pen(WaveBrush, 1), new Point(0, mid), new Point(width, mid));

        if (_state != EState.Ready || _peaks is not { Length: > 0 })
        {
            var message = _state switch
            {
                EState.Loading => TryFindResource("UI_WaveformLoading") as string ?? "Analyzing waveform...",
                EState.Failed => TryFindResource("UI_WaveformUnavailable") as string ?? "Waveform unavailable",
                _ => null
            };
            if (message != null) DrawCenteredText(dc, message, width, height);
            return;
        }

        var geometry = GetGeometry(width, height);
        var playedX = _progress * width;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, playedX, height)));
        dc.DrawGeometry(PlayedBrush, null, geometry);
        dc.Pop();

        dc.PushClip(new RectangleGeometry(new Rect(playedX, 0, Math.Max(0, width - playedX), height)));
        dc.DrawGeometry(WaveBrush, null, geometry);
        dc.Pop();

        if (_progress > 0)
            dc.DrawRectangle(CursorBrush, null, new Rect(Math.Min(playedX, width - 1), 0, 1, height));

        if (_hoverX is { } hoverX)
            dc.DrawRectangle(CursorBrush, null, new Rect(Math.Clamp(hoverX, 0, width - 2), 0, 2, height));
    }

    /// <summary>
    /// one closed shape: the top edge from left to right, then the bottom edge back
    /// </summary>
    private StreamGeometry GetGeometry(double width, double height)
    {
        var size = new Size(width, height);
        if (_geometry != null && _geometrySize == size) return _geometry;

        var columns = Math.Max(1, (int) Math.Ceiling(width));
        var bucketCount = _peaks.Length / 2;
        var tops = new double[columns];
        var bottoms = new double[columns];
        var half = height / 2 - 1;

        for (var x = 0; x < columns; x++)
        {
            var start = (int) ((long) x * bucketCount / columns);
            var end = Math.Max(start + 1, (int) ((long) (x + 1) * bucketCount / columns));
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var b = start; b < end && b < bucketCount; b++)
            {
                min = Math.Min(min, _peaks[b * 2]);
                max = Math.Max(max, _peaks[b * 2 + 1]);
            }

            // keep silence visible as a thin line
            tops[x] = height / 2 - Math.Max(max * half, 0.5);
            bottoms[x] = height / 2 - Math.Min(min * half, -0.5);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, tops[0]), true, true);
            for (var x = 0; x < columns; x++)
            {
                ctx.LineTo(new Point(x, tops[x]), false, false);
                ctx.LineTo(new Point(x + 1, tops[x]), false, false);
            }
            for (var x = columns - 1; x >= 0; x--)
            {
                ctx.LineTo(new Point(x + 1, bottoms[x]), false, false);
                ctx.LineTo(new Point(x, bottoms[x]), false, false);
            }
        }
        geometry.Freeze();

        _geometry = geometry;
        _geometrySize = size;
        return geometry;
    }

    private void DrawCenteredText(DrawingContext dc, string text, double width, double height)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2 - 8));
    }
}
