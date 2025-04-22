#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public partial class BoltzLightningClient : ILightningClient, IDisposable
{
    private readonly ConcurrentDictionary<string, BoltzInvoiceListener> _activeListeners = new(); // Key: Swap ID
    private readonly HttpClient _httpClient;
    private readonly BoltzOptions _options;

    // Internal cache for preimage -> swap ID mapping for lookups
    private readonly ConcurrentDictionary<string, string> _preimageHashToSwapId = new();
    private readonly ConcurrentDictionary<string, SwapData> _swapData = new(); // Key: Swap ID
    private readonly BoltzWebSocketService _webSocketService;

    public BoltzLightningClient(BoltzOptions options, HttpClient httpClient, BoltzWebSocketService webSocketService, ILogger<BoltzLightningClient> logger)
    {
        _options = options;
        _httpClient = httpClient;
        _webSocketService = webSocketService;
        Logger = logger;
        _httpClient.BaseAddress = options.ApiUrl;
    }

    public ILogger<BoltzLightningClient> Logger { get; }

    public void Dispose()
    {
        Logger.LogInformation("Disposing BoltzLightningClient.");
        // HttpClient is managed externally (HttpClientFactory)
        // WebSocketService is managed externally (Hosted Service)
        _swapData.Clear();
        _activeListeners.Clear();
        _preimageHashToSwapId.Clear();
    }

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = new())
    {
        return CreateInvoice(createInvoiceRequest.Amount, createInvoiceRequest.Description, createInvoiceRequest.Expiry, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        Logger.LogInformation($"CreateInvoice called: Amount={amount}, Description='{description}', Expiry={expiry}");
        if (!string.IsNullOrEmpty(description)) Logger.LogWarning("Boltz CreateInvoice does not use the 'description' field.");
        // if (expiry != TimeSpan.Zero && expiry != BoltzConstants.DefaultInvoiceExpiry) { // Assuming a default constant exists or comparing to a reasonable default
        //     _logger.LogWarning($"Boltz CreateInvoice does not use the custom 'expiry' field. Using default Boltz expiry. Requested: {expiry}");
        // }

        // Input validation (Basic)
        if (amount == null || amount <= LightMoney.Zero)
        {
            Logger.LogError("Invalid amount for CreateInvoice.");
            throw new Exception("Invalid amount for CreateInvoice.");
        }

        try
        {
            // 1. Generate Preimage and Hash
            var preimage = RandomNumberGenerator.GetBytes(32);
            var preimageHash = SHA256.HashData(preimage);
            var preimageHashHex = Convert.ToHexString(preimageHash).ToLowerInvariant();

            Logger.LogInformation($"Creating Boltz Reverse Swap for {amount.ToUnit(LightMoneyUnit.Satoshi)} sats (L-BTC -> BTC)");
            Logger.LogDebug($"Preimage Hash: {preimageHashHex}");

            // 2. Get Liquid Address and Claim Key (Using placeholder for now)
            var (claimAddress, claimKey) = GetLiquidDetailsForSwap();
            if (string.IsNullOrEmpty(claimAddress) || claimKey == null)
            {
                Logger.LogError("Failed to get Liquid claim details.");
                throw new Exception("Failed to obtain Liquid address or claim key for the swap.");
            }

            Logger.LogInformation($"Using Liquid Claim Address: {claimAddress}");

            // 3. Call Boltz API to create reverse swap
            var request = new CreateReverseSwapRequest
            {
                FromAsset = "BTC", // We receive BTC (Lightning)
                ToAsset = _options.SwapToAsset, // We send L-BTC (on-chain)
                InvoiceAmountSat = (long)amount.ToUnit(LightMoneyUnit.Satoshi),
                PreimageHash = preimageHashHex,
                ClaimPublicKey = claimKey.PubKey.ToHex() // Provide the public key for the claim script
                // Add Address for claim?
                // Description? req.Description / req.DescriptionHash
            };

            Logger.LogDebug("Sending CreateReverseSwap request to Boltz API.");
            // Explicitly call the extension method to resolve ambiguity
            var response = await HttpClientJsonExtensions.PostAsJsonAsync(_httpClient, "/v2/swap/reverse", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var swapResponse = await response.Content.ReadFromJsonAsync<CreateReverseSwapResponse>(cancellationToken);

            if (swapResponse == null || string.IsNullOrEmpty(swapResponse.Id) || string.IsNullOrEmpty(swapResponse.Invoice))
            {
                Logger.LogError("Boltz API returned invalid response for reverse swap creation.");
                throw new Exception("Failed to create reverse swap: Invalid response from Boltz API.");
            }

            Logger.LogInformation($"Boltz Reverse Swap created successfully. ID: {swapResponse.Id}, Invoice: {swapResponse.Invoice.Substring(0, 15)}...");

            // 4. Parse the returned Lightning Invoice
            var bolt11 = BOLT11PaymentRequest.Parse(swapResponse.Invoice,
                _options.IsTestnet ? Network.TestNet : Network.Main);
            var invoice = new LightningInvoice
            {
                Id = bolt11.PaymentHash.ToString(),
                BOLT11 = swapResponse.Invoice,
                Amount = bolt11.MinimumAmount,
                ExpiresAt = bolt11.ExpiryDate,
                Status = LightningInvoiceStatus.Unpaid,
                PaymentHash = bolt11.PaymentHash.ToString(), // Use PaymentHash from BOLT11 as ID
                Preimage = preimageHashHex // Store hash initially, actual preimage only on payment
            };

            // 5. Store Swap Data and Subscribe to Updates
            var newSwap = new SwapData
            {
                Id = swapResponse.Id,
                PreimageHash = preimageHashHex,
                Preimage = preimage, // Store the actual preimage
                ClaimKey = claimKey,
                ClaimAddress = claimAddress,
                OriginalInvoice = invoice, // Store our constructed invoice
                SwapResponse = swapResponse,
                IsPaid = false
            };

            _swapData.TryAdd(swapResponse.Id, newSwap);
            _preimageHashToSwapId.TryAdd(preimageHashHex, swapResponse.Id);

            // Subscribe via WebSocket
            await _webSocketService.SubscribeToSwapAsync(swapResponse.Id, _options.ApiUrl, HandleSwapUpdate, cancellationToken);

            return invoice;
        }
        catch (HttpRequestException httpEx)
        {
            Logger.LogError(httpEx, "HTTP error creating Boltz reverse swap.");
            throw new Exception($"Failed to communicate with Boltz API: {httpEx.Message}", httpEx);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating Boltz reverse swap invoice.");
            throw;
        }
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellationToken = default)
    {
        // In Boltz context, 'invoiceId' often corresponds to the 'swapId'.
        // Cancelling means we stop listening for updates for this swap.
        var swapId = invoiceId;
        Logger.LogInformation($"Attempting to cancel listening for invoice/swap ID: {swapId}");

        if (_activeListeners.TryRemove(swapId, out var listener))
        {
            Logger.LogInformation($"Removed active listener for swap {swapId} due to cancellation request.");
            listener.Dispose(); // Dispose the listener to clean up resources and stop waiting
            // Note: We don't necessarily tell the Boltz *server* to cancel, 
            // as swaps might be atomic or have specific timeout mechanisms.
            // We just stop tracking it on the client-side.
        }
        else
        {
            Logger.LogWarning($"Could not cancel invoice/swap {swapId}: No active listener found.");
            // It might have already completed or never been listened to.
        }

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
            Logger.LogDebug($"Removed preimage hash mapping for cancelled swap {swapId}.");

        // Unsubscribe from WebSocket updates for this swap ID
        await _webSocketService.UnsubscribeFromSwapAsync(swapId, cancellationToken);
    }

    public Task<ILightningInvoiceListener> Listen(string paymentHash, CancellationToken cancellationToken = default)
    {
        Logger.LogInformation($"Listen called for PaymentHash: {paymentHash}");

        // Look up the swap ID associated with this payment hash
        if (!_preimageHashToSwapId.TryGetValue(paymentHash.ToLowerInvariant(), out var swapId))
        {
            Logger.LogError($"Listen called for unknown paymentHash: {paymentHash}. Swap data may not exist yet or invoice creation failed.");
            // Throw an exception as we cannot listen for an untracked swap
            return Task.FromException<ILightningInvoiceListener>(
                new InvalidOperationException($"Cannot listen for paymentHash {paymentHash}: Corresponding swap not found."));
        }

        Logger.LogDebug($"Found swapId {swapId} for paymentHash {paymentHash}. Checking for existing listener.");

        // Check if a listener already exists for this swapId
        if (_activeListeners.TryGetValue(swapId, out var existingListener))
        {
            Logger.LogInformation($"Returning existing listener for swap {swapId}");
            return Task.FromResult<ILightningInvoiceListener>(existingListener);
        }

        // Create and register a new listener
        var listener = new BoltzInvoiceListener(this, swapId, CleanupListenerResources, Logger, cancellationToken);

        if (!_activeListeners.TryAdd(swapId, listener))
        {
            // This should theoretically not happen if the previous TryGetValue failed, but handle defensively
            Logger.LogWarning($"Failed to add new listener for swap {swapId}, but it wasn't found previously. Attempting lookup again.");
            if (_activeListeners.TryGetValue(swapId, out existingListener))
            {
                Logger.LogInformation($"Returning existing listener found on second attempt for swap {swapId}");
                listener.Dispose(); // Dispose the newly created one
                return Task.FromResult<ILightningInvoiceListener>(existingListener);
            }

            Logger.LogError($"Concurrency issue: Failed to add or retrieve listener for swap {swapId}.");
            listener.Dispose();
            return Task.FromException<ILightningInvoiceListener>(
                new InvalidOperationException($"Failed to register listener for swap {swapId} due to unexpected concurrency state."));
        }

        Logger.LogInformation($"Created and registered new listener for swap {swapId} (PaymentHash: {paymentHash})");
        return Task.FromResult<ILightningInvoiceListener>(listener);
    }

    // Called by the listener itself via cleanup delegate
    private void CleanupListenerResources(string swapId)
    {
        Logger.LogDebug($"Cleaning up resources for listener associated with swap {swapId}");
        if (_activeListeners.TryRemove(swapId, out var listener))
        {
            Logger.LogDebug($"Removed listener for swap {swapId} from active dictionary.");
            // Ensure listener's TaskCompletionSource is cancelled if not already done by Dispose
            try
            {
                if (!listener._tcs.Task.IsCompleted) listener._tcs.TrySetCanceled();
            }
            catch (ObjectDisposedException)
            {
                /* Ignore if already disposed */
            }
        }
        else
        {
            Logger.LogWarning($"Attempted to clean up resources for swap {swapId}, but listener was not found in active dictionary.");
        }
        // Note: Unsubscribing from WebSocket happens when swap status reaches a final state (paid/failed) or during CancelInvoice.
    }

    // Temporary placeholder - replace with actual wallet integration
    private (string address, Key claimKey) GetLiquidDetailsForSwap()
    {
        // WARNING: Hardcoded keys and address for testing ONLY. DO NOT USE IN PRODUCTION.

        // Use a deterministic but unique key for testing if possible, otherwise random
        // var privateKey = new Key(RandomNumberGenerator.GetBytes(32));
        var privateKey = new Key(Encoders.Hex.DecodeData("a_very_secret_and_persistent_hex_private_key_for_testing"));
        var pubKey = privateKey.PubKey;

        return ("VJL9yJCQBqw9XyMduQmDfaVXLG8QP2iW6j1MwatE1dAwPPMNFozLJvfG3TCvxTzF6sTMsh2Vbj6272ck", privateKey);
    }

    // Callback from WebSocketService
    private async Task HandleSwapUpdate(SwapStatusUpdate update)
    {
        Logger.LogInformation($"Handling status update for swap {update.Id}: {update.Status}");
        if (_swapData.TryGetValue(update.Id, out var swap))
        {
            swap.LastStatusUpdate = update; // Update latest status

            // Check if the swap status indicates payment or completion
            // Adjust these statuses based on Boltz documentation for reverse swaps!
            var paidStatuses = new[] { "invoice.paid", "swap.claimed", "transaction.claimed", "transaction.confirmed" }; // Example statuses
            var isPaid = paidStatuses.Contains(update.Status?.ToLowerInvariant());

            if (isPaid && !swap.IsPaid)
            {
                Logger.LogInformation($"Swap {update.Id} detected as PAID (Status: {update.Status}). Notifying listener.");
                swap.IsPaid = true;

                // Update the stored invoice status and add preimage
                swap.OriginalInvoice.Status = LightningInvoiceStatus.Paid;
                swap.OriginalInvoice.PaidAt = DateTimeOffset.UtcNow;
                swap.OriginalInvoice.Preimage = Convert.ToHexString(swap.Preimage).ToLowerInvariant(); // Reveal preimage

                // Find the listener and notify it
                if (_activeListeners.TryGetValue(swap.Id, out var listener))
                    listener.TriggerInvoicePaid(swap.OriginalInvoice);
                else
                    Logger.LogWarning($"Swap {swap.Id} paid, but no active listener found.");

                // Once paid, we can unsubscribe from updates for this swap
                // Use CancellationToken.None as this is a background task
                await _webSocketService.UnsubscribeFromSwapAsync(swap.Id, CancellationToken.None);

                // TODO: Trigger the Liquid claim transaction here!
                Logger.LogWarning($"TODO: Implement Liquid claim transaction for swap {swap.Id}");
            }
            else if (IsFailedStatus(update.Status))
            {
                Logger.LogWarning($"Swap {update.Id} failed (Status: {update.Status}). Notifying listener of failure.");
                swap.OriginalInvoice.Status = LightningInvoiceStatus.Expired; // Or a custom 'Failed' status if possible?
                swap.OriginalInvoice.AmountReceived = LightMoney.Zero; // Ensure AmountReceived is set on failure

                if (_activeListeners.TryGetValue(swap.Id, out var listener)) listener.TriggerInvoiceFailure(); // Signal failure to the listener

                // Unsubscribe on failure too
                await _webSocketService.UnsubscribeFromSwapAsync(swap.Id, CancellationToken.None);
                _swapData.TryRemove(swap.Id, out _); // Clean up failed swap data?
                _preimageHashToSwapId.TryRemove(swap.PreimageHash, out _);
            }
        }
        else
        {
            Logger.LogWarning($"Received status update for unknown or removed swap ID: {update.Id}");
        }
    }

    private bool IsFailedStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return false;
        var lowerStatus = status.ToLowerInvariant();
        // Add known failure statuses from Boltz API documentation
        return lowerStatus.Contains("fail") || lowerStatus.Contains("refund") || lowerStatus == "invoice.expired";
    }

    // Internal data structure for holding swap details
    private class SwapData
    {
        public required string Id { get; init; }
        public required string PreimageHash { get; init; }
        public required byte[] Preimage { get; init; }
        public required Key ClaimKey { get; init; }
        public required string ClaimAddress { get; init; } // L-BTC address
        public required LightningInvoice OriginalInvoice { get; set; } // Store the invoice BOLT11
        public CreateReverseSwapResponse? SwapResponse { get; set; }
        public SwapStatusUpdate? LastStatusUpdate { get; set; }
        public bool IsPaid { get; set; } // Flag indicating if listener was notified
    }
}

internal class BoltzInvoiceListener : ILightningInvoiceListener
{
    private readonly CancellationToken _cancellationToken;
    private readonly Action<string> _cleanupCallback;
    private readonly BoltzLightningClient _client;
    private readonly ILogger _logger;
    private readonly string _swapId; // Store swapId directly
    internal readonly TaskCompletionSource<LightningInvoice> _tcs = new();
    private bool _disposed; // Track disposal state

    public BoltzInvoiceListener(BoltzLightningClient client, string swapId, Action<string> cleanupCallback, ILogger logger, CancellationToken cancellationToken)
    {
        _client = client;
        _swapId = swapId;
        _cleanupCallback = cleanupCallback;
        _cancellationToken = cancellationToken;
        _logger = logger; // Use logger from parent client
        _cancellationToken.Register(() =>
        {
            _logger.LogDebug($"BoltzInvoiceListener cancellation token triggered for swap {_swapId}.");
            // Only cancel TCS if it's not already completed
            _tcs.TrySetCanceled(_cancellationToken);
            Dispose(); // Ensure cleanup on cancellation
        });
    }

    public Task<LightningInvoice> WaitInvoice(CancellationToken cancellation) // Use passed cancellation
    {
        if (_disposed)
        {
            _logger.LogWarning($"WaitInvoice called on disposed listener for swap {_swapId}.");
            return Task.FromCanceled<LightningInvoice>(new CancellationToken(true));
        }

        _logger.LogInformation($"Waiting for invoice payment notification for swap {_swapId}...");

        // Combine the external cancellation token with the listener's own token
        // Use CreateLinkedTokenSource for proper cancellation propagation
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellation);

        // Register action for the combined token
        var registration = linkedCts.Token.Register(() =>
        {
            // This will trigger if either the listener's lifetime token OR the WaitInvoice cancellation token fires
            _logger.LogWarning($"Cancellation detected during WaitInvoice for swap {_swapId}.");
            _tcs.TrySetCanceled(linkedCts.Token);
        });

        var waitTask = _tcs.Task;

        // Return a task that continues after waitTask completes (or is cancelled)
        return waitTask.ContinueWith(task =>
            {
                registration.Unregister(); // Clean up the linked token registration
                registration.Dispose();

                // Check the final status of the TCS task
                if (task.IsFaulted)
                {
                    _logger.LogError(task.Exception?.InnerException ?? task.Exception, $"Error waiting for invoice payment for swap {_swapId}.");
                    // Rethrow the inner exception if available for better context
                    throw task.Exception.InnerException ?? task.Exception;
                }

                if (task.IsCanceled)
                {
                    _logger.LogWarning($"WaitInvoice task cancelled for swap {_swapId}.");
                    // Throw OperationCanceledException with the token that caused cancellation
                    throw new OperationCanceledException($"WaitInvoice cancelled for swap {_swapId}.", linkedCts.Token);
                }

                // If completed successfully (TaskStatus.RanToCompletion)
                _logger.LogInformation($"Invoice payment received or final state reached for swap {_swapId}.");
                // No need to look up swapId here anymore
                return task.Result; // Return the paid invoice
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default); // Ensure sync execution and specify scheduler
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; // Prevent re-entry

        _logger.LogDebug($"Disposing BoltzInvoiceListener for swap {_swapId}.");
        // Ensure TCS is completed (cancelled if not already done)
        _tcs.TrySetCanceled(_cancellationToken.IsCancellationRequested ? _cancellationToken : new CancellationToken(true));

        // Call the cleanup callback provided by the client
        try
        {
            _cleanupCallback(_swapId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error during cleanup callback for swap {_swapId}.");
        }
    }

    // Method called by BoltzLightningClient when the corresponding invoice is paid
    public void TriggerInvoicePaid(LightningInvoice paidInvoice)
    {
        _logger.LogInformation($"TriggerInvoicePaid called for listener of swap {_swapId}.");
        _tcs.TrySetResult(paidInvoice);
    }

    public void TriggerInvoiceFailure()
    {
        _logger.LogInformation($"TriggerInvoiceFailure called for listener of swap {_swapId}.");
        _tcs.TrySetException(new Exception("Invoice payment failed or swap expired."));
    }
}
