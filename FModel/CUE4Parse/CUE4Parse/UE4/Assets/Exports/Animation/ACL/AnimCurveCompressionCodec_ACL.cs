using System;
using System.Runtime.InteropServices;
using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.Engine.Curves;

namespace CUE4Parse.UE4.Assets.Exports.Animation.ACL;

public class AnimCurveCompressionCodec_ACL : UAnimCurveCompressionCodec
{
    public override unsafe FFloatCurve[] ConvertCurves(FSmartName[] names, byte[] data)
    {
        if (names.Length == 0 || data.Length == 0)
        {
            return [];
        }

        CompressedTracks compressedTracks;
        try
        {
            compressedTracks = new CompressedTracks(data);
        }
        catch (Exception e) when (e is ACLException or EntryPointNotFoundException or DllNotFoundException)
        {
            return [];
        }

        var header = compressedTracks.GetTracksHeader();
        var numSamples = header.NumSamples;

        var floatKeys = new float[names.Length * numSamples];
        fixed (float* floatKeysPtr = floatKeys)
        {
            try
            {
                nReadCurveACLData(compressedTracks.Handle, floatKeysPtr);
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                return [];
            }
        }

        var floatCurves = new FFloatCurve[names.Length];
        for (var curveIndex = 0; curveIndex < floatCurves.Length; curveIndex++)
        {
            var curveKeys = new float[numSamples];
            var offset = curveIndex * numSamples;
            Array.Copy(floatKeys, offset, curveKeys, 0, numSamples);

            var floatCurve = new FFloatCurve
            {
                CurveName = names[curveIndex].DisplayName,
                FloatCurve = new FRichCurve
                {
                    Keys = new FRichCurveKey[numSamples]
                }
            };

            for (var sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
            {
                floatCurve.FloatCurve.Keys[sampleIndex] = new FRichCurveKey
                {
                    Value = curveKeys[sampleIndex],
                    Time = sampleIndex / header.SampleRate
                };
            }

            floatCurves[curveIndex] = floatCurve;
        }

        return floatCurves;
    }

    [DllImport(ACLNative.LIB_NAME)]
    private static extern unsafe void nReadCurveACLData(IntPtr compressedTracks, float* outFloatKeys);
}
