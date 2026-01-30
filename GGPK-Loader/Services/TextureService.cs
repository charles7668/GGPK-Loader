using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GGPK_Loader.Utils;

namespace GGPK_Loader.Services;

public interface ITextureService
{
    bool IsDdsFile(string fileName);
    bool IsDdsHeaderFile(string fileName);
    Task<Bitmap?> ResolveDdsDataAsync(byte[] data);
}

public class TextureService(IMessageService messageService) : ITextureService
{
    public bool IsDdsFile(string fileName)
    {
        return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsDdsHeaderFile(string fileName)
    {
        return fileName.EndsWith(".dds.header", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Bitmap?> ResolveDdsDataAsync(byte[] data)
    {
        var scratchImage = new DirectXTex.ScratchImage();
        var decompressedScratch = new DirectXTex.ScratchImage();

        try
        {
            var sourceData = GetDdsDataSpan(data);

            var hr = DirectXTex.GetMetadataFromDDSMemory(sourceData, 0, out var metadata);
            if (hr != 0)
            {
                await messageService.ShowErrorMessageAsync("Failed to resolve DDS data");
                return null;
            }

            hr = DirectXTex.LoadFromDDSMemory(sourceData, 0, ref metadata, ref scratchImage);
            if (hr != 0)
            {
                await messageService.ShowErrorMessageAsync("Failed to resolve DDS data");
                return null;
            }

            if (scratchImage.nimages == 0)
            {
                return null;
            }

            var image = Marshal.PtrToStructure<DirectXTex.Image>(scratchImage.image);
            if (IsCompressedFormat(image.format, out var targetFormat))
            {
                hr = DirectXTex.Decompress(ref image, targetFormat, ref decompressedScratch);
                if (hr < 0)
                {
                    await messageService.ShowErrorMessageAsync($"Decompression failed with HRESULT {hr:X}");
                    return null;
                }

                image = Marshal.PtrToStructure<DirectXTex.Image>(decompressedScratch.image);
            }

            if (image.format == targetFormat)
            {
                return CreateBitmapFromImage(image);
            }

            await messageService.ShowErrorMessageAsync(
                $"Format {image.format} not supported for direct display demo yet (Target: {targetFormat}).");
            return null;

        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync(ex.Message);
        }
        finally
        {
            SafeRelease(ref decompressedScratch);
            SafeRelease(ref scratchImage);
        }

        return null;
    }

    private static ReadOnlySpan<byte> GetDdsDataSpan(byte[] data)
    {
        if (data.Length > 28 &&
            data[28] == 0x44 && data[29] == 0x44 && data[30] == 0x53 && data[31] == 0x20)
        {
            return new ReadOnlySpan<byte>(data)[28..];
        }

        return new ReadOnlySpan<byte>(data);
    }

    private static Bitmap CreateBitmapFromImage(DirectXTex.Image image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize((int)image.width, (int)image.height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var buffer = bitmap.Lock();
        var dest = buffer.Address;
        var src = image.pixels;
        var height = (int)image.height;
        var rowPitch = (int)image.rowPitch;

        for (var i = 0; i < height; i++)
        {
            unsafe
            {
                Buffer.MemoryCopy((void*)(src + i * rowPitch), (void*)(dest + i * rowPitch),
                    rowPitch, rowPitch);
            }
        }

        return bitmap;
    }

    private static void SafeRelease(ref DirectXTex.ScratchImage image)
    {
        try
        {
            DirectXTex.Release(ref image);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsCompressedFormat(DirectXTex.DXGI_FORMAT format, out DirectXTex.DXGI_FORMAT targetFormat)
    {
        switch (format)
        {
            case DirectXTex.DXGI_FORMAT.BC1_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC1_UNORM:
            case DirectXTex.DXGI_FORMAT.BC1_UNORM_SRGB:
            case DirectXTex.DXGI_FORMAT.BC2_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC2_UNORM:
            case DirectXTex.DXGI_FORMAT.BC2_UNORM_SRGB:
            case DirectXTex.DXGI_FORMAT.BC3_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC3_UNORM:
            case DirectXTex.DXGI_FORMAT.BC3_UNORM_SRGB:
            case DirectXTex.DXGI_FORMAT.BC4_SNORM:
            case DirectXTex.DXGI_FORMAT.BC4_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC4_UNORM:
            case DirectXTex.DXGI_FORMAT.BC5_SNORM:
            case DirectXTex.DXGI_FORMAT.BC5_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC5_UNORM:
            case DirectXTex.DXGI_FORMAT.BC7_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC7_UNORM:
            case DirectXTex.DXGI_FORMAT.BC7_UNORM_SRGB:
                targetFormat = DirectXTex.DXGI_FORMAT.R8G8B8A8_UNORM;
                return true;
            default:
                targetFormat = format;
                return false;
        }
    }
}