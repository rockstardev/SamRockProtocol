#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Formats.Tar;

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

        // Build the service provider to resolve services needed for extraction
        var serviceProvider = services.BuildServiceProvider();
        var dataDirectory = serviceProvider.GetService<IOptions<DataDirectories>>();
        var pluginDir = dataDirectory.Value.PluginDir;
        RuntimeWrapper.ExtractClaimerIfNeeded(pluginDir);

        var platformClaimer = RuntimeWrapper.GetClaimerPath(pluginDir);
        if (!File.Exists(platformClaimer))
            throw new Exception("Claimer executable not found. Please check the plugin directory.");
            
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

    public static string GetClaimerPath(string pluginDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (Architecture == "amd64")
                return CombineToPath(pluginDir, "claimer-linux-amd64");
            else if (Architecture == "arm64")
                return CombineToPath(pluginDir,  "claimer-linux-arm64");
            else
                throw new NotSupportedException("Unsupported architecture");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return CombineToPath(pluginDir, "claimer-windows-amd64.exe");

        throw new NotSupportedException("Unsupported platform");
    }

    public static bool IsUnsupportedPlatform()
    {
        return !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    public static void ExtractClaimerIfNeeded(string pluginDir)
    {
        var claimerPath = CombineToPath(pluginDir, "");
        var targzpath = CombineToPath(pluginDir, "claimer-linux.tar.gz");

        if (File.Exists(targzpath))
        {
            try
            {
                using var fileStream = File.OpenRead(targzpath);
                using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, claimerPath, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error extracting claimer: {ex.Message}");
            }
            
            File.Delete(targzpath);
        }
    }

    private static string CombineToPath(string pluginDir, string file)
    {
        var ns = nameof(BTCPayServer.RockstarDev.Plugins.BoltzExchanger);
        ns = "BTCPayServer.RockstarDev.Plugins.BoltzExchanger";
        
        return Path.Combine(pluginDir, ns, "ClaimerExecutables", file);
    }
}
