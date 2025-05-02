using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;
using Microsoft.Extensions.Logging;
using NBitcoin;
using TwentyTwenty.Storage.Azure;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

/// <summary>
/// This service maintains the state of current invoices and raises appropriate events
/// </summary>
public class BoltzExchangerService : IDisposable
{
    private readonly BoltzWebSocketService _webSocketService;
    private readonly CovClaimDaemon _covClaimDaemon;
    private readonly Microsoft.Extensions.Options.IOptions<DataDirectories> _dataDirectories;
    private readonly EventAggregator _eventAggregator;
    public EventAggregator EventAggregator => _eventAggregator;
    private readonly ILogger<BoltzLightningClient> _logger;

    public BoltzExchangerService(
        BoltzWebSocketService webSocketService,
        CovClaimDaemon covClaimDaemon,
        Microsoft.Extensions.Options.IOptions<DataDirectories> dataDirectories,
        EventAggregator eventAggregator,
        ILogger<BoltzLightningClient> logger)
    {
        _webSocketService = webSocketService;
        _covClaimDaemon = covClaimDaemon;
        _dataDirectories = dataDirectories;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }
    
    public void Dispose()
    {
        _swapData.Clear();
        _preimageHashToSwapId.Clear();
    }
    
    // Internal cache for preimage -> swap ID mapping for lookups
    private readonly ConcurrentDictionary<string, string> _preimageHashToSwapId = new();
    private readonly ConcurrentDictionary<string, SwapData> _swapData = new(); // Key: Swap ID

    // Internal data structure for holding swap details
    public class SwapData
    {
        public required string Id { get; init; }
        public required byte[] Preimage { get; init; } // Store the preimage
        public required string PreimageHash { get; init; }
        public required LightningInvoice OriginalInvoice { get; init; } // Store the invoice BOLT11
        public required CreateReverseSwapResponse SwapResponse { get; init; } // Store the original swap response
        public SwapStatusUpdate? LastStatusUpdate { get; set; }
        public Key PrivateKey { get; set; }
        public bool IsPaid { get; set; } // Flag indicating if listener was notified
    }

    public async Task InvoiceCreatedThroughSwap(LightningInvoice invoice, CreateReverseSwapResponse swapResponse, byte[] preimage, string preimageHashHex, 
        Key claimPrivateKey, Uri websocketUri, CancellationToken cancellationToken = default)
    {
        // 5. Store Swap Data and Subscribe to Updates
        // https://docs.boltz.exchange/api/claim-covenants
        var restClient = _covClaimDaemon.CovClaimClient;
        var preimageHex = Convert.ToHexString(preimage).ToLowerInvariant(); // Convert preimage bytes to hex
        await restClient.RegisterCovenant(new CovClaimRegisterRequest
        {
            Address = swapResponse.LockupAddress, 
            Preimage = preimageHex, 
            Tree = swapResponse.SwapTree, // Pass the tree received from Boltz directly
            BlindingKey = swapResponse.BlindingKey, 
            ClaimPublicKey = claimPrivateKey.PubKey.ToHex(),
            RefundPublicKey = swapResponse.RefundPublicKey
        }, cancellationToken);

        _swapData.TryAdd(swapResponse.Id, new SwapData
        {
            Id = swapResponse.Id,
            Preimage = preimage,
            PreimageHash = preimageHashHex,
            OriginalInvoice = invoice,
            SwapResponse = swapResponse,
            PrivateKey = claimPrivateKey,
            LastStatusUpdate = null,
            IsPaid = false
        });
        _preimageHashToSwapId.TryAdd(preimageHashHex, swapResponse.Id);

        // Subscribe via WebSocket
        await _webSocketService.SubscribeToSwapStatusAsync(websocketUri, swapResponse.Id, HandleSwapUpdate, cancellationToken);
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
        else
        {
            _logger.LogWarning($"GetInvoice by ID: Swap {invoiceId} not found in cache.");
            return null;
        }
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new CancellationToken())
    { var preimageHashStr = paymentHash.ToString();
        if (_preimageHashToSwapId.TryGetValue(preimageHashStr, out var swapId))
        {
            if (_swapData.TryGetValue(swapId, out var swapDetails))
            {
                _logger.LogDebug($"GetInvoice by PaymentHash: Found swap {swapId} for hash {preimageHashStr} in cache.");
                // Return a Task containing the cached invoice
                return swapDetails.OriginalInvoice;
            }
            else
            {
                // This case implies inconsistent cache (mapping exists, but swap data doesn't). Log error.
                _logger.LogError($"GetInvoice by PaymentHash: Inconsistent cache! Found mapping {preimageHashStr} -> {swapId}, but swap data not found.");
                return null;
            }
        }
        else
        {
            _logger.LogWarning($"GetInvoice by PaymentHash: Swap with payment hash {preimageHashStr} not found in cache.");
            return null;
        }
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
                {
                    swap.OriginalInvoice.Preimage = Convert.ToHexString(swap.Preimage).ToLowerInvariant();
                }
                else
                {
                    _logger.LogWarning($"Preimage is null for paid swap {swap.Id} when preparing BoltzSwapPaidEvent.");
                }

                // Publish the event for the listener to pick up
                var paidEvent = new BoltzSwapPaidEvent(swap.Id);
                _eventAggregator.Publish(paidEvent);

                // Invoke the claimer to claim the swap
                string? transactionHex = await InvokeClaimerForPaidSwap(swap);
                
                if (!string.IsNullOrEmpty(transactionHex))
                {
                    _logger.LogInformation($"Successfully obtained transaction hex for swap {swap.Id}: {transactionHex}");
                    // You can store or use this transaction hex if needed
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
            
            // Build the process arguments - be careful with spaces in the arguments
            var args = new[]
            {
                "claim-reverse-swap",
                $"--swap-id", swap.Id,
                $"--private-key", swap.PrivateKey.ToHex(),
                $"--preimage", swap.Preimage.ToHex(),
                $"--swap-tree", "'"+ JsonSerializer.Serialize(swapResponse.SwapTree) +"'",
                $"--lockup-address", swapResponse.LockupAddress,
                $"--refund-public-key", swapResponse.RefundPublicKey,
                $"--address", swapResponse.LockupAddress, // Using lockup address as the destination
                $"--blinding-key", swapResponse.BlindingKey ?? string.Empty
            };
            
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "BoltzExchanger", "claimer.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            
            // Add each argument separately to handle spaces correctly
            foreach (var arg in args)
            {
                processStartInfo.ArgumentList.Add(arg);
            }
            
            _logger.LogInformation($"Invoking claimer for swap {swap.Id}");
            
            using var process = System.Diagnostics.Process.Start(processStartInfo);
            
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
        if (_swapData.TryGetValue(paidSwapId, out swapData))
        {
            return true;
        }
        else
        {
            swapData = null;
            return false;
        }
    }
}
