// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Benchmarks <see cref="Bytes.CountZeros"/> against optimized variants.
/// Tests the Vector128 codepath (active on ARM), the scalar tail,
/// and forced Vector256/Sum paths to measure decomposition overhead.
/// </summary>
public class CountZerosSpanBenchmarks
{
    private byte[] _data = null!;

    /// <summary>
    /// Data sizes chosen to exercise different codepaths:
    /// 8 = scalar/SWAR only, 20 = one Vector128 + 4-byte tail,
    /// 32 = two Vector128 iterations, 64/128/256 = multiple iterations,
    /// 1024 = large buffer.
    /// </summary>
    [Params(8, 20, 32, 64, 128, 256, 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _data = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            int roll = rng.Next(10);
            _data[i] = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
        }
    }

    // ── Baseline: current CountZeros implementation ──

    /// <summary>
    /// Current production implementation from Bytes.cs: Vector512 → Vector256 → Vector128 → scalar loop.
    /// On ARM only the Vector128 and scalar paths are active.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Current()
    {
        return ((ReadOnlySpan<byte>)_data).CountZeros();
    }

    // ── Optimized: Vector128 loop + SWAR for 8-byte tail + scalar for 0-7 remainder ──

    /// <summary>
    /// Replaces the scalar tail with SWAR for remaining 8+ bytes after Vector128 loop.
    /// SWAR processes 8 bytes in ~5 ALU ops vs 8 branch-laden iterations.
    /// </summary>
    [Benchmark]
    public int Optimized_V128_SwarTail()
    {
        return CountZeros_V128_SwarTail(_data);
    }

    private static int CountZeros_V128_SwarTail(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
                totalZeros += BitOperations.PopCount(flags);
            }
            data = data[i..];
        }

        // SWAR for remaining 8+ bytes
        if (data.Length >= sizeof(ulong))
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            ulong value = Unsafe.ReadUnaligned<ulong>(ref bytes);
            totalZeros += CountZeroBytesSwar(value);
            data = data[sizeof(ulong)..];
        }

        // Scalar for remaining 0-7 bytes
        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Optimized: Vector128 loop + Vector128 ExtractMsb for 8-byte tail ──

    /// <summary>
    /// Uses Vector128 with CreateScalar for the 8-byte tail instead of SWAR.
    /// Benchmarked as 1.9x faster than SWAR for 8 bytes on ARM.
    /// </summary>
    [Benchmark]
    public int Optimized_V128_V128Tail()
    {
        return CountZeros_V128_V128Tail(_data);
    }

    private static int CountZeros_V128_V128Tail(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
                totalZeros += BitOperations.PopCount(flags);
            }
            data = data[i..];

            // Vector128 for remaining 8-15 bytes: load 8 bytes into lower half
            if (data.Length >= sizeof(ulong))
            {
                ulong value = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(data));
                Vector128<byte> v = Vector128.CreateScalar(value).AsByte();
                uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
                totalZeros += BitOperations.PopCount(flags) - 8; // subtract zero-filled upper half
                data = data[sizeof(ulong)..];
            }
        }

        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Forced Vector256 Sum path (decomposed to 2×Vector128 on ARM) ──

    /// <summary>
    /// Forces the Vector256 Sum approach even on ARM where Vector256 is not hardware-accelerated.
    /// Measures the cost of JIT decomposition into 2×Vector128 operations.
    /// </summary>
    [Benchmark]
    public int Forced_V256_Sum()
    {
        return CountZeros_V256Sum(_data);
    }

    private static int CountZeros_V256Sum(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;
        ref byte bytes = ref MemoryMarshal.GetReference(data);
        int i = 0;

        // Vector256 path — on ARM this decomposes to 2×Vector128 per iteration
        for (; i <= data.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
        {
            Vector256<byte> v = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref bytes, i));
            totalZeros += Vector256.Sum(~Vector256.Equals(v, default) + Vector256<byte>.One);
        }

        // Vector128 for remaining 16-31 bytes
        for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
        {
            Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
            uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
            totalZeros += BitOperations.PopCount(flags);
        }

        // Vector128 for 8-byte tail
        if (i + sizeof(ulong) <= data.Length)
        {
            ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, i));
            Vector128<byte> v = Vector128.CreateScalar(value).AsByte();
            uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
            totalZeros += BitOperations.PopCount(flags) - 8;
            i += sizeof(ulong);
        }

        for (; i < data.Length; i++)
        {
            if (data[i] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Forced Vector256 ExtractMsb path ──

    /// <summary>
    /// Forces Vector256 ExtractMsb on ARM to measure its decomposition overhead
    /// compared to Vector256 Sum.
    /// </summary>
    [Benchmark]
    public int Forced_V256_ExtractMsb()
    {
        return CountZeros_V256ExtractMsb(_data);
    }

    private static int CountZeros_V256ExtractMsb(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;
        ref byte bytes = ref MemoryMarshal.GetReference(data);
        int i = 0;

        for (; i <= data.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
        {
            Vector256<byte> v = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref bytes, i));
            uint flags = Vector256.ExtractMostSignificantBits(Vector256.Equals(v, default));
            totalZeros += BitOperations.PopCount(flags);
        }

        for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
        {
            Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
            uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
            totalZeros += BitOperations.PopCount(flags);
        }

        if (i + sizeof(ulong) <= data.Length)
        {
            ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, i));
            Vector128<byte> v = Vector128.CreateScalar(value).AsByte();
            uint flags = Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default));
            totalZeros += BitOperations.PopCount(flags) - 8;
            i += sizeof(ulong);
        }

        for (; i < data.Length; i++)
        {
            if (data[i] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Vector128 Sum path (native on ARM) ──

    /// <summary>
    /// Vector128 loop using Sum instead of ExtractMsb.
    /// Tests whether Sum is faster than ExtractMsb at the native 128-bit width on ARM.
    /// </summary>
    [Benchmark]
    public int V128_Sum()
    {
        return CountZeros_V128Sum(_data);
    }

    private static int CountZeros_V128Sum(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One);
            }
            data = data[i..];
        }

        if (data.Length >= sizeof(ulong))
        {
            ulong value = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(data));
            Vector128<byte> v = Vector128.CreateScalar(value).AsByte();
            totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One) - 8;
            data = data[sizeof(ulong)..];
        }

        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Pure scalar: SWAR only, no SIMD ──

    /// <summary>
    /// Pure SWAR scalar path — no SIMD at all. Processes 8 bytes at a time using
    /// bitmask tricks. Useful as a baseline on platforms without SIMD.
    /// </summary>
    [Benchmark]
    public int Pure_Swar()
    {
        return CountZeros_PureSwar(_data);
    }

    private static int CountZeros_PureSwar(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;
        ref byte bytes = ref MemoryMarshal.GetReference(data);
        int i = 0;

        for (; i <= data.Length - sizeof(ulong); i += sizeof(ulong))
        {
            ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, i));
            totalZeros += CountZeroBytesSwar(value);
        }

        for (; i < data.Length; i++)
        {
            if (data[i] == 0) totalZeros++;
        }

        return totalZeros;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytesSwar(ulong value)
    {
        ulong mask = ~(((value & 0x7F7F7F7F7F7F7F7FUL) + 0x7F7F7F7F7F7F7F7FUL) | value | 0x7F7F7F7F7F7F7F7FUL);
        return BitOperations.PopCount(mask);
    }
}
