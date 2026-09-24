using System;
using System.Collections.Generic;
using System.Threading;
using CSCore;

namespace FModel.Views.Resources.Controls.Aup;

/// <summary>
/// decodes an audio file once and reduces it to min/max pairs the waveform view can draw at any width
/// </summary>
public static class WaveformBuilder
{
    public const int BucketCount = 2048;
    private const int FramesPerBlock = 32;

    /// <returns>interleaved min/max per bucket ([min0, max0, min1, max1, ...]) normalized to [-1, 1], or null if it can't be decoded</returns>
    public static float[] Compute(byte[] data, string extension, CancellationToken cancellationToken)
    {
        if (data is not { Length: > 0 }) return null;

        IWaveSource waveSource = null;
        try
        {
            waveSource = new CustomCodecFactory().GetCodec(data, extension);
            if (waveSource == null) return null;

            var sampleSource = waveSource.ToSampleSource();
            var channels = Math.Max(1, sampleSource.WaveFormat.Channels);

            // the length is unknown for some decoders, so reduce to small blocks first and to buckets at the end
            var blocks = new List<(float Min, float Max)>();
            var buffer = new float[FramesPerBlock * channels * 16];
            var blockMin = float.MaxValue;
            var blockMax = float.MinValue;
            var blockSamples = 0;
            var samplesPerBlock = FramesPerBlock * channels;

            int read;
            while ((read = sampleSource.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i++)
                {
                    var sample = buffer[i];
                    if (sample < blockMin) blockMin = sample;
                    if (sample > blockMax) blockMax = sample;
                    if (++blockSamples < samplesPerBlock) continue;

                    blocks.Add((blockMin, blockMax));
                    blockMin = float.MaxValue;
                    blockMax = float.MinValue;
                    blockSamples = 0;
                }
            }

            if (blockSamples > 0) blocks.Add((blockMin, blockMax));
            return blocks.Count == 0 ? null : ToBuckets(blocks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // the player still works without a waveform
        }
        finally
        {
            waveSource?.Dispose();
        }
    }

    private static float[] ToBuckets(List<(float Min, float Max)> blocks)
    {
        var bucketCount = Math.Min(BucketCount, blocks.Count);
        var peaks = new float[bucketCount * 2];
        var peak = 0f;

        for (var b = 0; b < bucketCount; b++)
        {
            var start = (int) ((long) b * blocks.Count / bucketCount);
            var end = Math.Max(start + 1, (int) ((long) (b + 1) * blocks.Count / bucketCount));
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var i = start; i < end; i++)
            {
                if (blocks[i].Min < min) min = blocks[i].Min;
                if (blocks[i].Max > max) max = blocks[i].Max;
            }

            peaks[b * 2] = min;
            peaks[b * 2 + 1] = max;
            peak = Math.Max(peak, Math.Max(Math.Abs(min), Math.Abs(max)));
        }

        // game sounds are often mixed quiet, normalize so the shape is readable
        if (peak > 0.0001f)
        {
            var scale = 1f / peak;
            for (var i = 0; i < peaks.Length; i++)
                peaks[i] = Math.Clamp(peaks[i] * scale, -1f, 1f);
        }

        return peaks;
    }
}
