using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;

public class CovClaimDaemonRestClient
{
    private readonly ILogger<CovClaimDaemonRestClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public CovClaimDaemonRestClient(ILogger<CovClaimDaemonRestClient> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> RegisterCovenant(string apiUrl, CovClaimRegisterRequest requestBody, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Registering covenant {SwapId} with API: {ApiUrl}", requestBody.SwapId, apiUrl);

        try
        {
            var client = _httpClientFactory.CreateClient("CovClaimDaemonClient");
            // Consider adding timeout to the client or request
            // client.Timeout = TimeSpan.FromSeconds(30);

            // Log the request body being sent
            var jsonRequest = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogDebug("Sending covenant registration request to {ApiUrl}:\n{JsonRequest}", apiUrl, jsonRequest);

            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = JsonContent.Create(requestBody, mediaType: new MediaTypeHeaderValue("application/json"), options: null) // Default serializer options
            };
            // Add authentication headers if the API requires it
            // request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "your_api_token");

            using var response = await client.SendAsync(request, cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Received response from {ApiUrl}: Status {StatusCode}, Body:\n{ResponseBody}", apiUrl, response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully registered covenant {SwapId} via API.", requestBody.SwapId);
                return true;
            }
            else
            {
                _logger.LogError("Failed to register covenant {SwapId}. API returned status {StatusCode}. Response: {ResponseBody}",
                    requestBody.SwapId, response.StatusCode, responseBody);
                // Consider parsing error response for more details if the API provides structured errors
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed when registering covenant {SwapId} at {ApiUrl}.", requestBody.SwapId, apiUrl);
            return false;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Request timed out when registering covenant {SwapId} at {ApiUrl}.", requestBody.SwapId, apiUrl);
            return false; // Handle timeout specifically
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Request cancelled when registering covenant {SwapId} at {ApiUrl}.", requestBody.SwapId, apiUrl);
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred when registering covenant {SwapId} at {ApiUrl}.", requestBody.SwapId, apiUrl);
            return false;
        }
    }
}

// Define request/response models for the CovClaim REST API
public class CovClaimRegisterRequest
{
    // Match the JSON field names expected by the covclaim API exactly
    [System.Text.Json.Serialization.JsonPropertyName("swap_id")]
    public string SwapId { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("pubkey")]
    public string Pubkey { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("blinding_key")]
    public string BlindingKey { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("covenant_address")]
    public string CovenantAddress { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("funding_txid")]
    public string FundingTxid { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("funding_vout")]
    public uint FundingVout { get; set; }
}
