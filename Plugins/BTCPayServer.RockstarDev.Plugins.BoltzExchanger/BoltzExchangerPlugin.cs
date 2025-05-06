#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

public class BoltzExchangerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        if (RuntimeWrapper.IsUnsupportedPlatform())
            throw new NotSupportedException("Unsupported platform. Only Linux and Windows are supported.");
        
        
        
        // Register HttpClientFactory if not already registered (usually is)
        services.AddHttpClient();

        // Register the WebSocket service as a singleton hosted service
        services.AddSingleton<BoltzWebSocketService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzWebSocketService>());

        // Register Singleton Service that maintains state
        services.AddSingleton<BoltzExchangerService>();

        // Register the connection string handler
        services.AddSingleton<ILightningConnectionStringHandler, BoltzLightningConnectionStringHandler>();

        base.Execute(services);
   }
}

public class RuntimeWrapper
{
    private static string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => throw new NotSupportedException("Unsupported architecture")
    };

    public static string GetClaimerPath(string datadir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (Architecture == "amd64")
                return Path.Combine(datadir, "Plugins", "BoltzExchanger", "claimer-linux-amd64");
            else if (Architecture == "arm64")
                return Path.Combine(datadir, "Plugins", "BoltzExchanger", "claimer-linux-arm64");
            else
                throw new NotSupportedException("Unsupported architecture");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(datadir, "Plugins", "BoltzExchanger", "claimer-windows-amd64.exe");
        
        throw new NotSupportedException("Unsupported platform");
    }
    
    public static bool IsUnsupportedPlatform()
    {
        return !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}
