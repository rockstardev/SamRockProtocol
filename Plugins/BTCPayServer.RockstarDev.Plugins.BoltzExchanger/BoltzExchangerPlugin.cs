#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public class BoltzExchangerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // Register HttpClientFactory if not already registered (usually is)
        services.AddHttpClient();

        // Register the WebSocket service as a singleton hosted service
        services.AddSingleton<BoltzWebSocketService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzWebSocketService>());

        // Register the CovClaimDaemon 
        services.AddSingleton<CovClaimDaemon>();

        // Register the connection string handler
        services.AddSingleton<ILightningConnectionStringHandler, BoltzLightningConnectionStringHandler>();

        _ = services.AddSingleton<PluginRegistrator>();
        base.Execute(services);

        // TODO: Implement the core logic in BoltzLightningClient
        // Connection string: type=boltzexchanger;swap-to=L-BTC;apiurl=https://api.[testnet.]boltz.exchange/
    }
}

// Helper class to ensure plugin gets initialized (if needed later)
internal class PluginRegistrator
{
    public PluginRegistrator(ILogger<BoltzExchangerPlugin> logger)
    {
        logger.LogInformation("Boltz Exchanger Plugin activated.");
    }
}
