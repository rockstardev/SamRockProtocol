#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public class BoltzLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly BoltzWebSocketService _webSocketService;

    public BoltzLightningConnectionStringHandler(
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        BoltzWebSocketService webSocketService)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _webSocketService = webSocketService;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        if (!TryParse(connectionString, out var options, out error))
        {
            error = null;
            return null;
        }

        // Validate the network if needed
        // if (options.IsTestnet && network.ChainName != ChainName.Testnet)
        // {
        //     error = $"Boltz connection string is for testnet, but the network is {network.ChainName}.";
        //     return null;
        // }

        var httpClient = _httpClientFactory.CreateClient(nameof(BoltzLightningClient)); // Use a named client
        var logger = _loggerFactory.CreateLogger<BoltzLightningClient>();

        error = null;
        return new BoltzLightningClient(options, httpClient, _webSocketService, logger);
    }

    public static bool TryParse(string connectionString, out BoltzOptions options, out string? error)
    {
        options = null;
        error = null;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error = "Connection string is empty.";
            return false;
        }

        var kv = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        if (!kv.TryGetValue("type", out var type) || !type.Equals("boltzexchanger", StringComparison.OrdinalIgnoreCase))
            // Not our type, let other handlers try
            return false;

        if (!kv.TryGetValue("apiurl", out var apiUrlStr) || !Uri.TryCreate(apiUrlStr, UriKind.Absolute, out var apiUrl))
        {
            error = "Invalid or missing 'apiurl' parameter.";
            return false;
        }

        if (apiUrl.Scheme != "http" && apiUrl.Scheme != "https")
        {
            error = "'apiurl' must use http or https scheme.";
            return false;
        }

        if (!kv.TryGetValue("swap-to", out var swapToAsset) || string.IsNullOrWhiteSpace(swapToAsset))
            // Default or validation?
            swapToAsset = "L-BTC"; // Assuming default for now
        // error = "Missing 'swap-to' parameter.";
        // return false;
        options = new BoltzOptions { ApiUrl = apiUrl, SwapToAsset = swapToAsset };

        return true;
    }
}
