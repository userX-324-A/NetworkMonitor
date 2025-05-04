using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NetworkMonitor.Shared.DTOs; // Corrected using statement

namespace NetworkMonitorUI.Services
{
    /// <summary>
    /// Implementation of IApiService using HttpClient.
    /// </summary>
    public class ApiService : IApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiBaseUrl;

        // Store options statically for performance
        private static readonly JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Inject IConfiguration along with HttpClient
        public ApiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            
            // Read BaseUrl from configuration
            _apiBaseUrl = configuration.GetValue<string>("ApiService:BaseUrl") 
                          ?? throw new InvalidOperationException("API Base URL not found in configuration (ApiService:BaseUrl).");
            
            // Validate the URL (optional but recommended)
            if (!Uri.TryCreate(_apiBaseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Invalid API Base URL format in configuration: {_apiBaseUrl}");
            }

            // BaseAddress could be set here if preferred, or per-request
            // _httpClient.BaseAddress = new Uri(_apiBaseUrl);
        }

        public async Task<AllProcessStatsDto?> GetNetworkStatsAsync()
        {
            string requestUri = $"{_apiBaseUrl}/stats";
            try
            {
                var response = await _httpClient.GetFromJsonAsync<AllProcessStatsDto>(requestUri, _jsonSerializerOptions);
                // Ensure ProcessStats list is not null if response itself is not null
                if (response != null && response.Stats == null)
                {
                     response.Stats = new List<ProcessStatsDto>();
                }
                return response;
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[ApiService] HTTP request error fetching process stats from {requestUri}: {ex.Message} (Status: {ex.StatusCode})");
                return null; // Indicate failure
            }
            catch (JsonException ex)
            {
                // Includes cases where the response body is not valid JSON
                Debug.WriteLine($"[ApiService] JSON deserialization error fetching process stats from {requestUri}: {ex.Message}");
                return null; // Indicate failure
            }
            catch (TaskCanceledException ex) // Handle timeouts specifically if needed
            {
                Debug.WriteLine($"[ApiService] Request timed out fetching process stats from {requestUri}: {ex.Message}");
                 return null;
            }
            catch (Exception ex) // Catch any other unexpected errors
            {
                Debug.WriteLine($"[ApiService] An unexpected error occurred fetching process stats from {requestUri}: {ex.ToString()}");
                // Log the full exception details for unexpected issues
                return null; // Indicate failure
            }
        }

        public async Task<AllProcessDiskStatsDto?> GetDiskStatsAsync()
        {
            string requestUri = $"{_apiBaseUrl}/stats/disk";
            try
            {
                var response = await _httpClient.GetFromJsonAsync<AllProcessDiskStatsDto>(requestUri, _jsonSerializerOptions);
                 // Ensure ProcessDiskStats list is not null if response itself is not null
                if (response != null && response.Stats == null)
                {
                     response.Stats = new List<ProcessDiskStatsDto>();
                }
                return response;
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[ApiService] HTTP request error fetching disk stats from {requestUri}: {ex.Message} (Status: {ex.StatusCode})");
                return null; // Indicate failure
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[ApiService] JSON deserialization error fetching disk stats from {requestUri}: {ex.Message}");
                return null; // Indicate failure
            }
             catch (TaskCanceledException ex) // Handle timeouts
            {
                Debug.WriteLine($"[ApiService] Request timed out fetching disk stats from {requestUri}: {ex.Message}");
                 return null;
            }
            catch (Exception ex) // Catch any other unexpected errors
            {
                Debug.WriteLine($"[ApiService] An unexpected error occurred fetching disk stats from {requestUri}: {ex.ToString()}");
                return null; // Indicate failure
            }
        }

        public Task<bool> CheckServiceAvailabilityAsync()
        {
            // Placeholder: Implement actual API call to check service health
            // For now, assume service is available or return a default based on expected behavior
            Debug.WriteLine("[ApiService] CheckServiceAvailabilityAsync called (placeholder).");
            return Task.FromResult(true); // Or false, depending on default assumption
        }

        public async Task<bool> ResetTotalsAsync()
        {
            string requestUri = $"{_apiBaseUrl}/reset-totals";
            Debug.WriteLine($"[ApiService] Attempting to reset totals via POST to {requestUri}.");
            try
            {
                // Send a POST request (or appropriate verb) to the reset endpoint
                // Assuming the endpoint requires no body for reset. Adjust if needed.
                var response = await _httpClient.PostAsync(requestUri, null); // Use PostAsJsonAsync or similar if a body is needed

                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("[ApiService] Reset totals request successful.");
                    return true;
                }
                else
                {
                    Debug.WriteLine($"[ApiService] Reset totals request failed with status code: {response.StatusCode}.");
                    // Optionally read response body for more details if available and useful
                    // var errorContent = await response.Content.ReadAsStringAsync();
                    // Debug.WriteLine($"[ApiService] Error content: {errorContent}");
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[ApiService] HTTP request error during reset totals: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException ex) // Handle timeouts
            {
                Debug.WriteLine($"[ApiService] Reset totals request timed out: {ex.Message}");
                return false;
            }
            catch (Exception ex) // Catch any other unexpected errors
            {
                Debug.WriteLine($"[ApiService] An unexpected error occurred during reset totals: {ex.ToString()}");
                return false;
            }
        }
    }
} 