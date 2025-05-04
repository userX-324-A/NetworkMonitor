using System;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using NetworkMonitorUI.Services;
using NetworkMonitorUI.ViewModels;

namespace NetworkMonitorUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder() // Use default builder
            .ConfigureAppConfiguration((context, config) => // Add configuration setup
            {
                // Clear default providers if necessary (e.g., environment variables, command line)
                // config.Sources.Clear(); 

                // Add appsettings.json
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

                // Add environment-specific settings (optional)
                // config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);

                // Add other configuration sources if needed (e.g., user secrets, environment variables)
                // config.AddEnvironmentVariables(); 
            })
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(context.Configuration, services); // Pass configuration to ConfigureServices
            })
            .Build();
    }

    private void ConfigureServices(IConfiguration configuration, IServiceCollection services)
    {
        // Register HttpClient as a singleton
        // Typically, HttpClient is registered as singleton for performance/socket reuse
        services.AddSingleton<HttpClient>();

        // Register ApiService as the implementation for IApiService
        // Transient might be suitable if it holds no state, Singleton if it does
        // Let's start with Transient
        services.AddTransient<IApiService, ApiService>(); 

        // Register ViewModel
        // Transient: New instance for each request (typically for windows/views)
        services.AddTransient<MainWindowViewModel>();

        // Register MainWindow
        // Transient: Each time it's requested, a new window instance is created
        services.AddTransient<MainWindow>(); 

        // You could also register configuration sections as options here if needed
        // services.Configure<ApiSettings>(configuration.GetSection("ApiService"));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Resolve the MainWindow from the DI container
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Stop the host and dispose services
        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5)); // Allow timeout for graceful shutdown
        }
        
        base.OnExit(e);
    }
}

