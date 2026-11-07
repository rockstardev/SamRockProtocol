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
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" },
#if BOLTZ_SUPPORT
        new() { Identifier = "BTCPayServer.Plugins.Indra", Condition = ">=2.5.0" }
#endif
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("dashboard-setup-guide-payment", "SamRockProtocolSetupPayments");
        services.AddUIExtension("store-integrations-nav", "SamRockProtocolNav");

        services.AddSingleton<SamRockProtocolHostedService>();
        services.AddScheduledTask<SamRockProtocolHostedService>(TimeSpan.FromMinutes(1));
        services.AddSingleton<BoltzWrapper>();
        services.AddSingleton<OtpService>();

        services.AddRateLimits(); // configured in SamRockProtocolHostedService
    }
}
