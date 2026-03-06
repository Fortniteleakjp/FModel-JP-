using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using ILGPU;
using ILGPU.Runtime;

namespace FModel.Features.Athena;

public sealed class GpuRandomAesKeyGenerator : IDisposable
{
    private const int UintPerKey = 8; // 8 * 4 = 32 bytes = AES-256 key
    public const int DefaultBatchSize = 1_048_576; // 1M keys per dispatch to keep GPU busier
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private readonly Action<Index1D, ArrayView<uint>, uint> _kernel;

    public bool IsHardwareAccelerated => _accelerator.AcceleratorType != AcceleratorType.CPU;

    public GpuRandomAesKeyGenerator()
    {
        _context = Context.CreateDefault();

        var device = _context.Devices
            .OrderBy(d => d.AcceleratorType == AcceleratorType.CPU ? 1 : 0)
            .FirstOrDefault();

        if (device == null)
            throw new InvalidOperationException("利用可能なILGPUデバイスが見つかりませんでした。");

        _accelerator = device.CreateAccelerator(_context);
        _kernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<uint>, uint>(GenerateKernel);
    }

    public IEnumerable<string> GenerateKeys(HashSet<string> excludedKeys = null, int batchSize = DefaultBatchSize)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        using var buffer = _accelerator.Allocate1D<uint>(batchSize * UintPerKey);
        var host = new uint[batchSize * UintPerKey];
        var keyBytes = new byte[32];

        while (true)
        {
            var seed = unchecked((uint)Random.Shared.NextInt64());
            _kernel(batchSize, buffer.View, seed);
            _accelerator.Synchronize();
            buffer.CopyToCPU(host);

            for (var i = 0; i < batchSize; i++)
            {
                var baseIdx = i * UintPerKey;
                keyBytes.AsSpan().Clear();

                for (var j = 0; j < UintPerKey; j++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(keyBytes.AsSpan(j * 4, 4), host[baseIdx + j]);
                }

                var key = "0x" + Convert.ToHexString(keyBytes);
                if (excludedKeys == null || !excludedKeys.Contains(key))
                    yield return key;
            }
        }
    }

    private static void GenerateKernel(Index1D index, ArrayView<uint> output, uint seed)
    {
        var state = seed ^ (uint)index ^ 0x9E3779B9u;
        var baseIdx = index * UintPerKey;

        for (var i = 0; i < UintPerKey; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            state += 0x9E3779B9u + (uint)i * 0x85EBCA6Bu;
            output[baseIdx + i] = state;
        }
    }

    public void Dispose()
    {
        _accelerator.Dispose();
        _context.Dispose();
    }
}
