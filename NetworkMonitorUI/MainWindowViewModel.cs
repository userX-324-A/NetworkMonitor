using NetworkMonitor.Shared.DTOs; // Updated using for DTOs
using NetworkMonitorUI.Commands; // RelayCommand
using NetworkMonitorUI.Services; // Add this using
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
// Remove HttpClient usings
// using System.Net.Http;
// using System.Net.Http.Json;
// using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Data; // For CollectionViewSource
using System.Windows.Input; // For ICommand
using System.Windows.Threading; // For DispatcherTimer

namespace NetworkMonitorUI.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        // --- Fields ---
        // Remove HttpClient instance
        // private static readonly HttpClient sharedClient ...

        private readonly IApiService _apiService; // Inject the service
        private readonly DispatcherTimer _uiUpdateTimer;
        private readonly DispatcherTimer _retryTimer;
        private bool _isServiceAvailable = false; // Track service availability
        private string _statusText = "Initializing...";
        private bool _isBusy = false; // Indicate ongoing operations

        // --- Properties for Data Binding ---
        public ObservableCollection<ProcessStatsDto> ProcessStats { get; } = new();
        public ObservableCollection<ProcessDiskStatsDto> DiskStats { get; } = new();
        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }
        private bool _isLoading = false;
        public bool IsLoading // Optional: For showing a loading indicator
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        // --- Commands ---
        public ICommand InitializeCommand { get; }
        public ICommand ResetTotalsCommand { get; }
        public ICommand RefreshCommand { get; }

        // --- Constructor ---
        public MainWindowViewModel(IApiService apiService) // Accept IApiService
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));

            // Initialize Commands
            InitializeCommand = new RelayCommand(async _ => await InitializeAsync());
            ResetTotalsCommand = new RelayCommand(async _ => await ResetTotalsAsync(), _ => CanResetTotals());
            RefreshCommand = new RelayCommand(async _ => await TriggerUpdatesAsync(), _ => CanRefresh());

            // Initialize Timers
            _uiUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2) // Update interval
            };
            _uiUpdateTimer.Tick += async (s, e) => await UiUpdateTimer_TickAsync();

            _retryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30) // Retry interval
            };
            _retryTimer.Tick += async (s, e) => await RetryTimer_TickAsync();

            // Enable Collection Synchronization
            BindingOperations.EnableCollectionSynchronization(ProcessStats, new object());
            BindingOperations.EnableCollectionSynchronization(DiskStats, new object());

            // Start initialization instead of old LoadDataAsync
            _ = InitializeAsync();
        }

        // --- Command Methods ---
        private async Task InitializeAsync()
        {
            await AttemptConnectionAsync();
        }

        private async Task ResetTotalsAsync()
        {
            if (!CanResetTotals()) return;

            IsBusy = true;
            StatusText = "Resetting totals...";
            bool success = false;
            try
            {
                // Call the service method
                success = await _apiService.ResetTotalsAsync();

                if (success)
                {
                    // Clear local collections immediately
                    ProcessStats.Clear();
                    DiskStats.Clear();
                    StatusText = "Totals reset successfully.";
                }
                else
                {
                    StatusText = "Error resetting totals: The API request failed.";
                }
            }
            catch (Exception ex) // Catch potential exceptions from service interaction if not handled there
            {
                StatusText = $"Error resetting totals: {ex.Message}";
                Debug.WriteLine($"Error resetting totals: {ex}");
            }
            finally
            {
                IsBusy = false;
                ((RelayCommand)ResetTotalsCommand).RaiseCanExecuteChanged();
            }
        }

        private bool CanResetTotals()
        {
            return _isServiceAvailable && !IsBusy;
        }

        // Add CanRefresh predicate for RefreshCommand
        private bool CanRefresh()
        {
            return _isServiceAvailable && !IsBusy;
        }

        // Add TriggerUpdatesAsync method for RefreshCommand
        private async Task TriggerUpdatesAsync()
        {
            if (!CanRefresh()) return;

            IsBusy = true; // Optionally indicate busy state during manual refresh
            StatusText = "Refreshing data...";
            try
            {
                 await Task.WhenAll(UpdateStatsAsync(), UpdateDiskStatsAsync());
                 StatusText = "Data refreshed.";
            }
            catch(Exception ex)
            {
                // Handle potential errors during manual refresh
                HandleConnectionFailure($"Error during manual refresh: {ex.Message}");
                Debug.WriteLine($"Error during manual refresh: {ex}");
            }
            finally
            {
                IsBusy = false;
                // Update CanExecute for relevant commands if needed (e.g., Refresh itself)
                ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged(); 
                 ((RelayCommand)ResetTotalsCommand).RaiseCanExecuteChanged(); 
            }
        }

        // --- Timer Tick Handlers ---
        private async Task UiUpdateTimer_TickAsync()
        {
            if (_isServiceAvailable)
            {
                await Task.WhenAll(UpdateStatsAsync(), UpdateDiskStatsAsync());
            }
        }

        private async Task RetryTimer_TickAsync()
        {
            StatusText = "Attempting to reconnect...";
            await AttemptConnectionAsync();
        }

        // --- Data Fetching and Handling Logic ---
        private async Task AttemptConnectionAsync()
        {
            IsBusy = true;
            StatusText = "Checking service availability...";
            try
            {
                // Call the service method
                _isServiceAvailable = await _apiService.CheckServiceAvailabilityAsync();

                if (_isServiceAvailable)
                {
                    StatusText = "Connected. Fetching initial data...";
                    _retryTimer.Stop();

                    await Task.WhenAll(UpdateStatsAsync(), UpdateDiskStatsAsync());

                    _uiUpdateTimer.Start();
                    StatusText = "Connected.";
                }
                else
                {
                    HandleConnectionFailure("Service not reachable");
                }
            }
            catch (Exception ex) // Catch potential exceptions from service interaction
            {
                HandleConnectionFailure($"Unexpected error during connection check: {ex.Message}");
                Debug.WriteLine($"Error checking connection: {ex}");
            }
            finally
            {
                IsBusy = false;
                ((RelayCommand)ResetTotalsCommand).RaiseCanExecuteChanged();
            }
        }

        private void HandleConnectionFailure(string reason)
        {
            _isServiceAvailable = false;
            _uiUpdateTimer.Stop();
            StatusText = $"Disconnected: {reason}. Retrying in 30s...";
            if (!_retryTimer.IsEnabled)
            {
                _retryTimer.Start();
            }
             ((RelayCommand)ResetTotalsCommand).RaiseCanExecuteChanged();
        }

        private async Task UpdateStatsAsync()
        {
            if (!_isServiceAvailable) return;

            AllProcessStatsDto? allStats = null;
            try
            {
                 // Call the service method
                allStats = await _apiService.GetNetworkStatsAsync();
            }
            catch (Exception ex) // Catch potential exceptions from service interaction
            {
                 HandleConnectionFailure($"Error fetching network stats: {ex.Message}");
                 Debug.WriteLine($"Error fetching network stats: {ex}");
                 return; // Exit if fetch fails
            }

            if (allStats?.Stats != null)
            {
                UpdateObservableCollection(ProcessStats, allStats.Stats, s => s.ProcessId,
                    (existing, received) =>
                    {
                        existing.ProcessName = received.ProcessName;
                        existing.TotalBytesSent = received.TotalBytesSent;
                        existing.TotalBytesReceived = received.TotalBytesReceived;
                    });
                if (_isServiceAvailable) StatusText = "Connected.";
            }
            else
            {
                if (_isServiceAvailable) StatusText = "Connected (Received no network process data).";
                Debug.WriteLine("Received null or empty network stats from ApiService.");
            }
        }

        private async Task UpdateDiskStatsAsync()
        {
            if (!_isServiceAvailable) return;

            AllProcessDiskStatsDto? allDiskStats = null;
            try
            {
                allDiskStats = await _apiService.GetDiskStatsAsync();
            }
            catch (Exception ex)
            {
                HandleConnectionFailure($"Error fetching disk stats: {ex.Message}");
                Debug.WriteLine($"Error fetching disk stats: {ex}");
                return; // Exit if fetch fails
            }

            if (allDiskStats?.Stats != null)
            {
                UpdateObservableCollection(DiskStats, allDiskStats.Stats, s => s.ProcessId,
                    (existing, received) =>
                    {
                         existing.ProcessName = received.ProcessName;
                         existing.TotalBytesRead = received.TotalBytesRead;
                         existing.TotalBytesWritten = received.TotalBytesWritten;
                    });
                 if (_isServiceAvailable) StatusText = "Connected."; 
            }
            else
            {
                 if (_isServiceAvailable) StatusText = "Connected (Received no disk process data).";
                 Debug.WriteLine("Received null or empty disk stats from ApiService.");
            }
        }

        // --- Helper Methods ---

        /// <summary>
        /// Efficiently updates an ObservableCollection based on a new list of items.
        /// Assumes EnableCollectionSynchronization has been called on the collection.
        /// </summary>
        private void UpdateObservableCollection<T, TKey>(
            ObservableCollection<T> collection,
            IEnumerable<T> newItems,
            Func<T, TKey> keySelector,
            Action<T, T> updateAction)
            where TKey : notnull
        {
            // Existing implementation remains the same
            var newItemsDict = newItems.ToDictionary(keySelector);
            var currentKeys = collection.Select(keySelector).ToHashSet();
            var receivedKeys = newItemsDict.Keys.ToHashSet();

            var keysToRemove = currentKeys.Except(receivedKeys).ToList();
            var itemsToRemove = collection.Where(item => keysToRemove.Contains(keySelector(item))).ToList();
            foreach (var itemToRemove in itemsToRemove)
            {
                collection.Remove(itemToRemove);
            }

            foreach (var receivedItem in newItemsDict.Values)
            {
                var existingItem = collection.FirstOrDefault(p => keySelector(p).Equals(keySelector(receivedItem)));
                if (existingItem != null)
                {
                    updateAction(existingItem, receivedItem);
                }
                else
                {
                    collection.Add(receivedItem);
                }
            }
        }

        // --- IDisposable Implementation ---
        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _uiUpdateTimer?.Stop();
                    _retryTimer?.Stop();
                    // No HttpClient to dispose here
                }
                _disposed = true;
                Debug.WriteLine("MainWindowViewModel disposed.");
            }
        }

        // Optional: Add method to stop timer when window closes
        public void StopTimer()
        {
            _uiUpdateTimer.Stop();
            _retryTimer.Stop();
        }
    }
}
