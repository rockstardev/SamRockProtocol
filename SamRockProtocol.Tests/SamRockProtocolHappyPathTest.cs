using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // Plugin-load diagnostic
        var allPlugins = ServerTester.PayTester.ServiceProvider
            .GetServices<IBTCPayServerPlugin>()
            .Select(p => $"{p.Identifier}@{p.Version}")
            .ToList();
        _helper.WriteLine($"Plugins in DI ({allPlugins.Count}): {string.Join(", ", allPlugins)}");

        // OTP via Greenfield API with basic auth (admin credentials)
        using var client = new HttpClient { BaseAddress = ServerTester.PayTester.ServerUri };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{user.RegisterDetails.Email}:{user.RegisterDetails.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var otpReqBody = new { btc = true, btcln = false, lbtc = true };
        var otpReq = new StringContent(JsonSerializer.Serialize(otpReqBody), Encoding.UTF8, "application/json");
        var otpResp = await client.PostAsync($"api/v1/stores/{storeId}/samrock/otps", otpReq);
        var otpRespBody = await otpResp.Content.ReadAsStringAsync();
        _helper.WriteLine($"OTP create response ({(int)otpResp.StatusCode}): {otpRespBody}");
        Assert.True(otpResp.IsSuccessStatusCode,
            $"OTP create expected 2xx, got {(int)otpResp.StatusCode}: {otpRespBody}");

        using var otpDoc = JsonDocument.Parse(otpRespBody);
        // Property casing depends on the ASP.NET JSON serializer config. Try both.
        string otp = null;
        foreach (var name in new[] { "otp", "Otp", "OTP" })
        {
            if (otpDoc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                otp = v.GetString();
                break;
            }
        }
        Assert.False(string.IsNullOrEmpty(otp), $"OTP not found in response: {otpRespBody}");

        // Protocol POST (anonymous, OTP-gated)
        using var anon = new HttpClient { BaseAddress = ServerTester.PayTester.ServerUri };
        var payload = new
        {
            Version = "1.0",
            BTC = new { Descriptor = BtcDescriptor },
            LBTC = new { Descriptor = LbtcDescriptor }
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await anon.PostAsync($"plugins/{storeId}/samrock/protocol?otp={otp}", content);
        var body = await response.Content.ReadAsStringAsync();
        _helper.WriteLine($"Protocol POST response ({(int)response.StatusCode}): {body}");

        Assert.True(response.IsSuccessStatusCode,
            $"Protocol POST expected 2xx, got {(int)response.StatusCode}: {body}");

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
