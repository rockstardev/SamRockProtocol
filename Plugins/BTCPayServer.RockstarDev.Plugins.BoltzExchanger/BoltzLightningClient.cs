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
    private readonly HttpClient _httpClient;
    private readonly BoltzExchangerService _boltzExchangerService;
    private readonly BoltzOptions _options;

    public BoltzLightningClient(BoltzOptions options, HttpClient httpClient, BoltzExchangerService boltzExchangerService,
        ILogger<BoltzLightningClient> logger)
    {
        _options = options;
        _httpClient = httpClient;
        _boltzExchangerService = boltzExchangerService;
        _logger = logger;
        _httpClient.BaseAddress = options.ApiUrl;
    }

    private ILogger<BoltzLightningClient> _logger { get; }

    public void Dispose()
    {
        _logger.LogInformation("Disposing BoltzLightningClient.");
    }

    private Uri WebSocketUri()
    {
        var str = _options.ApiUrl.ToString().Replace("http", "ws") + "/v2/ws";
        str = str.Replace("//v2/ws", "/v2/ws"); // handle base URL with trailing slash
        return new Uri(str);
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

            // 3. Call Boltz API to create reverse swap
            var request = new CreateReverseSwapRequest
            {
                Address = _options.SwapAddress,
                From = "BTC", // We receive BTC (Lightning)
                To = _options.SwapTo, // We send L-BTC (on-chain)
                InvoiceAmountSat = (long)amount.ToUnit(LightMoneyUnit.Satoshi),
                PreimageHash = preimageHashHex,
                ClaimPublicKey = claimPrivateKey.PubKey.ToHex() // Provide the public key for the claim script
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

            await _boltzExchangerService.InvoiceCreatedThroughSwap(invoice, request, swapResponse, preimage, preimageHashHex, claimPrivateKey, 
                WebSocketUri(), cancellationTokenDebug);

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

    public Task CancelInvoice(string invoiceId, CancellationToken cancellationToken = default)
    {
        return _boltzExchangerService.CancelInvoice(WebSocketUri(), invoiceId, cancellationToken);
    }
    
    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        return _boltzExchangerService.GetInvoice(invoiceId, cancellation);
    }

    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new CancellationToken())
    {
        return _boltzExchangerService.GetInvoice(paymentHash, cancellation);
    }
}
