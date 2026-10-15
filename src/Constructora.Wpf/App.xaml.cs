using System.IO;
using System.Windows;
using Constructora.Client.Core;
using Constructora.Wpf.Services;
using Constructora.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Constructora.Wpf;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var configuredUrl = Environment.GetEnvironmentVariable("CONSTRUCTORA_API_URL")
                                ?? "http://127.0.0.1:5080/";
            if (!Uri.TryCreate(configuredUrl.TrimEnd('/') + "/", UriKind.Absolute, out var apiUri)
                || apiUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("CONSTRUCTORA_API_URL debe ser una URL HTTP o HTTPS válida.");

            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Constructora", "Certificaciones");
            Directory.CreateDirectory(dataDirectory);
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information)
                .AddProvider(new ArchivoLoggerProvider(Path.Combine(dataDirectory, "cliente.log"))));
            services.AddConstructoraClient(apiUri, Path.Combine(dataDirectory, "certificaciones.db"));
            services.AddSingleton(provider => new SincronizacionEnSegundoPlano(
                provider.GetRequiredService<ISincronizadorService>(), apiUri,
                provider.GetRequiredService<ILogger<SincronizacionEnSegundoPlano>>()));
            services.AddSingleton<CertificacionViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider(new ServiceProviderOptions
                { ValidateScopes = true, ValidateOnBuild = true });
            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            await _services.GetRequiredService<CertificacionViewModel>().InicializarAsync();
        }
        catch (Exception exception)
        {
            _services?.GetService<ILogger<App>>()?.LogCritical(exception, "No se pudo iniciar el cliente.");
            MessageBox.Show($"No se pudo iniciar la aplicación: {exception.Message}", "Constructora",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
