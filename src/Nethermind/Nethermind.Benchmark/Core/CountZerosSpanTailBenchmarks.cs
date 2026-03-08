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
/// Measures the impact of the SWAR 8-byte tail branch in CountZeros.
/// Compares production code (V128 loop + SWAR tail + scalar) against
/// alternatives: scalar-only tail, V128 CreateScalar tail.
/// Sizes chosen to isolate tail behavior after the V128 (16-byte) loop.
/// </summary>
[MemoryDiagnoser]
public class CountZerosSpanTailBenchmarks
{
    private byte[] _data = null!;

    /// <summary>
    /// Tail scenarios after V128 (16-byte) loop:
    /// 4,7 = no V128 loop, pure tail (SWAR doesn't fire for 4, fires for neither);
    /// 8 = no V128 loop, SWAR fires exactly;
    /// 12 = no V128 loop, SWAR(8) + scalar(4);
    /// 20 = one V128(16) + 4-byte tail (SWAR skipped, 4 &lt; 8);
    /// 24 = one V128(16) + 8-byte tail (SWAR fires exactly);
    /// 25 = one V128(16) + SWAR(8) + scalar(1);
    /// 32 = two V128(16), zero tail;
    /// 64,256,1024 = bulk, tail cost amortized.
    /// </summary>
    [Params(4, 7, 8, 12, 20, 24, 25, 32, 64, 256, 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _data = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            // ~30% zeros, ~30% 0x01 (SWAR edge case), ~40% random non-zero
            int roll = rng.Next(10);
            _data[i] = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
        }
    }

    // ── Production: current Bytes.CountZeros ──

    /// <summary>
    /// Current production code. On ARM: V128 Sum loop + SWAR(8) + scalar(0-7).
    /// On x86: V512 → V256 → V128 ExtractMsb loop + SWAR(8) + scalar(0-7).
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Production() => ((ReadOnlySpan<byte>)_data).CountZeros();

    // ── V128 loop + scalar-only tail (no SWAR middle step) ──

    /// <summary>
    /// Same V128 loop as production, but skips the SWAR branch entirely.
    /// All remaining 0-15 bytes handled by scalar loop.
    /// Tests whether the SWAR branch adds overhead (extra comparison, code size)
    /// that outweighs its benefit on 8-15 byte tails.
    /// </summary>
    [Benchmark]
    public int V128_ScalarTail() => CountZeros_ScalarTail(_data);

    private static int CountZeros_ScalarTail(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;

            // Same platform-branching as production
            if (Vector256.IsHardwareAccelerated)
            {
                for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                    totalZeros += BitOperations.PopCount(
                        Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default)));
                }
            }
            else
            {
                for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                    totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One);
                }
            }

            data = data[i..];
        }

        // Pure scalar — no SWAR
        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── V128 loop + SWAR tail (isolated reimplementation of production tail) ──

    /// <summary>
    /// Same as production tail logic but without the V512/V256 cascade.
    /// Confirms production match and isolates SWAR tail cost.
    /// </summary>
    [Benchmark]
    public int V128_SwarTail() => CountZeros_SwarTail(_data);

    private static int CountZeros_SwarTail(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;

            if (Vector256.IsHardwareAccelerated)
            {
                for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                    totalZeros += BitOperations.PopCount(
                        Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default)));
                }
            }
            else
            {
                for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                    totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One);
                }
            }

            data = data[i..];
        }

        // SWAR for 8+ remaining bytes
        if (data.Length >= sizeof(ulong))
        {
            ulong value = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(data));
            totalZeros += CountZeroBytesSwar(value);
            data = data[sizeof(ulong)..];
        }

        // Scalar for 0-7 remaining
        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── V128 loop + V128 CreateScalar tail for 8-byte chunk ──

    /// <summary>
    /// After the V128 loop, uses V128 CreateScalar to process 8 remaining
    /// bytes via SIMD instead of SWAR. Loads 8 bytes into the lower half
    /// of a 128-bit register (upper 8 are zero), then subtracts 8 from
    /// the count to compensate for the zero-padded upper half.
    /// </summary>
    [Benchmark]
    public int V128_V128Tail() => CountZeros_V128Tail(_data);

    private static int CountZeros_V128Tail(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;

            if (Vector256.IsHardwareAccelerated)
            {
                for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                    totalZeros += BitOperations.PopCount(
                        Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default)));
                }
            }
            else
            {
                for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                    totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One);
                }
            }

            data = data[i..];

            // V128 CreateScalar for 8-15 remaining bytes
            if (data.Length >= sizeof(ulong))
            {
                ulong value = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(data));
                Vector128<byte> v = Vector128.CreateScalar(value).AsByte();

                // Platform-optimal count, subtract 8 for zero-padded upper half
                if (Vector256.IsHardwareAccelerated)
                {
                    totalZeros += BitOperations.PopCount(
                        Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default))) - 8;
                }
                else
                {
                    totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One) - 8;
                }

                data = data[sizeof(ulong)..];
            }
        }

        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Original master: V512→V256→V128 ExtractMsb + scalar tail ──

    /// <summary>
    /// Exact copy of master's CountZeros before any changes.
    /// V512 → V256 → V128 cascade, all ExtractMsb, pure scalar tail.
    /// No SWAR, no ARM Sum branch. This is the true baseline to beat.
    /// </summary>
    [Benchmark]
    public int Original() => CountZeros_Original(_data);

    private static int CountZeros_Original(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;
        if (Vector512.IsHardwareAccelerated && data.Length >= Vector512<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i < data.Length - Vector512<byte>.Count; i += Vector512<byte>.Count)
            {
                Vector512<byte> dataVector = Unsafe.ReadUnaligned<Vector512<byte>>(ref Unsafe.Add(ref bytes, i));
                ulong flags = Vector512.Equals(dataVector, default).ExtractMostSignificantBits();
                totalZeros += BitOperations.PopCount(flags);
            }
            data = data[i..];
        }
        if (Vector256.IsHardwareAccelerated && data.Length >= Vector256<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i < data.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> dataVector = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref bytes, i));
                uint flags = Vector256.Equals(dataVector, default).ExtractMostSignificantBits();
                totalZeros += BitOperations.PopCount(flags);
            }
            data = data[i..];
        }
        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i < data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> dataVector = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), i));
                uint flags = Vector128.Equals(dataVector, default).ExtractMostSignificantBits();
                totalZeros += BitOperations.PopCount(flags);
            }
            data = data[i..];
        }

        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                totalZeros++;
            }
        }
        return totalZeros;
    }

    // ── ARM Sum loop + scalar tail (no SWAR) ──

    /// <summary>
    /// Uses our ARM-optimized V128 Sum loop but with scalar-only tail.
    /// Tests the Sum loop improvement in isolation, without SWAR tail.
    /// </summary>
    [Benchmark]
    public int ArmSum_ScalarTail() => CountZeros_ArmSum_ScalarTail(_data);

    private static int CountZeros_ArmSum_ScalarTail(ReadOnlySpan<byte> data)
    {
        int totalZeros = 0;

        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;

            // ARM Sum loop only — no ExtractMsb/x86 branch
            for (; i <= data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref bytes, i));
                totalZeros += Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One);
            }

            data = data[i..];
        }

        // Pure scalar tail — no SWAR
        for (int j = 0; j < data.Length; j++)
        {
            if (data[j] == 0) totalZeros++;
        }

        return totalZeros;
    }

    // ── Pure SWAR (no SIMD) ──

    /// <summary>
    /// No SIMD at all. Processes 8 bytes at a time using SWAR bitmask tricks.
    /// Lower bound for platforms without hardware vector support.
    /// </summary>
    [Benchmark]
    public int PureSwar() => CountZeros_PureSwar(_data);

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
