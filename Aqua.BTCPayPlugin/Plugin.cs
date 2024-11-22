using Aqua.BTCPayPlugin.Services;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Template;
using Microsoft.Extensions.DependencyInjection;

namespace Aqua.BTCPayPlugin;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"}
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("header-nav", "TemplatePluginHeaderNav");
        
        services.AddHostedService<ApplicationPartsLogger>();
        services.AddSingleton<MyPluginService>();
    }
}
