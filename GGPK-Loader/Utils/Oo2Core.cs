using System;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace GGPK_Loader.Utils;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public static class Oo2Core
{
    [DllImport("oo2core_8_win64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long OodleLZ_Decompress(
        byte[] srcBuf, // Compressed data buffer
        long srcLen, // Length of the compressed data
        byte[] dstBuf, // Buffer to store the decompressed data
        long dstLen, // Expected decompression length (obtained from Bundle Header)
        int fuzz, // Fuzz safety check (set to 0 or 1)
        int crc, // CRC check (set to 0)
        int verbose, // Logging verbosity (set to 0)
        IntPtr dstBase, // Base address for the dictionary (set to IntPtr.Zero)
        IntPtr e, // Extra parameters (set to IntPtr.Zero)
        IntPtr cb, // Callback function (set to IntPtr.Zero)
        IntPtr cbCtx, // Callback context (set to IntPtr.Zero)
        IntPtr scratch, // Scratch memory space (set to IntPtr.Zero)
        long scratchSize, // Size of the scratch space (set to 0)
        int threadKind // Threading model (set to 3 for synchronous/standard)
    );

    [DllImport("oo2core_8_win64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "OodleLZ_Decompress")]
    private static extern long OodleLZ_Decompress_Span(
        ref byte srcBuf,
        long srcLen,
        ref byte dstBuf,
        long dstLen,
        int fuzz,
        int crc,
        int verbose,
        IntPtr dstBase,
        IntPtr e,
        IntPtr cb,
        IntPtr cbCtx,
        IntPtr scratch,
        long scratchSize,
        int threadKind
    );

    public static long Decompress(byte[] src, long srcLen, byte[] dst, long dstLen)
    {
        return OodleLZ_Decompress(
            src,
            srcLen,
            dst,
            dstLen,
            1,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            3);
    }

    public static long Decompress(ReadOnlySpan<byte> src, long srcLen, Span<byte> dst, long dstLen)
    {
        return OodleLZ_Decompress_Span(
            ref MemoryMarshal.GetReference(src),
            srcLen,
            ref MemoryMarshal.GetReference(dst),
            dstLen,
            1,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            3);
    }
}