using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

/// <summary>
/// This service maintains the state of current invoices and raises appropriate events
/// </summary>
public class BoltzExchangerService : IDisposable
{
    private readonly BoltzWebSocketService _webSocketService;
    private readonly CovClaimDaemon _covClaimDaemon;
    private readonly EventAggregator _eventAggregator;
    public EventAggregator EventAggregator => _eventAggregator;
    private readonly ILogger<BoltzLightningClient> _logger;

    public BoltzExchangerService(
        BoltzWebSocketService webSocketService,
        CovClaimDaemon covClaimDaemon,
        EventAggregator eventAggregator,
        ILogger<BoltzLightningClient> logger)
    {
        _webSocketService = webSocketService;
        _covClaimDaemon = covClaimDaemon;
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
        public SwapStatusUpdate? LastStatusUpdate { get; set; }
        public bool IsPaid { get; set; } // Flag indicating if listener was notified
    }

    public async Task InvoiceCreatedThroughSwap(LightningInvoice invoice, CreateReverseSwapResponse swapResponse, byte[] preimage, string preimageHashHex, 
        string requestClaimPublicKeyHex, Uri websocketUri, CancellationToken cancellationToken = default)
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
            ClaimPublicKey = requestClaimPublicKeyHex,
            RefundPublicKey = swapResponse.RefundPublicKey
        }, cancellationToken);

        _swapData.TryAdd(swapResponse.Id, new SwapData
        {
            Id = swapResponse.Id,
            Preimage = preimage,
            PreimageHash = preimageHashHex,
            OriginalInvoice = invoice,
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
