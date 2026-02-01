using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GGPK_Loader.Services;
using GGPK_Loader.ViewModels;
using GGPK_Loader.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GGPK_Loader;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            var collection = new ServiceCollection();
            collection.AddSingleton<MainWindow>();
            collection.AddSingleton<IFileDialogService>(sp =>
                new FileDialogService(sp.GetRequiredService<MainWindow>()));
            collection.AddSingleton<IMessageService>(sp => new MessageService(sp.GetRequiredService<MainWindow>()));
            collection.AddSingleton<IStreamManager, StreamManager>();
            collection.AddSingleton<IGgpkParsingService, GgpkParsingService>();
            collection.AddSingleton<IGgpkBundleService, GgpkBundleService>();
            collection.AddSingleton<ISchemaService, SchemaService>();
            collection.AddSingleton<ITextureService, TextureService>();
            collection.AddSingleton<IDatParsingService, DatParsingService>();
            collection.AddSingleton<MainWindowViewModel>();

            var services = collection.BuildServiceProvider();

            var schemaService = services.GetRequiredService<ISchemaService>();
            var schemaPath = Path.Combine(AppContext.BaseDirectory, "deps", "schema.min.json");
            schemaService.LoadSchema(schemaPath);

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