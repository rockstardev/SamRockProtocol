#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Lightning;
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public class BoltzLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BoltzExchangerService _service;
    private readonly ILoggerFactory _loggerFactory;

    // Inject required services
    public BoltzLightningConnectionStringHandler(IHttpClientFactory httpClientFactory, BoltzExchangerService service, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _service = service;
        _loggerFactory = loggerFactory;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        try
        {
            return CreateInternal(connectionString, network, out error);
        }
        catch (Exception e)
        {
            error = $"Error while initializing Boltz plugin: {e.Message}";
            return null;
        }
    }

    // Renamed from Parse and updated return type and logic
    private ILightningClient? CreateInternal(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);

        if (type != "boltzexchanger")
        {
            // Not our type, let other handlers try
            error = null;
            return null;
        }

        if (!kv.TryGetValue("apiurl", out var apiUrlStr) || !Uri.TryCreate(apiUrlStr, UriKind.Absolute, out var apiUrl))
        {
            error = "Invalid or missing 'apiurl' parameter.";
            return null;
        }

        if (apiUrl.Scheme != "http" && apiUrl.Scheme != "https")
        {
            error = "The key 'apiurl' should be an URI starting by http:// or https://";
            return null;
        }

        if (!kv.TryGetValue("swap-to", out var swapToAsset) || string.IsNullOrWhiteSpace(swapToAsset))
            // Default or validation?
            swapToAsset = "L-BTC"; // Assuming default for now
        // error = "Missing 'swap-to' parameter.";
        // return false;

        // --- New: Parse and Validate Destination Address ---
        if (!kv.TryGetValue("swap-address", out var swapAddress) || string.IsNullOrWhiteSpace(swapAddress))
        {
            error = "The key 'swap-address' is missing or empty in the connection string.";
            return null;
        }

        // try
        // {
        //     // Basic validation: Check if it's a valid BitcoinAddress for the target network
        //     // Note: This doesn't guarantee it's a *Liquid* address specifically, 
        //     // but it's a good first step. More robust validation might be needed.
        //     BitcoinAddress.Create(destinationAddressStr, network);
        //     // TODO: Add specific check for Liquid address format if NBitcoin supports it easily.
        // }
        // catch (FormatException)
        // {
        //     error = $"'swap-address' ('{destinationAddressStr}') is not a valid address format for the network '{network.Name}'.";
        //     return null;
        // }

        var options = new BoltzOptions
        {
            ApiUrl = apiUrl,
            SwapTo = swapToAsset, // Assuming BTC Lightning is what user receives
            SwapAddress = swapAddress, // Store the validated address
        };

        // Create HttpClient instance
        var httpClient = _httpClientFactory.CreateClient(nameof(BoltzLightningClient)); 
        // Consider configuring base address or other HttpClient options here if needed
        // httpClient.BaseAddress = options.ApiUrl; // BaseAddress is set in BoltzLightningClient constructor

        // Create Logger instance
        var logger = _loggerFactory.CreateLogger<BoltzLightningClient>();

        // Instantiate the client
        var client = new BoltzLightningClient(options, httpClient, _service, logger);

        error = null;
        return client;
    }
}
