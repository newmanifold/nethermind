// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Compares strategies for counting zero-valued bytes in UInt64 and UInt256 values.
/// Covers SWAR (current), Vector64/128/256 approaches with ExtractMsb and Sum variants.
/// </summary>
public class CountZeroBytesBenchmarks
{
    private ulong[] _ulongValues = null!;
    private UInt256[] _uint256Values = null!;

    [Params(64, 256)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _ulongValues = new ulong[Count];
        _uint256Values = new UInt256[Count];

        for (int i = 0; i < Count; i++)
        {
            // Biased toward zero/0x01 bytes to stress borrow-propagation edge cases
            _ulongValues[i] = NextBiasedUInt64(rng);

            byte[] buf = new byte[32];
            for (int j = 0; j < 32; j++)
            {
                int roll = rng.Next(10);
                buf[j] = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
            }
            _uint256Values[i] = new UInt256(buf.AsSpan(), isBigEndian: true);
        }
    }

    private static ulong NextBiasedUInt64(Random rng)
    {
        ulong value = 0;
        for (int pos = 0; pos < 8; pos++)
        {
            int roll = rng.Next(10);
            byte b = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
            value |= (ulong)b << (pos * 8);
        }
        return value;
    }

    // ── UInt64 benchmarks ──

    /// <summary>
    /// Current implementation: SWAR borrow-safe zero-byte detection + PopCount.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int UInt64_Swar()
    {
        int total = 0;
        ulong[] values = _ulongValues;
        for (int i = 0; i < values.Length; i++)
            total += values[i].CountZeroBytes();
        return total;
    }

    /// <summary>
    /// Naive byte-by-byte loop for baseline comparison.
    /// </summary>
    [Benchmark]
    public int UInt64_Naive()
    {
        int total = 0;
        ulong[] values = _ulongValues;
        for (int i = 0; i < values.Length; i++)
            total += NaiveCountZeroBytes(values[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NaiveCountZeroBytes(ulong value)
    {
        int count = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((value & 0xFF) == 0) count++;
            value >>= 8;
        }
        return count;
    }

    /// <summary>
    /// Vector64 approach for UInt64: load 8 bytes into a 64-bit SIMD register,
    /// compare + sum. Tests whether SIMD overhead is worth it for just 8 bytes.
    /// </summary>
    [Benchmark]
    public int UInt64_Vector64_Sum()
    {
        int total = 0;
        ulong[] values = _ulongValues;
        for (int i = 0; i < values.Length; i++)
            total += CountZeroBytesVector64(values[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytesVector64(ulong value)
    {
        Vector64<byte> data = Vector64.CreateScalar(value).AsByte();
        return Vector64.Sum(~Vector64.Equals(data, default) + Vector64<byte>.One);
    }

    /// <summary>
    /// Vector128 approach for UInt64: load 8 bytes into lower half of a 128-bit register.
    /// Upper 8 zero bytes are excluded by subtracting 8 from the count.
    /// </summary>
    [Benchmark]
    public int UInt64_Vector128_ExtractMsb()
    {
        int total = 0;
        ulong[] values = _ulongValues;
        for (int i = 0; i < values.Length; i++)
            total += CountZeroBytesVector128(values[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytesVector128(ulong value)
    {
        // Load into lower 8 bytes; upper 8 are zero → compare as zero → must subtract 8
        Vector128<byte> data = Vector128.CreateScalar(value).AsByte();
        uint mask = Vector128.ExtractMostSignificantBits(Vector128.Equals(data, default));
        return BitOperations.PopCount(mask) - 8;
    }

    // ── UInt256 benchmarks ──

    /// <summary>
    /// Current implementation: 4x SWAR across the four UInt64 limbs.
    /// </summary>
    [Benchmark]
    public int UInt256_4xSwar()
    {
        int total = 0;
        UInt256[] values = _uint256Values;
        for (int i = 0; i < values.Length; i++)
            total += values[i].CountZeroBytes();
        return total;
    }

    /// <summary>
    /// Vector256 approach using ExtractMostSignificantBits + PopCount.
    /// Compiles to vpcmpeqb + vpmovmskb + popcnt on x86.
    /// </summary>
    [Benchmark]
    public int UInt256_Vector_ExtractMsb()
    {
        int total = 0;
        UInt256[] values = _uint256Values;
        for (int i = 0; i < values.Length; i++)
            total += CountZeroBytesExtractMsb(in values[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytesExtractMsb(in UInt256 value)
    {
        Vector256<byte> data = Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in value));
        uint mask = Vector256.ExtractMostSignificantBits(Vector256.Equals(data, default));
        return BitOperations.PopCount(mask);
    }

    /// <summary>
    /// Vector256 approach using bitwise NOT + Add(One) + Sum.
    /// Avoids ExtractMostSignificantBits which may be slow on ARM.
    /// </summary>
    [Benchmark]
    public int UInt256_Vector_Sum()
    {
        int total = 0;
        UInt256[] values = _uint256Values;
        for (int i = 0; i < values.Length; i++)
            total += CountZeroBytesVectorSum(in values[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytesVectorSum(in UInt256 value)
    {
        Vector256<byte> data = Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in value));
        // Equals → 0xFF for zero bytes, 0x00 for non-zero
        // ~0xFF + 1 = 0x00 + 1 = 0x01 (zero byte → counted as 1)
        // ~0x00 + 1 = 0xFF + 1 = 0x00 (non-zero byte → wraps to 0, not counted)
        return Vector256.Sum(~Vector256.Equals(data, default) + Vector256<byte>.One);
    }
}
