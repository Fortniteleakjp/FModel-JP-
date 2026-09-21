using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using FModel.Extensions;

namespace FModel.Views.Resources.Controls.Diff;

public partial class DataDiffViewer
{
    private readonly List<string> _leftChunks;
    private readonly List<string> _rightChunks;

    private ScrollViewer _scroll;

    private int _loadedChunkIndex;
    private const int ChunksPerLoad = 1;
    private bool _isLoading;

    private readonly DiffAlignment _globalAlignment = new([], [], []);
    private readonly HashSet<string> _globalMovedStrings = [];

    private DataDiffColorizer _globalLeftColorizer;
    private DataDiffColorizer _globalRightColorizer;
    private GapWidthBackgroundRenderer _leftGapRenderer;
    private GapWidthBackgroundRenderer _rightGapRenderer;

    private double _monospaceCharWidth;

    public DataDiffViewer(List<string> leftChunks, List<string> rightChunks, string extension)
    {
        InitializeComponent();

        var highlighter = AvalonExtensions.HighlighterSelector(extension);
        var linkBrush = Brushes.Cornsilk;
        AvalonLeft.TextArea.TextView.LinkTextForegroundBrush = linkBrush;
        AvalonRight.TextArea.TextView.LinkTextForegroundBrush = linkBrush;
        AvalonLeft.SyntaxHighlighting = highlighter;
        AvalonRight.SyntaxHighlighting = highlighter;

        _leftChunks = leftChunks ?? [];
        _rightChunks = rightChunks ?? [];

        Loaded += DataDiffViewer_Loaded;
    }

    public async Task Initialize()
    {
        await LoadInitialDiff();
    }

    private async Task LoadInitialDiff()
    {
        _loadedChunkIndex = 0;
        AvalonLeft.Text = string.Empty;
        AvalonRight.Text = string.Empty;

        _globalMovedStrings.Clear();

        if (_globalLeftColorizer != null)
            AvalonLeft.TextArea.TextView.LineTransformers.Remove(_globalLeftColorizer);
        if (_globalRightColorizer != null)
            AvalonRight.TextArea.TextView.LineTransformers.Remove(_globalRightColorizer);

        if (_leftGapRenderer != null)
            AvalonLeft.TextArea.TextView.BackgroundRenderers.Remove(_leftGapRenderer);
        if (_rightGapRenderer != null)
            AvalonRight.TextArea.TextView.BackgroundRenderers.Remove(_rightGapRenderer);

        _globalLeftColorizer = null;
        _globalRightColorizer = null;

        await LoadMoreChunksAsync();
    }

    private async Task LoadMoreChunksAsync()
    {
        if (_isLoading)
            return;

        if (_loadedChunkIndex >= Math.Max(_leftChunks.Count, _rightChunks.Count))
            return;

        _isLoading = true;

        int chunksToLoad = Math.Min(ChunksPerLoad, Math.Max(_leftChunks.Count, _rightChunks.Count) - _loadedChunkIndex);

        for (int i = 0; i < chunksToLoad; i++)
        {
            string leftChunk = _loadedChunkIndex + i < _leftChunks.Count ? _leftChunks[_loadedChunkIndex + i] : "";
            string rightChunk = _loadedChunkIndex + i < _rightChunks.Count ? _rightChunks[_loadedChunkIndex + i] : "";

            // everything that does not touch a UI object is done off the dispatcher, otherwise a file with
            // tens of thousands of lines freezes the whole window while it is being aligned and joined
            var (alignment, moved, leftText, rightText) = await Task.Run(() =>
            {
                var model = new SideBySideDiffBuilder().BuildDiffModel(leftChunk, rightChunk);
                var a = AlignLinesWithGaps(model);
                var m = a.Meta
                    .Where(x => x.Old != null && x.New != null && x.Old.Text == x.New.Text)
                    .Select(x => x.New.Text)
                    .ToList();

                return (a, m, string.Join("\n", a.LeftLines) + "\n", string.Join("\n", a.RightLines) + "\n");
            });

            _globalAlignment.LeftLines.AddRange(alignment.LeftLines);
            _globalAlignment.RightLines.AddRange(alignment.RightLines);
            _globalAlignment.Meta.AddRange(alignment.Meta);

            foreach (var text in moved)
            {
                _globalMovedStrings.Add(text);
            }

            AvalonLeft.Document.BeginUpdate();
            AvalonRight.Document.BeginUpdate();

            if (_loadedChunkIndex == 0)
            {
                AvalonLeft.Document.Text = leftText;
                AvalonRight.Document.Text = rightText;
            }
            else
            {
                AvalonLeft.Document.Text += leftText;
                AvalonRight.Document.Text += rightText;
            }

            AvalonLeft.Document.EndUpdate();
            AvalonRight.Document.EndUpdate();

            _loadedChunkIndex++;
        }

        SetupGapRenderers();

        if (_globalLeftColorizer == null)
        {
            _globalLeftColorizer = new DataDiffColorizer(_globalAlignment, _globalMovedStrings, isLeft: true);
            AvalonLeft.TextArea.TextView.LineTransformers.Add(_globalLeftColorizer);
        }


        if (_globalRightColorizer == null)
        {
            _globalRightColorizer = new DataDiffColorizer(_globalAlignment, _globalMovedStrings, isLeft: false);
            AvalonRight.TextArea.TextView.LineTransformers.Add(_globalRightColorizer);
        }

        AvalonLeft.TextArea.TextView.Redraw();
        AvalonRight.TextArea.TextView.Redraw();

        _isLoading = false;
    }

    // Only reference to one scroll is needed because they are aligned
    private void DataDiffViewer_Loaded(object sender, RoutedEventArgs e)
    {
        _scroll = FindScrollViewer(AvalonLeft);

        if (_scroll == null) return;

        _scroll.ScrollChanged += Scroll_ScrollChanged;

        DiffNavbar.Attach(_scroll, _globalAlignment.Meta, _globalMovedStrings);
        DiffNavbar.LineClicked += line =>
        {
            _scroll.ScrollToVerticalOffset(line * _scroll.ExtentHeight / DiffNavbar.TotalLines);
        };
    }

    private async void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoading)
            return;

        if (sender is not ScrollViewer sv || !IsNearBottom(sv)) return;

        if (_loadedChunkIndex >= Math.Max(_leftChunks.Count, _rightChunks.Count))
            return;

        await LoadMoreChunksAsync();
        DiffNavbar.UpdateNavbar(_globalAlignment.Meta, _globalMovedStrings, true);
    }

    private static bool IsNearBottom(ScrollViewer sv)
    {
        return sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 50;
    }

    private static ScrollViewer FindScrollViewer(DependencyObject d)
    {
        switch (d)
        {
            case null:
                return null;
            case ScrollViewer sv:
                return sv;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private static DiffAlignment AlignLinesWithGaps(SideBySideDiffModel model)
    {
        var leftLines = new List<string>();
        var rightLines = new List<string>();
        var meta = new List<LineMeta>();

        int max = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);
        for (int i = 0; i < max; i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            string leftText = (oldLine == null || oldLine.Type == ChangeType.Imaginary) ? "" : oldLine.Text;
            string rightText = (newLine == null || newLine.Type == ChangeType.Imaginary) ? "" : newLine.Text;

            leftLines.Add(leftText);
            rightLines.Add(rightText);
            meta.Add(new LineMeta(oldLine, newLine));
        }

        return new DiffAlignment(leftLines, rightLines, meta);
    }

    private void SetupGapRenderers()
    {
        if (_leftGapRenderer != null)
            AvalonLeft.TextArea.TextView.BackgroundRenderers.Remove(_leftGapRenderer);
        if (_rightGapRenderer != null)
            AvalonRight.TextArea.TextView.BackgroundRenderers.Remove(_rightGapRenderer);

        var leftGapMap = new Dictionary<int, double>();
        var rightGapMap = new Dictionary<int, double>();
        var charWidth = GetMonospaceCharWidth();

        for (int i = 0; i < _globalAlignment.Meta.Count; i++)
        {
            var meta = _globalAlignment.Meta[i];
            if (meta.Old == null || meta.Old.Type == ChangeType.Imaginary)
            {
                leftGapMap[i] = MeasureCells(_globalAlignment.RightLines[i]) * charWidth;
            }
            if (meta.New == null || meta.New.Type == ChangeType.Imaginary)
            {
                rightGapMap[i] = MeasureCells(_globalAlignment.LeftLines[i]) * charWidth;
            }
        }

        _leftGapRenderer = new GapWidthBackgroundRenderer(leftGapMap);
        _rightGapRenderer = new GapWidthBackgroundRenderer(rightGapMap);

        AvalonLeft.TextArea.TextView.BackgroundRenderers.Add(_leftGapRenderer);
        AvalonRight.TextArea.TextView.BackgroundRenderers.Add(_rightGapRenderer);
    }

    /// <summary>
    /// Width of a line in character cells, CJK glyphs taking two of them. Assets carry localized strings,
    /// so counting raw characters would make the gap stripe too short on those lines.
    /// </summary>
    private static int MeasureCells(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int cells = 0;
        foreach (var c in text)
        {
            cells += IsWide(c) ? 2 : 1;
        }

        return cells;
    }

    private static bool IsWide(char c) => c switch
    {
        >= '\u1100' and <= '\u115F' => true, // hangul jamo
        >= '\u2E80' and <= '\uA4CF' => true, // cjk radicals, kana, cjk ideographs
        >= '\uAC00' and <= '\uD7A3' => true, // hangul syllables
        >= '\uF900' and <= '\uFAFF' => true, // cjk compatibility ideographs
        >= '\uFE30' and <= '\uFE6F' => true, // cjk compatibility forms
        >= '\uFF00' and <= '\uFF60' => true, // fullwidth forms
        >= '\uFFE0' and <= '\uFFE6' => true, // fullwidth signs
        _ => false
    };

    /// <summary>
    /// Both editors use a fixed width font, so the gap stripes can be sized from a single character
    /// measurement. Building a <see cref="FormattedText"/> per line costs seconds on a 70k lines file.
    /// </summary>
    private double GetMonospaceCharWidth()
    {
        if (_monospaceCharWidth > 0)
            return _monospaceCharWidth;

        var typeface = new Typeface(AvalonLeft.FontFamily, AvalonLeft.FontStyle, AvalonLeft.FontWeight, AvalonLeft.FontStretch);
        var formatted = new FormattedText(
            "0",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            AvalonLeft.FontSize,
            Brushes.Transparent,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _monospaceCharWidth = formatted.WidthIncludingTrailingWhitespace;
        return _monospaceCharWidth;
    }
}