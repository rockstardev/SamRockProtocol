#nullable enable
using System;
using System.Net.Http;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Altcoins;
using System.Linq;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public class BoltzLightningConnectionStringHandler(IHttpClientFactory httpClientFactory, BoltzExchangerService service, ILoggerFactory loggerFactory)
    : ILightningConnectionStringHandler
{
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

        // --- Updated: Parse and Validate Multiple Destination Addresses ---
        if (!kv.TryGetValue("swap-addresses", out var swapAddressesStr) || string.IsNullOrWhiteSpace(swapAddressesStr))
        {
            error = "The key 'swap-addresses' is missing or empty in the connection string.";
            return null;
        }

        var lbtcAddresses = swapAddressesStr.Split(',').Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        if (lbtcAddresses.Length == 0)
        {
            error = "The key 'swap-addresses' must contain at least one valid address.";
            return null;
        }

        // TODO: Validate addresses

        var options = new BoltzOptions
        {
            ApiUrl = apiUrl,
            SwapTo = "L-BTC", // for now we only do swap to L-BTC
            SwapAddresses = lbtcAddresses
        };

        // Create HttpClient instance
        var httpClient = httpClientFactory.CreateClient(nameof(BoltzLightningClient));

        // Create Logger instance
        var logger = loggerFactory.CreateLogger<BoltzLightningClient>();

        // Instantiate the client
        var client = new BoltzLightningClient(options, httpClient, service, logger);

        error = null;
        return client;
    }
}
