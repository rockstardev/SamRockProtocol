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
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public partial class BoltzLightningClient : ILightningClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly BoltzOptions _options;
    private readonly EventAggregator _eventAggregator;

    // Internal cache for preimage -> swap ID mapping for lookups
    private readonly ConcurrentDictionary<string, string> _preimageHashToSwapId = new();
    private readonly ConcurrentDictionary<string, SwapData> _swapData = new(); // Key: Swap ID
    private readonly BoltzWebSocketService _webSocketService;
    private readonly CovClaimDaemon _covClaimDaemon;

    public BoltzLightningClient(BoltzOptions options, HttpClient httpClient, BoltzWebSocketService webSocketService, 
        ILogger<BoltzLightningClient> logger, CovClaimDaemon covClaimDaemon, EventAggregator eventAggregator)
    {
        _options = options;
        _httpClient = httpClient;
        _webSocketService = webSocketService;
        _logger = logger;
        _covClaimDaemon = covClaimDaemon;
        _eventAggregator = eventAggregator;
        _httpClient.BaseAddress = options.ApiUrl;
    }

    public ILogger<BoltzLightningClient> _logger { get; }

    public void Dispose()
    {
        _logger.LogInformation("Disposing BoltzLightningClient.");
        // HttpClient is managed externally (HttpClientFactory)
        // WebSocketService is managed externally (Hosted Service)
        _swapData.Clear();
        _preimageHashToSwapId.Clear();
    }

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = new())
    {
        return CreateInvoice(createInvoiceRequest.Amount, createInvoiceRequest.Description, createInvoiceRequest.Expiry, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"CreateInvoice called: Amount={amount}, Description='{description}', Expiry={expiry}");
        if (!string.IsNullOrEmpty(description)) _logger.LogWarning("Boltz CreateInvoice does not use the 'description' field.");

        // Input validation (Basic)
        if (amount == null || amount <= LightMoney.Zero)
        {
            _logger.LogError("Invalid amount for CreateInvoice.");
            throw new Exception("Invalid amount for CreateInvoice.");
        }

        try
        {
            // 1. Generate Preimage and Hash
            var preimage = RandomNumberGenerator.GetBytes(32);
            var preimageHash = SHA256.HashData(preimage);
            var preimageHashHex = Convert.ToHexString(preimageHash).ToLowerInvariant();

            _logger.LogInformation($"Creating Boltz Reverse Swap for {amount.ToUnit(LightMoneyUnit.Satoshi)} sats (Lightning -> {_options.SwapTo})");
            _logger.LogDebug($"Preimage Hash: {preimageHashHex}");

            // 2. Generate ephemeral key pair for this swap's claim mechanism
            var claimPrivateKey = new Key();
            var claimPublicKeyHex = claimPrivateKey.PubKey.ToHex();
            _logger.LogDebug($"Using ephemeral Claim Public Key: {claimPublicKeyHex}");

            // 3. Call Boltz API to create reverse swap
            var request = new CreateReverseSwapRequest
            {
                Address = _options.SwapAddress,
                From = "BTC", // We receive BTC (Lightning)
                To = _options.SwapTo, // We send L-BTC (on-chain)
                ClaimCovenant = true,
                InvoiceAmountSat = (long)amount.ToUnit(LightMoneyUnit.Satoshi),
                PreimageHash = preimageHashHex,
                ClaimPublicKey = claimPublicKeyHex // Provide the public key for the claim script
                // Add Address for claim?
                // Description? req.Description / req.DescriptionHash
            };

            _logger.LogInformation($"Sending CreateReverseSwap request to Boltz API: {_httpClient.BaseAddress}v2/swap/reverse");
            _logger.LogDebug(
                $"CreateReverseSwap Request Payload: From={request.From}, To={request.To}, Amount={request.InvoiceAmountSat}, PreimageHash={request.PreimageHash}, ClaimPubKey={request.ClaimPublicKey}");
            _logger.LogDebug($"Cancellation Token Status Before Call: IsCancellationRequested={cancellationToken.IsCancellationRequested}");

            // Explicitly call the extension method to resolve ambiguity
            var cancellationTokenDebug = new CancellationToken(false);
            //cancellationTokenDebug = cancellationToken;

            var response = await HttpClientJsonExtensions.PostAsJsonAsync(_httpClient, "/v2/swap/reverse", request, cancellationTokenDebug);
            response.EnsureSuccessStatusCode();

            var swapResponse = await response.Content.ReadFromJsonAsync<CreateReverseSwapResponse>(cancellationTokenDebug);

            if (swapResponse == null || string.IsNullOrEmpty(swapResponse.Id) || string.IsNullOrEmpty(swapResponse.Invoice))
            {
                _logger.LogError("Boltz API returned invalid response for reverse swap creation.");
                throw new Exception("Failed to create reverse swap: Invalid response from Boltz API.");
            }

            _logger.LogInformation($"Boltz Reverse Swap created successfully. ID: {swapResponse.Id}, Invoice: {swapResponse.Invoice.Substring(0, 15)}...");

            // 4. Parse the returned Lightning Invoice
            var bolt11 = BOLT11PaymentRequest.Parse(swapResponse.Invoice,
                _options.IsTestnet ? Network.TestNet : Network.Main);
            var invoice = new LightningInvoice
            {
                Id = swapResponse.Id,
                BOLT11 = swapResponse.Invoice,
                Amount = bolt11.MinimumAmount,
                ExpiresAt = bolt11.ExpiryDate,
                Status = LightningInvoiceStatus.Unpaid,
                PaymentHash = bolt11.PaymentHash.ToString(), // Use PaymentHash from BOLT11 as ID
                Preimage = preimageHashHex // Store hash initially, actual preimage only on payment
            };

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
                ClaimPublicKey = request.ClaimPublicKey,
                RefundPublicKey = swapResponse.RefundPublicKey
            }, cancellationTokenDebug);

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
            await _webSocketService.SubscribeToSwapStatusAsync(WebSocketUri(), swapResponse.Id, HandleSwapUpdate, cancellationTokenDebug);

            return invoice;
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating Boltz reverse swap.");
            throw new Exception($"Failed to communicate with Boltz API: {httpEx.Message}", httpEx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Boltz reverse swap invoice.");
            throw;
        }
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellationToken = default)
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
        await _webSocketService.UnsubscribeFromSwapStatusAsync(WebSocketUri(), swapId, cancellationToken);
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
                await _webSocketService.UnsubscribeFromSwapStatusAsync(WebSocketUri(), swap.Id, CancellationToken.None);
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
                await _webSocketService.UnsubscribeFromSwapStatusAsync(WebSocketUri(), swap.Id, CancellationToken.None);
            }
        }
        else
        {
            _logger.LogWarning($"Received status update for unknown or removed swap ID: {update.Id}");
        }
    }

    private Uri WebSocketUri()
    {
        var str = _options.ApiUrl.ToString().Replace("http", "ws") + "/v2/ws";
        str = str.Replace("//v2/ws", "/v2/ws"); // handle base URL with trailing slash
        return new Uri(str);
    }

    private bool IsFailedStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return false;
        var lowerStatus = status.ToLowerInvariant();
        // Add known failure statuses from Boltz API documentation
        return lowerStatus.Contains("fail") || lowerStatus.Contains("refund") || lowerStatus == "invoice.expired";
    }
    
    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    // Internal data structure for holding swap details
    private class SwapData
    {
        public required string Id { get; init; }
        public required byte[] Preimage { get; init; } // Store the preimage
        public required string PreimageHash { get; init; }
        public required LightningInvoice OriginalInvoice { get; init; } // Store the invoice BOLT11
        public SwapStatusUpdate? LastStatusUpdate { get; set; }
        public bool IsPaid { get; set; } // Flag indicating if listener was notified
    }
}
