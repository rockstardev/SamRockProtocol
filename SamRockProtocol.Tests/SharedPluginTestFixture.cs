using BTCPayServer.Tests;
using Xunit;

namespace BTCPayServer.Plugins.Tests;

// Generic configurable fixture that can be used with different parameters
public class ConfigurablePluginTestFixture : IDisposable
{
    private readonly string _testDirName;
    private readonly bool _useNewDb;

    public ConfigurablePluginTestFixture(string testDirName = "SharedPluginTests", bool useNewDb = true)
    {
        _testDirName = testDirName;
        _useNewDb = useNewDb;
        // Do NOT force-load the SamRockProtocol assembly into the AppDomain
        // here. PluginManager.AddPlugins scans AppDomain assemblies first and
        // registers any plugin it finds with Loader=null. Plugins loaded via
        // that path skip mvcBuilder.AddPluginLoader at PluginManager.cs:255,
        // which means MVC ApplicationParts never includes the plugin's
        // assembly -> the IBTCPayServerPlugin shows in DI but its controller
        // routes 404. Let the DEBUG_PLUGINS / plugins-folder path resolve the
        // plugin via PluginLoader.CreateFromAssemblyFile so the Loader is
        // non-null and MVC integration happens.
    }

    public ServerTester ServerTester { get; private set; }

    public void Dispose()
    {
        ServerTester?.Dispose();
        ServerTester = null;
    }

    public void Initialize(UnitTestBase testInstance)
    {
        if (ServerTester == null)
        {
            // Set fast sweep interval for all tests (1 second)
            // This is safe because the sweeper only processes enabled configurations
            Environment.SetEnvironmentVariable("BTCPAY_WALLETSWEEPER_INTERVAL", "1");

            var testDir = Path.Combine(Directory.GetCurrentDirectory(), _testDirName);
            ServerTester = testInstance.CreateServerTester(testDir, _useNewDb);
            ServerTester.StartAsync().GetAwaiter().GetResult();
        }
    }
}

// Specific fixture implementations for different collections
public class SharedPluginTestFixture : ConfigurablePluginTestFixture
{
    public SharedPluginTestFixture() : base() { }
}

[CollectionDefinition("Plugin Tests")]
public class PluginTestCollection : ICollectionFixture<SharedPluginTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

//
public class StandalonePluginTestFixture : ConfigurablePluginTestFixture
{
    public StandalonePluginTestFixture() : base("StandalonePluginTests") { }
}

[CollectionDefinition("Standalone Tests")]
public class StandaloneTestCollection : ICollectionFixture<StandalonePluginTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
