using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GGPK_Loader.ViewModels;
using GGPK_Loader.Views;
using GGPK_Loader.Services;
using Microsoft.Extensions.DependencyInjection;

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