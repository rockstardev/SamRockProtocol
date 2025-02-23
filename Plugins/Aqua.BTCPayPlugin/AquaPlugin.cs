using System;
using Aqua.BTCPayPlugin.Services;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Aqua.BTCPayPlugin;

public class AquaPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
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
        services.AddUIExtension("store-wallets-nav", "AquaSidebarNav");

        services.AddSingleton<SamrockProtocolHostedService>();
        services.AddScheduledTask<SamrockProtocolHostedService>(TimeSpan.FromMinutes(1));

        services.AddRateLimits(); // configured in SamrockProtocolHostedService
    }
}