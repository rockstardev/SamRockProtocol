using System;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using SamRockProtocol.Models;
using SamRockProtocol.Services;

namespace SamRockProtocol;

public class SamRockProtocolPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.1.6" },
        //new() { Identifier = "BTCPayServer.Plugins.Boltz", Condition = ">=2.1.12" }
    ];

    public static bool IsDevMode =>
        // #if DEBUG
        //             return true;
        // #else
        //             return false;
        // #endif
        true;

    public override void Execute(IServiceCollection services)
    {
        //services.AddUIExtension("store-wallets-nav", "AquaSidebarNav");
        services.AddUIExtension("dashboard-setup-guide-payment", "SamRockProtocolSetupPayments");
        services.AddUIExtension("store-integrations-nav", "SamRockProtocolNav");

        services.AddSingleton<SamrockProtocolHostedService>();
        services.AddScheduledTask<SamrockProtocolHostedService>(TimeSpan.FromMinutes(1));
        services.AddSingleton<BoltzWrapper>();

        services.AddRateLimits(); // configured in SamrockProtocolHostedService
    }
}
