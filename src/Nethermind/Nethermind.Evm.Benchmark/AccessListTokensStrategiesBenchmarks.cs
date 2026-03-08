#nullable enable
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// End-to-end benchmark of access list token calculation using different
/// zero-byte counting strategies for the UInt256 storage keys and address bytes.
/// Measures real-world impact of SWAR vs Vector128 vs Vector256 approaches
/// in the context of actual gas calculation, not isolated micro-ops.
/// Uses Osaka spec (latest on master) for TxDataNonZeroMultiplier.
/// </summary>
[MemoryDiagnoser]
public class AccessListTokensStrategiesBenchmarks
{
    private static readonly IReleaseSpec Spec = Osaka.Instance;

    private Transaction _smallTx = null!;   // 1 address, 0 storage keys
    private Transaction _mediumTx = null!;  // 10 addresses, 5 storage keys each
    private Transaction _largeTx = null!;   // 50 addresses, 20 storage keys each

    [GlobalSetup]
    public void GlobalSetup()
    {
        _smallTx = BuildTransaction(addresses: 1, keysPerAddress: 0);
        _mediumTx = BuildTransaction(addresses: 10, keysPerAddress: 5);
        _largeTx = BuildTransaction(addresses: 50, keysPerAddress: 20);
    }

    // ── Production: uses UInt256Extensions.CountZeroBytes (SIMD 3-tier) ──

    /// <summary>
    /// Production path: calls the SIMD-optimized CountZeroBytes from UInt256Extensions
    /// (V256 ExtractMsb on x86, 2×V128 Sum on ARM, 4×SWAR fallback).
    /// </summary>
    [Benchmark(Baseline = true, Description = "Production_SIMD — small")]
    public long Production_Small() => CalcTokens_Production(_smallTx);

    [Benchmark(Description = "Production_SIMD — medium")]
    public long Production_Medium() => CalcTokens_Production(_mediumTx);

    [Benchmark(Description = "Production_SIMD — large")]
    public long Production_Large() => CalcTokens_Production(_largeTx);

    // ── Vector256 Sum for UInt256, V128 ExtractMsb for address ──

    [Benchmark(Description = "V256Sum — small")]
    public long V256Sum_Small() => CalcTokens_V256Sum(_smallTx);

    [Benchmark(Description = "V256Sum — medium")]
    public long V256Sum_Medium() => CalcTokens_V256Sum(_mediumTx);

    [Benchmark(Description = "V256Sum — large")]
    public long V256Sum_Large() => CalcTokens_V256Sum(_largeTx);

    // ── Vector256 ExtractMsb for UInt256, V128 ExtractMsb for address ──

    [Benchmark(Description = "V256ExtractMsb — small")]
    public long V256ExtractMsb_Small() => CalcTokens_V256ExtractMsb(_smallTx);

    [Benchmark(Description = "V256ExtractMsb — medium")]
    public long V256ExtractMsb_Medium() => CalcTokens_V256ExtractMsb(_mediumTx);

    [Benchmark(Description = "V256ExtractMsb — large")]
    public long V256ExtractMsb_Large() => CalcTokens_V256ExtractMsb(_largeTx);

    // ── V128 Sum for both (2× V128 iterations for UInt256) ──

    [Benchmark(Description = "V128Sum — small")]
    public long V128Sum_Small() => CalcTokens_V128Sum(_smallTx);

    [Benchmark(Description = "V128Sum — medium")]
    public long V128Sum_Medium() => CalcTokens_V128Sum(_mediumTx);

    [Benchmark(Description = "V128Sum — large")]
    public long V128Sum_Large() => CalcTokens_V128Sum(_largeTx);

    // ── Span-based: ToBigEndian + CountZeros on Span<byte> ──

    [Benchmark(Description = "SpanCountZeros — small")]
    public long SpanCountZeros_Small() => CalcTokens_SpanCountZeros(_smallTx);

    [Benchmark(Description = "SpanCountZeros — medium")]
    public long SpanCountZeros_Medium() => CalcTokens_SpanCountZeros(_mediumTx);

    [Benchmark(Description = "SpanCountZeros — large")]
    public long SpanCountZeros_Large() => CalcTokens_SpanCountZeros(_largeTx);

    // ── V128 ExtractMsb for both ──

    [Benchmark(Description = "V128ExtractMsb — small")]
    public long V128ExtractMsb_Small() => CalcTokens_V128ExtractMsb(_smallTx);

    [Benchmark(Description = "V128ExtractMsb — medium")]
    public long V128ExtractMsb_Medium() => CalcTokens_V128ExtractMsb(_mediumTx);

    [Benchmark(Description = "V128ExtractMsb — large")]
    public long V128ExtractMsb_Large() => CalcTokens_V128ExtractMsb(_largeTx);

    // ═══════════════════════════════════════════════════════════════════════
    // Implementations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Uses the production UInt256Extensions.CountZeroBytes (3-tier SIMD)
    /// and V128 ExtractMsb for addresses.
    /// </summary>
    private static long CalcTokens_Production(Transaction tx)
    {
        AccessList? accessList = tx.AccessList;
        if (accessList is null) return 0L;
        long tokens = 0;
        long mul = Spec.GasCosts.TxDataNonZeroMultiplier;
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            int az = CountZerosAddress(address.Bytes);
            tokens += az + (20 - az) * mul;
            foreach (UInt256 key in storageKeys)
            {
                int kz = key.CountZeroBytes();
                tokens += kz + (32 - kz) * mul;
            }
        }
        return tokens;
    }

    private static long CalcTokens_V256Sum(Transaction tx)
    {
        AccessList? accessList = tx.AccessList;
        if (accessList is null) return 0L;
        long tokens = 0;
        long mul = Spec.GasCosts.TxDataNonZeroMultiplier;
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            int az = CountZerosAddress(address.Bytes);
            tokens += az + (20 - az) * mul;
            foreach (UInt256 key in storageKeys)
            {
                int kz = CountZeroBytes_V256Sum(in key);
                tokens += kz + (32 - kz) * mul;
            }
        }
        return tokens;
    }

    private static long CalcTokens_V256ExtractMsb(Transaction tx)
    {
        AccessList? accessList = tx.AccessList;
        if (accessList is null) return 0L;
        long tokens = 0;
        long mul = Spec.GasCosts.TxDataNonZeroMultiplier;
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            int az = CountZerosAddress(address.Bytes);
            tokens += az + (20 - az) * mul;
            foreach (UInt256 key in storageKeys)
            {
                int kz = CountZeroBytes_V256ExtractMsb(in key);
                tokens += kz + (32 - kz) * mul;
            }
        }
        return tokens;
    }

    private static long CalcTokens_V128Sum(Transaction tx)
    {
        AccessList? accessList = tx.AccessList;
        if (accessList is null) return 0L;
        long tokens = 0;
        long mul = Spec.GasCosts.TxDataNonZeroMultiplier;
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            int az = CountZerosAddress_Sum(address.Bytes);
            tokens += az + (20 - az) * mul;
            foreach (UInt256 key in storageKeys)
            {
                int kz = CountZeroBytes_V128Sum(in key);
                tokens += kz + (32 - kz) * mul;
            }
        }
        return tokens;
    }

    private static long CalcTokens_SpanCountZeros(Transaction tx)
    {
        AccessList? accessList = tx.AccessList;
        if (accessList is null) return 0L;
        long tokens = 0;
        long mul = Spec.GasCosts.TxDataNonZeroMultiplier;
        Span<byte> keyBytes = stackalloc byte[32];
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            ReadOnlySpan<byte> addrBytes = address.Bytes;
            int az = addrBytes.CountZeros();
            tokens += az + (20 - az) * mul;
            foreach (UInt256 key in storageKeys)
            {
                key.ToBigEndian(keyBytes);
                int kz = ((ReadOnlySpan<byte>)keyBytes).CountZeros();
                tokens += kz + (32 - kz) * mul;
            }
        }
        return tokens;
    }

    private static long CalcTokens_V128ExtractMsb(Transaction tx)
    {
        AccessList? accessList = tx.AccessList;
        if (accessList is null) return 0L;
        long tokens = 0;
        long mul = Spec.GasCosts.TxDataNonZeroMultiplier;
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            int az = CountZerosAddress(address.Bytes);
            tokens += az + (20 - az) * mul;
            foreach (UInt256 key in storageKeys)
            {
                int kz = CountZeroBytes_V128ExtractMsb(in key);
                tokens += kz + (32 - kz) * mul;
            }
        }
        return tokens;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Zero-byte counting primitives
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Address is 20 bytes: one full Vector128 (16 bytes) + 4 remaining bytes scalar.
    /// Uses ExtractMsb which is efficient at native 128-bit width on both ARM and x86.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZerosAddress(ReadOnlySpan<byte> addr)
    {
        ref byte b = ref MemoryMarshal.GetReference(addr);
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref b);
        int zeros = BitOperations.PopCount(
            Vector128.ExtractMostSignificantBits(Vector128.Equals(v, default)));
        // Remaining 4 bytes scalar
        for (int i = 16; i < 20; i++)
            if (Unsafe.Add(ref b, i) == 0) zeros++;
        return zeros;
    }

    /// <summary>
    /// Address via Sum instead of ExtractMsb, to compare on ARM.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZerosAddress_Sum(ReadOnlySpan<byte> addr)
    {
        ref byte b = ref MemoryMarshal.GetReference(addr);
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref b);
        int zeros = Vector128.Sum(~Vector128.Equals(v, default) + Vector128<byte>.One);
        for (int i = 16; i < 20; i++)
            if (Unsafe.Add(ref b, i) == 0) zeros++;
        return zeros;
    }

    /// <summary>
    /// UInt256 → Vector256 reinterpret, Sum approach.
    /// On ARM decomposes to 2×Vector128 Sum. Benchmarked as fastest for UInt256 on ARM.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytes_V256Sum(in UInt256 value)
    {
        Vector256<byte> data = Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in value));
        return Vector256.Sum(~Vector256.Equals(data, default) + Vector256<byte>.One);
    }

    /// <summary>
    /// UInt256 → Vector256 reinterpret, ExtractMsb + PopCount.
    /// Expected fastest on x86 (vpmovmskb = 1 instruction), slower on ARM.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytes_V256ExtractMsb(in UInt256 value)
    {
        Vector256<byte> data = Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in value));
        return BitOperations.PopCount(
            Vector256.ExtractMostSignificantBits(Vector256.Equals(data, default)));
    }

    /// <summary>
    /// UInt256 as 2×Vector128 with Sum. Stays at native 128-bit width on ARM.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytes_V128Sum(in UInt256 value)
    {
        ref byte b = ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in value));
        Vector128<byte> lo = Unsafe.ReadUnaligned<Vector128<byte>>(ref b);
        Vector128<byte> hi = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, 16));
        return Vector128.Sum(~Vector128.Equals(lo, default) + Vector128<byte>.One)
             + Vector128.Sum(~Vector128.Equals(hi, default) + Vector128<byte>.One);
    }

    /// <summary>
    /// UInt256 as 2×Vector128 with ExtractMsb + PopCount.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountZeroBytes_V128ExtractMsb(in UInt256 value)
    {
        ref byte b = ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in value));
        Vector128<byte> lo = Unsafe.ReadUnaligned<Vector128<byte>>(ref b);
        Vector128<byte> hi = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, 16));
        return BitOperations.PopCount(
                Vector128.ExtractMostSignificantBits(Vector128.Equals(lo, default)))
             + BitOperations.PopCount(
                Vector128.ExtractMostSignificantBits(Vector128.Equals(hi, default)));
    }

    // ═══════════════════════════════════════════════════════════════════════

    private static Transaction BuildTransaction(int addresses, int keysPerAddress)
    {
        AccessList.Builder builder = new();
        for (int i = 0; i < addresses; i++)
        {
            byte[] bytes = new byte[20];
            bytes[^1] = (byte)(i & 0xFF);
            bytes[^2] = (byte)((i >> 8) & 0xFF);
            bytes[10] = 0xAB;
            builder.AddAddress(new Address(bytes));
            for (int j = 0; j < keysPerAddress; j++)
            {
                builder.AddStorage(new UInt256((ulong)j * 0x0001000200030004UL));
            }
        }
        return new Transaction { AccessList = builder.Build() };
    }
}
