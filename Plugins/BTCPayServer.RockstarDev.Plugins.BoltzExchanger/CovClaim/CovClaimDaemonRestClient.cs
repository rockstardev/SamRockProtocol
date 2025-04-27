using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;

public class CovClaimDaemonRestClient
{
    private readonly ILogger<CovClaimDaemonRestClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiUrl;

    public CovClaimDaemonRestClient(ILogger<CovClaimDaemonRestClient> logger, IHttpClientFactory httpClientFactory, string apiUrl)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _apiUrl = apiUrl;
    }

    public async Task<bool> RegisterCovenant(CovClaimRegisterRequest requestBody, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Registering covenant {SwapId} with API: {ApiUrl}", requestBody.ClaimPublicKey, _apiUrl);

        try
        {
            var client = _httpClientFactory.CreateClient("CovClaimDaemonClient");
            // Consider adding timeout to the client or request
            // client.Timeout = TimeSpan.FromSeconds(30);

            // Log the request body being sent
            var jsonRequest = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogDebug("Sending covenant registration request to {ApiUrl}:\n{JsonRequest}", _apiUrl, jsonRequest);

            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
            {
                Content = JsonContent.Create(requestBody, mediaType: new MediaTypeHeaderValue("application/json"), options: null) // Default serializer options
            };
            // Add authentication headers if the API requires it
            // request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "your_api_token");

            using var response = await client.SendAsync(request, cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Received response from {ApiUrl}: Status {StatusCode}, Body:\n{ResponseBody}", _apiUrl, response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully registered covenant {SwapId} via API.", requestBody.ClaimPublicKey);
                return true;
            }
            else
            {
                _logger.LogError("Failed to register covenant {SwapId}. API returned status {StatusCode}. Response: {ResponseBody}",
                    requestBody.ClaimPublicKey, response.StatusCode, responseBody);
                // Consider parsing error response for more details if the API provides structured errors
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed when registering covenant {SwapId} at {ApiUrl}.", requestBody.ClaimPublicKey, _apiUrl);
            return false;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Request timed out when registering covenant {SwapId} at {ApiUrl}.", requestBody.ClaimPublicKey, _apiUrl);
            return false; // Handle timeout specifically
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Request cancelled when registering covenant {SwapId} at {ApiUrl}.", requestBody.ClaimPublicKey, _apiUrl);
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred when registering covenant {SwapId} at {ApiUrl}.", requestBody.ClaimPublicKey, _apiUrl);
            return false;
        }
    }
}

// Define request/response models for the CovClaim REST API
public class CovClaimRegisterRequest
{
    [JsonPropertyName("claimPublicKey")]
    public string ClaimPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("refundPublicKey")]
    public string RefundPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("preimage")]
    public string Preimage { get; set; } = string.Empty; // Assuming preimage is sent as hex string

    [JsonPropertyName("blindingKey")]
    public string BlindingKey { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("tree")]
    public SwapTree? Tree { get; set; } // Use the specific SwapTree type
}
