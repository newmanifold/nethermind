// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

using Word = Vector256<byte>;

public static class UInt256Extensions
{
    public const int ByteSize = 32;

    // value?.IsZero == false <=> x > 0
    public static bool IsPositive(this UInt256? @this) => @this?.IsZero == false;

    [SkipLocalsInit]
    public static ValueHash256 ToValueHash(this in UInt256 value)
    {
        Unsafe.SkipInit(out Word result);
        if (Avx2.IsSupported)
        {
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word data = Unsafe.As<UInt256, Word>(ref Unsafe.AsRef(in value));
                result = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
            }
            else
            {
                Vector256<ulong> permute = Unsafe.As<UInt256, Vector256<ulong>>(ref Unsafe.AsRef(in value));
                Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
                result = Avx2.Shuffle(Unsafe.As<Vector256<ulong>, Word>(ref convert), shuffle);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(value.u3);
                u2 = BinaryPrimitives.ReverseEndianness(value.u2);
                u1 = BinaryPrimitives.ReverseEndianness(value.u1);
                u0 = BinaryPrimitives.ReverseEndianness(value.u0);
            }
            else
            {
                u3 = value.u3;
                u2 = value.u2;
                u1 = value.u1;
                u0 = value.u0;
            }

            result = Vector256.Create(u3, u2, u1, u0).AsByte();
        }

        return Unsafe.As<Word, ValueHash256>(ref result);
    }

    /// <summary>
    /// Returns the number of zero-valued bytes in the 256-bit value.
    /// Uses platform-optimal SIMD: ExtractMostSignificantBits on x86 (native vpmovmskb),
    /// Vector128 Sum on ARM (efficient horizontal add at native 128-bit width),
    /// and 4×SWAR as scalar fallback on platforms without SIMD.
    /// No serialization needed — operates directly on the in-memory layout.
    /// </summary>
    public static int CountZeroBytes(this in UInt256 value)
    {
        // x86 with AVX2: single-pass 256-bit compare + vpmovmskb + popcnt
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> data = Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in value));
            return BitOperations.PopCount(
                Vector256.ExtractMostSignificantBits(Vector256.Equals(data, default)));
        }

        // ARM with NEON: 2×Vector128 compare + horizontal sum at native 128-bit width
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte b = ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in value));
            Vector128<byte> lo = Unsafe.ReadUnaligned<Vector128<byte>>(ref b);
            Vector128<byte> hi = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, 16));
            return Vector128.Sum(~Vector128.Equals(lo, default) + Vector128<byte>.One)
                   + Vector128.Sum(~Vector128.Equals(hi, default) + Vector128<byte>.One);
        }

        // Scalar fallback: 4×SWAR bitmask trick on the ulong limbs
        return value.u0.CountZeroBytes() + value.u1.CountZeroBytes()
                                         + value.u2.CountZeroBytes() + value.u3.CountZeroBytes();
    }

    public static int CountLeadingZeros(this in UInt256 uInt256)
    {
        // Scan from the highest limb down to the lowest
        for (int i = 3; i >= 0; i--)
        {
            ulong limb = uInt256[i];
            if (limb != 0)
            {
                return (3 - i) * 64 + BitOperations.LeadingZeroCount(limb);
            }
        }

        // All four limbs were zero
        return 256;
    }
}
