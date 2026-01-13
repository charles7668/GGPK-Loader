using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GGPK_Loader.ViewModels;
using GGPK_Loader.Views;
using GGPK_Loader.Services;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using GGPK_Loader.Utils;
using System.Diagnostics;
using System.Reflection;
using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
// using Avalonia; // Removed redundant using

namespace GGPK_Loader
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // try
            // {
            //     var ddsData = File.ReadAllBytes(@"c:\Users\charles\Downloads\atlas.dds");
            //     var result = DirectXTex.GetMetadataFromDDSMemory(ddsData, 0, out var metadata);
            //     Debug.WriteLine($"Metadata Width: {metadata.width}, Height: {metadata.height}, Format: {metadata.format}");
            //
            //     var scratchImage = new DirectXTex.ScratchImage();
            //     var loadResult = DirectXTex.LoadFromDDSMemory(ddsData, 0, ref metadata, ref scratchImage);
            //     Debug.WriteLine($"Load Result: {loadResult}, Images: {scratchImage.nimages}, Size: {scratchImage.size}");
            //
            //     if (loadResult == 0 && scratchImage.nimages > 0)
            //     {
            //         var image = Marshal.PtrToStructure<DirectXTex.Image>(scratchImage.image);
            //         Debug.WriteLine(
            //             $"Image Width: {image.width}, Height: {image.height}, Format: {image.format}, RowPitch: {image.rowPitch}");
            //
            //         var targetFormat = DirectXTex.DXGI_FORMAT.B8G8R8A8_UNORM;
            //         DirectXTex.ScratchImage decompressedScratch = new DirectXTex.ScratchImage();
            //
            //         // Simple check for BC formats (70-99 typically)
            //         bool isCompressed = (uint)image.format >= 70 && (uint)image.format <= 99;
            //
            //         if (isCompressed)
            //         {
            //             Debug.WriteLine($"Format {image.format} is compressed. Attempting to decompress to {targetFormat}...");
            //             var hr = DirectXTex.Decompress(ref image, targetFormat, ref decompressedScratch);
            //             if (hr >= 0)
            //             {
            //                 Debug.WriteLine("Decompression successful.");
            //                 image = Marshal.PtrToStructure<DirectXTex.Image>(decompressedScratch.image);
            //             }
            //             else
            //             {
            //                 Debug.WriteLine($"Decompression failed with HRESULT {hr:X}");
            //             }
            //             DirectXTex.Release(ref scratchImage);
            //         }
            //
            //         if (image.format == targetFormat)
            //         {
            //             var bitmap = new WriteableBitmap(
            //                 new PixelSize((int)image.width, (int)image.height),
            //                 new Vector(96, 96),
            //                 PixelFormat.Bgra8888,
            //                 AlphaFormat.Premul);
            //
            //             using (var buffer = bitmap.Lock())
            //             {
            //                 var dest = buffer.Address;
            //                 var src = image.pixels;
            //                 var height = (int)image.height;
            //                 var rowPitch = (int)image.rowPitch;
            //
            //                 // Copy line by line
            //                 for (var i = 0; i < height; i++)
            //                 {
            //                     unsafe
            //                     {
            //                         Buffer.MemoryCopy((void*)(src + i * rowPitch), (void*)(dest + i * rowPitch),
            //                             rowPitch, rowPitch);
            //                     }
            //                 }
            //             }
            //             bitmap.Save("test.png");
            //         }
            //         else
            //         {
            //             Debug.WriteLine($"Format {image.format} not supported for direct display demo yet (Target: {targetFormat}).");
            //         }
            //         DirectXTex.Release(ref decompressedScratch); 
            //         // Note: We are leaking memory here because we don't have FreeScratchImage bound.
            //     }
            // }
            // catch (Exception ex)
            // {
            //     Debug.WriteLine($"Error processing DDS: {ex}");
            // }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                var collection = new ServiceCollection();
                collection.AddSingleton<MainWindow>();
                collection.AddSingleton<IFileService>(sp => new FileService(sp.GetRequiredService<MainWindow>()));
                collection.AddSingleton<IMessageService>(sp => new MessageService(sp.GetRequiredService<MainWindow>()));
                collection.AddSingleton<IGgpkParsingService, GgpkParsingService>();
                collection.AddSingleton<MainWindowViewModel>();

                var services = collection.BuildServiceProvider();

                var mainWindow = services.GetRequiredService<MainWindow>();
                mainWindow.DataContext = services.GetRequiredService<MainWindowViewModel>();
                desktop.MainWindow = mainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }
    }
}