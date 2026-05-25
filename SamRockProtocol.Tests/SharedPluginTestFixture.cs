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
        // Force-load the SamRockProtocol assembly into the AppDomain so
        // PluginManager.PreloadPluginsFromAssemblies discovers it via
        // AppDomain.CurrentDomain.GetAssemblies() before BTCPay startup.
        _ = typeof(SamRockProtocol.SamRockProtocolPlugin);
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
            // BTCPay defaults plugins to isolated AssemblyLoadContext so production
            // plugins can be unloaded/swapped without restarting. In tests the
            // isolated context makes the plugin's assembly invisible to the test
            // process's MVC ApplicationParts discovery, so the controllers never
            // route (the plugin shows in DI but POSTs to its endpoints 404).
            // Load into the default context so MVC can discover the controllers.
            // Mirrors rockstardev/btcPayServerPlugins.RockstarDev's test fixture
            // and closes the master CI #22 OTP-404 failure deterministically.
            ServerTester.PayTester.LoadPluginsInDefaultAssemblyContext = false;
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
