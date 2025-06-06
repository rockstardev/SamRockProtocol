using System;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using SamRockProtocol.Services;

namespace SamRockProtocol;

public class SamRockProtocolPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" },
        // dependencies not working within BTCPay system
        //new() { Identifier = "BTCPayServer.RockstarDev.Plugins.BoltzExchanger", Condition = ">=0.0.1" }
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

        services.AddRateLimits(); // configured in SamrockProtocolHostedService
    }
}
