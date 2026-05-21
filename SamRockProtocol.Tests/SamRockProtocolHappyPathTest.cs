using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using SamRockProtocol.Services;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class SamRockProtocolHappyPathTest : UnitTestBase
{
    private readonly SharedPluginTestFixture _fixture;
    private readonly ITestOutputHelper _helper;

    public SamRockProtocolHappyPathTest(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        _helper = helper;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    // AQUA-shape descriptors copied verbatim from a real successful protocol
    // call. BTC: BIP84 native segwit. LBTC: wrapped Liquid
    // (slip77 + elsh(wpkh)). Apostrophe-form hardened markers, mainnet xpub
    // prefix (the plugin's regex requires literal "xpub").
    private const string BtcDescriptor =
        "wpkh([e17c2d80/84'/0'/0']xpub6BemYiVNp19a19pfjF1QyNfD9vWnUYcZFgqo1m2cRP7GJJ7j9QZKuEGHnP775g4dFWFBm1h9jDGzqoK617XnyamAcLATGaAC68Cm5sgVS1V/0/*)#sutkjd48";

    private const string LbtcDescriptor =
        "ct(slip77(c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403),elsh(wpkh([e17c2d80/49'/1776'/0']xpub6BemYiVNp19a2CyepSKDsDp2LgfvzZHvmepc5yM656fFDf93qcZ8UpgNwK9EwNbBimkr4mjNbK7anPqKS9M3pa9sGtve9seQaHuQJjJU6ps/0/*)))#ugh3xr7l";

    [Fact]
    public async Task SamRockProtocol_AcceptsAquaDescriptors()
    {
        var user = ServerTester.NewAccount();
        await user.GrantAccessAsync();
        await user.MakeAdmin();
        var storeId = user.StoreId;

        // Diagnostic: dump loaded assemblies + plugin DI registrations to
        // narrow down why plugin services aren't visible to the test.
        var srpAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "SamRockProtocol");
        Assert.NotNull(srpAsm); // ensure plugin assembly is in AppDomain

        var allPlugins = ServerTester.PayTester.ServiceProvider
            .GetServices<BTCPayServer.Abstractions.Contracts.IBTCPayServerPlugin>()
            .Select(p => $"{p.Identifier}@{p.Version}")
            .ToList();
        var pluginsList = string.Join(", ", allPlugins);
        _helper.WriteLine($"Plugins in DI ({allPlugins.Count}): {pluginsList}");

        var config = ServerTester.PayTester.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        _helper.WriteLine($"DEBUG_PLUGINS config: '{config?["DEBUG_PLUGINS"]}'");
        _helper.WriteLine($"AppContext.BaseDirectory: {AppContext.BaseDirectory}");
        _helper.WriteLine($"appsettings.dev.json exists in BaseDirectory: {System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.dev.json"))}");

        var otpServiceResolved = ServerTester.PayTester.ServiceProvider.GetService(typeof(OtpService));
        _helper.WriteLine($"OtpService resolved: {otpServiceResolved != null}");

        var samrockPlugin = ServerTester.PayTester.ServiceProvider
            .GetServices<BTCPayServer.Abstractions.Contracts.IBTCPayServerPlugin>()
            .FirstOrDefault(p => p.Identifier == "SamRockProtocol");
        _helper.WriteLine($"SamRockProtocol plugin in DI: {samrockPlugin != null}, type: {samrockPlugin?.GetType().FullName}, assembly: {samrockPlugin?.GetType().Assembly.Location}");

        Assert.NotNull(samrockPlugin);

        var otpService = ServerTester.PayTester.GetService<OtpService>();
        Assert.NotNull(otpService);
        var serverUri = ServerTester.PayTester.ServerUri.ToString().TrimEnd('/');
        var otpModel = otpService!.CreateOtp(storeId, btc: true, btcln: false, lbtc: true, baseUrl: serverUri);
        Assert.False(string.IsNullOrEmpty(otpModel.Otp));

        var payload = new
        {
            Version = "1.0",
            BTC = new { Descriptor = BtcDescriptor },
            LBTC = new { Descriptor = LbtcDescriptor }
        };
        var json = JsonSerializer.Serialize(payload);

        using var http = new HttpClient { BaseAddress = ServerTester.PayTester.ServerUri };
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync($"plugins/{storeId}/samrock/protocol?otp={otpModel.Otp}", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("Success").GetBoolean(),
            $"Outer Success expected true. Body: {body}");

        var results = root.GetProperty("Result").GetProperty("Results");

        var btcResult = results.GetProperty("BTC");
        Assert.True(btcResult.GetProperty("Success").GetBoolean(),
            $"BTC import expected Success=true. Result: {btcResult}");

        // LBTC: BTCPay regtest stack ships without Elements/Liquid enabled.
        // ProtocolController.cs:244-248 returns Success=true with
        // "LBTC is not available on server, ignoring sent data" warning when
        // explorerProvider.GetNetwork("LBTC") is null. Either real-track or
        // warning-success satisfies Success=true here.
        var lbtcResult = results.GetProperty("LBTC");
        Assert.True(lbtcResult.GetProperty("Success").GetBoolean(),
            $"LBTC import expected Success=true. Result: {lbtcResult}");
    }
}
