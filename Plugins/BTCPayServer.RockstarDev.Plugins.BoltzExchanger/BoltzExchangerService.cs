using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using TwentyTwenty.Storage.Azure;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

/// <summary>
///     This service maintains the state of current invoices and raises appropriate events
/// </summary>
public class BoltzExchangerService : IDisposable
{
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BoltzLightningClient> _logger;

    // Internal cache for preimage -> swap ID mapping for lookups
    private readonly ConcurrentDictionary<string, string> _preimageHashToSwapId = new();
    private readonly ConcurrentDictionary<string, SwapData> _swapData = new(); // Key: Swap ID
    private readonly BoltzWebSocketService _webSocketService;

    public BoltzExchangerService(
        BoltzWebSocketService webSocketService,
        IOptions<DataDirectories> dataDirectories,
        IHttpClientFactory httpClientFactory,
        EventAggregator eventAggregator,
        ILogger<BoltzLightningClient> logger)
    {
        _webSocketService = webSocketService;
        _dataDirectories = dataDirectories;
        _httpClientFactory = httpClientFactory;
        EventAggregator = eventAggregator;
        _logger = logger;
    }

    public EventAggregator EventAggregator { get; }

    public void Dispose()
    {
        _swapData.Clear();
        _preimageHashToSwapId.Clear();
    }

    public async Task InvoiceCreatedThroughSwap(LightningInvoice invoice, CreateReverseSwapRequest req, CreateReverseSwapResponse resp,
        byte[] preimage, string preimageHashHex,
        Key claimPrivateKey, Uri websocketUri, CancellationToken cancellationToken = default)
    {
        _swapData.TryAdd(resp.Id, new SwapData
        {
            Id = resp.Id,
            Preimage = preimage,
            PreimageHash = preimageHashHex,
            OriginalInvoice = invoice,
            SwapRequest = req,
            SwapResponse = resp,
            PrivateKey = claimPrivateKey,
            LastStatusUpdate = null,
            IsPaid = false
        });
        _preimageHashToSwapId.TryAdd(preimageHashHex, resp.Id);

        // Subscribe via WebSocket
        await _webSocketService.SubscribeToSwapStatusAsync(websocketUri, resp.Id, HandleSwapUpdate, cancellationToken);
    }

    public async Task CancelInvoice(Uri websocketUri, string invoiceId, CancellationToken cancellationToken)
    {
        // In Boltz context, 'invoiceId' often corresponds to the 'swapId'.
        // Cancelling means we stop listening for updates for this swap.
        var swapId = invoiceId;
        _logger.LogInformation($"Attempting to cancel listening for invoice/swap ID: {swapId}");

        // Also remove the preimage mapping if it exists
        // Find the preimage hash associated with this swapId
        string? preimageHashToRemove = null;
        foreach (var kvp in _preimageHashToSwapId)
            if (kvp.Value == swapId)
            {
                preimageHashToRemove = kvp.Key;
                break;
            }

        if (preimageHashToRemove != null && _preimageHashToSwapId.TryRemove(preimageHashToRemove, out _))
            _logger.LogDebug($"Removed preimage hash mapping for cancelled swap {swapId}.");

        // Unsubscribe from WebSocket updates for this swap ID
        await _webSocketService.UnsubscribeFromSwapStatusAsync(swapId, cancellationToken);
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation)
    {
        // In Boltz reverse swaps, the 'invoiceId' BTCPay uses corresponds to our 'swapId'.
        if (_swapData.TryGetValue(invoiceId, out var swapDetails))
        {
            _logger.LogDebug($"GetInvoice by ID: Found swap {invoiceId} in cache.");
            return swapDetails.OriginalInvoice;
        }

        _logger.LogWarning($"GetInvoice by ID: Swap {invoiceId} not found in cache.");
        return null;
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new())
    {
        var preimageHashStr = paymentHash.ToString();
        if (_preimageHashToSwapId.TryGetValue(preimageHashStr, out var swapId))
        {
            if (_swapData.TryGetValue(swapId, out var swapDetails))
            {
                _logger.LogDebug($"GetInvoice by PaymentHash: Found swap {swapId} for hash {preimageHashStr} in cache.");
                // Return a Task containing the cached invoice
                return swapDetails.OriginalInvoice;
            }

            // This case implies inconsistent cache (mapping exists, but swap data doesn't). Log error.
            _logger.LogError($"GetInvoice by PaymentHash: Inconsistent cache! Found mapping {preimageHashStr} -> {swapId}, but swap data not found.");
            return null;
        }

        _logger.LogWarning($"GetInvoice by PaymentHash: Swap with payment hash {preimageHashStr} not found in cache.");
        return null;
    }

    private async Task HandleSwapUpdate(SwapStatusUpdate update)
    {
        _logger.LogInformation($"Handling status update for swap {update.Id}: {update.Status}");
        if (_swapData.TryGetValue(update.Id, out var swap))
        {
            swap.LastStatusUpdate = update; // Update latest status

            // Check if the swap status indicates payment or completion
            // Adjust these statuses based on Boltz documentation for reverse swaps!
            var paidStatuses = new[] { "invoice.paid", "swap.claimed", "transaction.claimed", "transaction.confirmed" }; // Example statuses
            var isPaid = paidStatuses.Contains(update.Status?.ToLowerInvariant());

            if (isPaid && !swap.IsPaid)
            {
                _logger.LogInformation($"Swap {update.Id} detected as PAID (Status: {update.Status}). Publishing event.");
                swap.IsPaid = true;

                // Update the stored invoice status and add preimage
                swap.OriginalInvoice.Status = LightningInvoiceStatus.Paid;
                swap.OriginalInvoice.PaidAt = DateTimeOffset.UtcNow;

                // Ensure preimage is included in the invoice object before publishing
                if (swap.Preimage != null)
                    swap.OriginalInvoice.Preimage = Convert.ToHexString(swap.Preimage).ToLowerInvariant();
                else
                    _logger.LogWarning($"Preimage is null for paid swap {swap.Id} when preparing BoltzSwapPaidEvent.");

                // Publish the event for the listener to pick up
                var paidEvent = new BoltzSwapPaidEvent(swap.Id);
                EventAggregator.Publish(paidEvent);

                // Invoke the claimer to claim the swap
                var transactionHex = await InvokeClaimerForPaidSwap(swap);

                if (!string.IsNullOrEmpty(transactionHex))
                {
                    _logger.LogInformation($"Successfully obtained transaction hex for swap {swap.Id}: {transactionHex}");

                    // Broadcast the transaction to Boltz
                    var broadcastSuccess = await BroadcastTransactionToBoltz("L-BTC", transactionHex, swap.Id);

                    if (broadcastSuccess)
                        _logger.LogInformation($"Transaction for swap {swap.Id} successfully broadcast through Boltz API");
                    else
                        _logger.LogWarning($"Failed to broadcast transaction for swap {swap.Id} through Boltz API");
                }

                // Once paid, we can unsubscribe from updates for this swap
                // Use CancellationToken.None as this is a background task
                await _webSocketService.UnsubscribeFromSwapStatusAsync(swap.Id, CancellationToken.None);
            }
            else if (IsFailedStatus(update.Status))
            {
                _logger.LogWarning($"Swap {update.Id} failed (Status: {update.Status}). Notifying listener of failure.");
                swap.OriginalInvoice.Status = LightningInvoiceStatus.Expired; // Or a custom 'Failed' status if possible?
                swap.OriginalInvoice.AmountReceived = LightMoney.Zero; // Ensure AmountReceived is set on failure

                // Optional: Publish a failure event if listeners need to react to failures too.
                // Example: _eventAggregator.Publish(new BoltzSwapFailedEvent(swap.Id));

                // TODO: Decide if failed swaps should be removed from _swapData / _preimageHashToSwapId
                // or if the listener should handle this cleanup after WaitInvoice throws.

                // Unsubscribe on failure too
                await _webSocketService.UnsubscribeFromSwapStatusAsync(swap.Id, CancellationToken.None);
            }
        }
        else
        {
            _logger.LogWarning($"Received status update for unknown or removed swap ID: {update.Id}");
        }
    }

    private bool IsFailedStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return false;
        var lowerStatus = status.ToLowerInvariant();
        // Add known failure statuses from Boltz API documentation
        return lowerStatus.Contains("fail") || lowerStatus.Contains("refund") || lowerStatus == "invoice.expired";
    }

    private async Task<string?> InvokeClaimerForPaidSwap(SwapData swap)
    {
        try
        {
            var swapResponse = swap.SwapResponse;

            // Build full command string for the claimer
            // Escape quotes in the JSON swap tree
            var swapTreeJson = JsonSerializer.Serialize(swapResponse.SwapTree)
                .Replace("\"", "\\\""); // Replace " with \" for JSON escaping in command line

            var commandArgs = $"claim-reverse-swap " +
                              $"--swap-id {swap.Id} " +
                              $"--private-key {swap.PrivateKey.ToHex()} " +
                              $"--preimage {swap.Preimage.ToHex()} " +
                              $"--swap-tree \"{swapTreeJson}\" " +
                              $"--lockup-address {swapResponse.LockupAddress} " +
                              $"--refund-public-key {swapResponse.RefundPublicKey} " +
                              $"--address {swap.SwapRequest.Address} " +
                              $"--blinding-key {swapResponse.BlindingKey ?? string.Empty}";

            // Log the full command for debugging
            _logger.LogInformation($"Claimer command: claimer.exe {commandArgs}");

            var claimerPath = Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "BoltzExchanger", "claimer.exe");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = claimerPath,
                Arguments = commandArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _logger.LogInformation($"Invoking claimer for swap {swap.Id}");

            using var process = Process.Start(processStartInfo);

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var errorMessage = $"Claimer failed for swap {swap.Id} with exit code {process.ExitCode}: {error}";
                _logger.LogError(errorMessage);
            }

            _logger.LogInformation($"Successfully claimed swap {swap.Id}");

            // The claimer outputs the transaction hex on success
            // We trim to remove any whitespace and ensure we get just the hex
            return output?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error claiming swap {swap.Id}");
        }

        return null;
    }

    public bool TryGetPaidInvoice(string paidSwapId, out SwapData swapData)
    {
        if (_swapData.TryGetValue(paidSwapId, out swapData)) return true;

        swapData = null;
        return false;
    }

    /// <summary>
    ///     Broadcasts a transaction to the Boltz API
    /// </summary>
    /// <param name="currency">The currency of the transaction (e.g., "L-BTC")</param>
    /// <param name="transactionHex">The raw transaction hex string</param>
    /// <param name="swapId">The swap ID for logging</param>
    /// <returns>True if the broadcast was successful, false otherwise</returns>
    private async Task<bool> BroadcastTransactionToBoltz(string currency, string transactionHex, string swapId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();

            // The Boltz API endpoint for broadcasting transactions
            var endpoint = $"https://api.boltz.exchange/v2/chain/{currency}/transaction";

            // Create the request payload
            var requestContent = new StringContent(
                JsonSerializer.Serialize(new { hex = transactionHex }),
                Encoding.UTF8,
                "application/json");

            _logger.LogInformation($"Broadcasting transaction for swap {swapId} to Boltz API endpoint {endpoint}");

            // Send the POST request
            var response = await client.PostAsync(endpoint, requestContent);

            // Check if the request was successful
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Boltz API broadcast response: {responseContent}");
                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"Failed to broadcast transaction for swap {swapId}. Status: {response.StatusCode}, Response: {errorContent}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error broadcasting transaction for swap {swapId} to Boltz API");
            return false;
        }
    }

    // Internal data structure for holding swap details
    public class SwapData
    {
        public required string Id { get; init; }
        public required byte[] Preimage { get; init; } // Store the preimage
        public required string PreimageHash { get; init; }
        public required LightningInvoice OriginalInvoice { get; init; } // Store the invoice BOLT11
        public CreateReverseSwapRequest SwapRequest { get; set; }
        public required CreateReverseSwapResponse SwapResponse { get; init; } // Store the original swap response
        public SwapStatusUpdate? LastStatusUpdate { get; set; }
        public Key PrivateKey { get; set; }
        public bool IsPaid { get; set; } // Flag indicating if listener was notified
    }
}
