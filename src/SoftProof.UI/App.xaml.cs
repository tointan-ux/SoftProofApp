using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoftProof.UI.Services;
using SoftProof.UI.ViewModels;
using SoftProof.UI.Views;
using SoftProof.Core.Interfaces;
using SoftProof.ColorManagement;
using SoftProof.RIPParsers;
using SoftProof.RIPParsers.Raster;
using SoftProof.RIPParsers.Raster.Pyramid;
using SoftProof.Tools;
using SoftProof.Viewer;

namespace SoftProof.UI;

/// <summary>
/// Application entry point with dependency injection
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    internal IHost? AppHost => _host;
    
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsLog.Error("DispatcherUnhandledException", args.Exception);
            WriteErrorLog("runtime-error.log", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsLog.Error("AppDomain.UnhandledException",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsLog.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        DiagnosticsLog.Info($"startup {Environment.OSVersion.VersionString}");

        // async void: an unhandled exception here would tear the process down with no
        // explanation, so report startup failures (e.g. DI misconfiguration) explicitly.
        try
        {
            _host = CreateHostBuilder().Build();
            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // A file argument makes the app scriptable (and testable) without the Open dialog.
            var startFile = e.Args.FirstOrDefault(a => !a.StartsWith("-") && File.Exists(a));
            if (startFile is not null)
                _ = mainWindow.OpenJobFileAsync(startFile);
        }
        catch (Exception ex)
        {
            var logPath = WriteErrorLog("startup-error.log", ex);
            var root = ex.GetBaseException();

            MessageBox.Show(
                $"SoftProof failed to start:\n\n{ex.Message}" +
                (ReferenceEquals(root, ex) ? "" : $"\n\nCause: {root.Message}") +
                (logPath is null ? "" : $"\n\nDetails: {logPath}"),
                "SoftProof",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    
    /// <summary>
    /// A WinExe has no console, so write the full exception (including inner exceptions
    /// and the XAML line number for XamlParseException) to %AppData%\SoftProof\.
    /// </summary>
    private static string? WriteErrorLog(string fileName, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftProof");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null; // logging must never mask the original error
        }
    }
    
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
    
    private static IHostBuilder CreateHostBuilder()
        {
            return Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Core services
                    services.AddSingleton<IColorEngine, LittleCmsEngine>();
                    services.AddSingleton<IFileDialogService, FileDialogService>();
                
                    // Parsers - registered as IRipParser so the registry receives them via
                    // IEnumerable<IRipParser>. Order matters where several parsers claim the
                    // same extension (.tif/.tiff): the registry picks the first match.
                    services.AddSingleton<IRipParser, HarlequinPageBufferParser>();
                    services.AddSingleton<IRipParser, OneBitTiffSeparationParser>();
                    services.AddSingleton<IRipParser, MultiChannelTiffParser>();
                    services.AddSingleton<IRipParser, LenFileParser>();
                
                    // The registry is the router, not a parser itself: register it as its own
                    // concrete type ONLY. Registering it as IRipParser too would make it a
                    // dependency of itself (circular dependency at startup).
                    services.AddSingleton<RipParserRegistry>();

                    // Raster pipeline: decodes parsed jobs into PlateLayers (TIFF strips, PBF plates).
                    services.AddSingleton<PyramidCache>();
                    services.AddSingleton<IPlateLoader>(sp =>
                        new PlateLoader(sp.GetRequiredService<PyramidCache>()));
                
                    // Tools
                    services.AddSingleton<InverseDotGainTool>();
                    services.AddSingleton<DensitometerTool>();
                    services.AddSingleton<ColorMeasurementTool>();
                
                    // Viewer
                
                    // ViewModels
                    services.AddSingleton<MainViewModel>();
                
                    // Views
                    services.AddSingleton<MainWindow>();
                });
        }
}

/// <summary>
/// ViewModel locator for design-time support
/// </summary>
public class ViewModelLocator
{
    public MainViewModel Main => (Application.Current as App)?.AppHost?.Services.GetRequiredService<MainViewModel>() 
        ?? new MainViewModel(null!, null!, null!, null!, null!, null!, null!, null!);
}