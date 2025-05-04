using NetworkMonitorService; // For DTOs - Assuming this namespace is correct for the DTOs
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http; // Added for HttpClient
using System.Net.Http.Json; // Added for JSON extensions
using System.Text.Json; // Added for JsonException
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls; // Added for Button
using System.Windows.Threading; // For DispatcherTimer
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Timers;
using System.Windows.Data; // Added for CollectionViewSource

namespace NetworkMonitorUI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // Shared HttpClient instance
    private static readonly HttpClient sharedClient = new()
    {
        // Configure the base address of your service API
        // Ensure this matches where your NetworkMonitorService API is running
        // Common defaults are 5000 (HTTP) or 5001 (HTTPS) when run directly
        BaseAddress = new Uri("http://localhost:5000"), // <-- ADJUST PORT IF NEEDED
    };

    private readonly DispatcherTimer _uiUpdateTimer;
    private bool _isServiceAvailable = false; // Track service availability
    private DispatcherTimer _retryTimer; // Timer for reconnection attempts

    // ObservableCollection to update the UI automatically
    public ObservableCollection<ProcessStatsDto> ProcessStats { get; } = new();
    // Add collection for Disk Stats
    public ObservableCollection<ProcessDiskStatsDto> DiskStats { get; } = new();

    private readonly ObservableCollection<ProcessStatsDto> _processStats = new();
    // Add a dictionary to keep track of processes seen in the current interval
    private readonly HashSet<int> _activeProcessesInInterval = new(); // Use HashSet for simpler tracking

    // --- ViewModelBase Implementation ---

    public MainWindow()
    {
        InitializeComponent();
        // Remove NetworkServiceClient initialization
        DataContext = this; // Set DataContext for binding ProcessStats

        // Timer to refresh data from the service
        _uiUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2) // Update interval (e.g., every 2 seconds)
        };
        _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

        // Timer for reconnection attempts
        _retryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30) // Retry interval
        };
        _retryTimer.Tick += RetryTimer_Tick;

        // Add handler for Window Closing
        this.Closing += Window_Closing;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await AttemptConnectionAsync();
    }

    private async Task AttemptConnectionAsync()
    {
        StatusTextBlock.Text = "Checking service availability...";
        try
        {
            // Make an initial request to check if the service API is reachable
            using var response = await sharedClient.GetAsync("/stats", HttpCompletionOption.ResponseHeadersRead); // Use a lightweight endpoint if available
            if (response.IsSuccessStatusCode)
            {
                _isServiceAvailable = true;
                StatusTextBlock.Text = "Connected. Fetching initial data...";
                await UpdateStatsAsync(); // Initial data load
                await UpdateDiskStatsAsync(); // Initial disk data load
                _retryTimer.Stop(); // Stop retrying if we were
                _uiUpdateTimer.Start(); // Start regular updates
                StatusTextBlock.Text = "Connected."; // Set final status after initial load
            }
            else
            {
                HandleConnectionFailure($"Service API returned status: {response.StatusCode}");
                MessageBox.Show($"Failed to connect to the Network Monitor service API at {sharedClient.BaseAddress}. Status: {response.StatusCode}. Will retry every 30 seconds.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (HttpRequestException httpEx)
        {
             HandleConnectionFailure($"Connection error: {httpEx.Message}");
             Debug.WriteLine($"Connection error: {httpEx}");
             // Only show MessageBox on initial load failure, not on retries triggered by timer
             if (!_retryTimer.IsEnabled) // Avoid showing this on every retry tick failure
             {
                 MessageBox.Show($"Failed to connect to the Network Monitor service API at {sharedClient.BaseAddress}: {httpEx.Message}" +
                                   "\nPlease ensure the service is running. Will retry every 30 seconds.",
                                   "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
             }
        }
        catch (Exception ex) // Catch other potential exceptions during initial connection
        {
             HandleConnectionFailure($"Unexpected error: {ex.Message}");
             Debug.WriteLine($"Unexpected error on initial connect/retry: {ex}");
             MessageBox.Show($"An unexpected error occurred while trying to connect: {ex.Message}. Will retry every 30 seconds.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HandleConnectionFailure(string reason)
    {
        _isServiceAvailable = false;
        _uiUpdateTimer.Stop(); // Stop normal updates
        StatusTextBlock.Text = $"Disconnected: {reason}. Retrying in 30s...";
        if (!_retryTimer.IsEnabled)
        {
            _retryTimer.Start(); // Start retry attempts
        }
    }

    private async void RetryTimer_Tick(object? sender, EventArgs e)
    {
        StatusTextBlock.Text = "Attempting to reconnect...";
        await AttemptConnectionAsync(); // Reuse the connection attempt logic
    }

    private async void UiUpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_isServiceAvailable) // Only update if service was initially available
        {
            await UpdateStatsAsync();
            await UpdateDiskStatsAsync(); // Update disk stats too
        }
        // Optional: Could add logic here to periodically re-check service availability if !_isServiceAvailable
    }

    private async Task UpdateStatsAsync()
    {
        if (!_isServiceAvailable) return; // Don't try if service isn't available

        try
        {
            // Fetch stats using HttpClient
            var allStats = await sharedClient.GetFromJsonAsync<AllProcessStatsDto>("/stats");

            if (allStats != null && allStats.Stats != null)
            {
                 Dispatcher.Invoke(() => // Ensure UI updates happen on the UI thread
                 {
                     // --- Preserve Sort Order --- 
                     var view = CollectionViewSource.GetDefaultView(NetworkStatsGrid.ItemsSource);
                     var sortDescriptions = new List<SortDescription>(view.SortDescriptions);
                     // --- End Preserve Sort Order ---

                     var currentPids = ProcessStats.Select(p => p.ProcessId).ToHashSet();
                     var receivedPids = allStats.Stats.Select(p => p.ProcessId).ToHashSet();

                     // Remove items no longer present
                     var pidsToRemove = currentPids.Except(receivedPids).ToList();
                     foreach (var pid in pidsToRemove)
                     {
                         var itemToRemove = ProcessStats.FirstOrDefault(p => p.ProcessId == pid);
                         if (itemToRemove != null)
                         {
                             ProcessStats.Remove(itemToRemove);
                         }
                     }

                     // Update existing items and add new ones
                     foreach (var receivedStat in allStats.Stats)
                     {
                         var existingStat = ProcessStats.FirstOrDefault(p => p.ProcessId == receivedStat.ProcessId);
                         if (existingStat != null)
                         {
                             // Update properties only if they changed to minimize UI churn
                             if (existingStat.ProcessName != receivedStat.ProcessName) existingStat.ProcessName = receivedStat.ProcessName;
                             if (existingStat.TotalBytesSent != receivedStat.TotalBytesSent) existingStat.TotalBytesSent = receivedStat.TotalBytesSent;
                             if (existingStat.TotalBytesReceived != receivedStat.TotalBytesReceived) existingStat.TotalBytesReceived = receivedStat.TotalBytesReceived;
                         }
                         else
                         {
                             ProcessStats.Add(receivedStat); // Add new item
                         }
                     }

                     // --- Re-apply Sort Order --- 
                     if (sortDescriptions.Any())
                     {
                         view.SortDescriptions.Clear();
                         foreach (var sd in sortDescriptions)
                         {
                             view.SortDescriptions.Add(sd);
                         }
                     }
                     // --- End Re-apply Sort Order ---

                     StatusTextBlock.Text = "Connected."; // Update status after successful refresh
                 });
            }
            else
            {
                // Handle null/empty stats case - might indicate an issue, but not necessarily disconnection
                 Dispatcher.Invoke(() => StatusTextBlock.Text = "Connected (Received no process data).");
                 Debug.WriteLine("Received null or empty stats DTO from API.");
            }
        }
        catch (HttpRequestException httpEx)
        {
            // Handle HTTP errors (network issue, service down)
            HandleConnectionFailure(httpEx.Message); // Use the common handler
            Debug.WriteLine($"Error fetching stats via HTTP: {httpEx}");
            // Maybe show a less intrusive notification instead of a MessageBox on timer ticks
        }
        catch (JsonException jsonEx)
        {
             // Handle malformed JSON response - indicates a server-side issue?
             Dispatcher.Invoke(() => StatusTextBlock.Text = "Error: Invalid data received from service.");
             Debug.WriteLine($"Failed to deserialize stats response: {jsonEx}");
             // Consider stopping timer or logging more details
        }
        catch (Exception ex)
        {
            // Handle other unexpected errors
            HandleConnectionFailure($"Error fetching stats: {ex.Message}"); // Use the common handler
            Debug.WriteLine($"Unexpected error fetching stats: {ex}");
        }
    }

    // --- Method to fetch and update Disk Stats ---
    private async Task UpdateDiskStatsAsync()
    {
        if (!_isServiceAvailable) return; // Don't try if service isn't available

        try
        {
            // Fetch disk stats using HttpClient
            var allDiskStats = await sharedClient.GetFromJsonAsync<AllProcessDiskStatsDto>("/stats/disk");

            if (allDiskStats != null && allDiskStats.Stats != null)
            {
                Dispatcher.Invoke(() => // Ensure UI updates happen on the UI thread
                {
                    // --- Preserve Sort Order --- 
                    var view = CollectionViewSource.GetDefaultView(DiskStatsGrid.ItemsSource);
                    var sortDescriptions = new List<SortDescription>(view.SortDescriptions);
                    // --- End Preserve Sort Order ---

                    var currentPids = DiskStats.Select(p => p.ProcessId).ToHashSet();
                    var receivedPids = allDiskStats.Stats.Select(p => p.ProcessId).ToHashSet();

                    // Remove items no longer present
                    var pidsToRemove = currentPids.Except(receivedPids).ToList();
                    foreach (var pid in pidsToRemove)
                    {
                        var itemToRemove = DiskStats.FirstOrDefault(p => p.ProcessId == pid);
                        if (itemToRemove != null)
                        {
                            DiskStats.Remove(itemToRemove);
                        }
                    }

                    // Update existing items and add new ones
                    foreach (var receivedStat in allDiskStats.Stats)
                    {
                        var existingStat = DiskStats.FirstOrDefault(p => p.ProcessId == receivedStat.ProcessId);
                        if (existingStat != null)
                        {
                            // Update properties only if they changed to minimize UI churn
                            if (existingStat.ProcessName != receivedStat.ProcessName) existingStat.ProcessName = receivedStat.ProcessName;
                            if (existingStat.TotalBytesRead != receivedStat.TotalBytesRead) existingStat.TotalBytesRead = receivedStat.TotalBytesRead;
                            if (existingStat.TotalBytesWritten != receivedStat.TotalBytesWritten) existingStat.TotalBytesWritten = receivedStat.TotalBytesWritten;
                        }
                        else
                        {
                            DiskStats.Add(receivedStat); // Add new item
                        }
                    }

                    // --- Re-apply Sort Order --- 
                    if (sortDescriptions.Any())
                    {
                        view.SortDescriptions.Clear();
                        foreach (var sd in sortDescriptions)
                        {
                            view.SortDescriptions.Add(sd);
                        }
                    }
                    // --- End Re-apply Sort Order ---

                });
            }
            else
            {
                // Handle null/empty stats case
                 Dispatcher.Invoke(() => StatusTextBlock.Text = "Connected (Received no disk data).");
                 Debug.WriteLine("Received null or empty disk stats DTO from API.");
            }
        }
        catch (HttpRequestException httpEx)
        {
            // Handle HTTP errors (network issue, service down)
            HandleConnectionFailure(httpEx.Message); // Use the common handler
            Debug.WriteLine($"Error fetching disk stats via HTTP: {httpEx}");
        }
        catch (JsonException jsonEx)
        {
             Dispatcher.Invoke(() => StatusTextBlock.Text = "Error: Invalid disk data received.");
             Debug.WriteLine($"Failed to deserialize disk stats response: {jsonEx}");
        }
        catch (Exception ex)
        {
             HandleConnectionFailure($"Error fetching disk stats: {ex.Message}"); // Use the common handler
             Debug.WriteLine($"Unexpected error fetching disk stats: {ex}");
        }
    }

    // --- Button Click Handlers ---

    // Removed filtering methods ApplyFilter_Click, ClearFilter_Click, ApplyFilter

    // --- Helper Methods ---
    // Removed SendControlCommandAsync

    // --- Window Closing Event Handler ---
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Stop timers to release resources
        _uiUpdateTimer?.Stop();
        _retryTimer?.Stop();

        // Dispose HttpClient if it were managed here (but static sharedClient doesn't need disposal here)
        // sharedClient.Dispose(); // Not needed for static readonly client

        Debug.WriteLine("MainWindow closing. Timers stopped.");
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await sharedClient.PostAsync("/reset-totals", null);
            response.EnsureSuccessStatusCode(); // Throw exception if not successful

            // Clear the local collections on the UI thread
            Dispatcher.Invoke(() =>
            {
                ProcessStats.Clear();
                DiskStats.Clear();
                StatusTextBlock.Text = "Totals reset successfully. UI cleared.";
            });

            // No need to call FetchNetworkStats/FetchDiskStats immediately after reset
            // The timer will fetch new data shortly.
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error resetting totals: {ex.Message}";
        }
    }
}

// DTO Classes (assuming they are defined elsewhere, e.g., in NetworkMonitorService namespace or a shared library)
/*
public class ProcessStatsDto
// ... existing code ...
*/