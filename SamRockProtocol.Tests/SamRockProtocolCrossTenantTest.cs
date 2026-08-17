using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Tests;

/// <summary>
/// HTTP-level test that POST /plugins/{storeId}/samrock/protocol only honors
/// an OTP on the route of the store the OTP was created for, and that the
/// legitimate same-store flow keeps working.
/// </summary>
[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class SamRockProtocolCrossTenantTest : UnitTestBase
{
    private readonly SharedPluginTestFixture _fixture;
    private readonly ITestOutputHelper _helper;

    public SamRockProtocolCrossTenantTest(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        _helper = helper;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    // AQUA-shape Liquid descriptor, same reference value as the happy-path test.
    private const string LiquidDescriptor =
        "ct(slip77(c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403),elsh(wpkh([e17c2d80/49'/1776'/0']xpub6BemYiVNp19a2CyepSKDsDp2LgfvzZHvmepc5yM656fFDf93qcZ8UpgNwK9EwNbBimkr4mjNbK7anPqKS9M3pa9sGtve9seQaHuQJjJU6ps/0/*)))#ugh3xr7l";

    [Fact]
    public async Task SamRockProtocol_OtpFromOtherStore_IsRejected()
    {
        var userA = ServerTester.NewAccount();
        await userA.GrantAccessAsync();
        var storeIdA = userA.StoreId;

        var userB = ServerTester.NewAccount();
        await userB.GrantAccessAsync();
        var storeIdB = userB.StoreId;

        Assert.NotEqual(storeIdA, storeIdB);

        // 1. User A mints an OTP for their own store, Lightning selected.
        using var clientA = new HttpClient { BaseAddress = ServerTester.PayTester.ServerUri };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{userA.RegisterDetails.Email}:{userA.RegisterDetails.Password}"));
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);

        // Same warm-up retry as SamRockProtocolHappyPathTest: plugin controller
        // routes can race ServerTester boot. The OTP endpoints also share a
        // rate-limit zone (12r/min, burst=3, per remote address) with the
        // happy-path test, so retry on 429 as well.
        string otp = null;
        const int otpMaxAttempts = 20;
        const int otpDelayMs = 500;
        const int rateLimitDelayMs = 6000;
        for (var attempt = 1; attempt <= otpMaxAttempts; attempt++)
        {
            var otpReq = new StringContent(
                JsonSerializer.Serialize(new { btc = false, btcln = true, lbtc = false }),
                Encoding.UTF8, "application/json");
            var otpResp = await clientA.PostAsync($"api/v1/stores/{storeIdA}/samrock/otps", otpReq);
            var otpRespBody = await otpResp.Content.ReadAsStringAsync();
            _helper.WriteLine($"OTP create attempt {attempt}/{otpMaxAttempts} ({(int)otpResp.StatusCode}): {otpRespBody}");
            if (otpResp.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(otpDelayMs);
                continue;
            }
            if (otpResp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(rateLimitDelayMs);
                continue;
            }
            Assert.True(otpResp.IsSuccessStatusCode,
                $"OTP create expected 2xx, got {(int)otpResp.StatusCode}: {otpRespBody}");
            using var otpDoc = JsonDocument.Parse(otpRespBody);
            foreach (var name in new[] { "otp", "Otp", "OTP" })
            {
                if (otpDoc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    otp = v.GetString();
                    break;
                }
            }
            break;
        }
        Assert.False(string.IsNullOrEmpty(otp), "OTP not found in create response");

        // Raw JSON string: the request model binds "BTC-LN" via an explicit
        // [JsonProperty] name that anonymous C# types cannot express.
        var payloadJson = "{\"Version\":\"1.0\",\"BTC-LN\":{\"Type\":\"Boltz\",\"LBTC\":{\"Descriptor\":\"" +
                          LiquidDescriptor + "\"}}}";

        // 2. POST the OTP to store B's protocol route: must be rejected with
        //    404 before any processing.
        using var anon = new HttpClient { BaseAddress = ServerTester.PayTester.ServerUri };
        var (crossResp, crossBody) = await PostProtocolWithRetry(anon, storeIdB, otp, payloadJson, "Cross-store");

        Assert.True(crossResp.StatusCode == HttpStatusCode.NotFound,
            $"An OTP minted for store {storeIdA} must not be accepted on the route of store " +
            $"{storeIdB} (HTTP {(int)crossResp.StatusCode}). Response: {crossBody}");

        // 3. The rejected request must not have consumed the OTP: status stays
        //    "pending".
        var statusResp = await clientA.GetAsync($"api/v1/stores/{storeIdA}/samrock/otps/{otp}");
        var statusBody = await statusResp.Content.ReadAsStringAsync();
        _helper.WriteLine($"OTP status after cross-store POST ({(int)statusResp.StatusCode}): {statusBody}");
        Assert.True(statusResp.IsSuccessStatusCode,
            $"OTP status expected 2xx, got {(int)statusResp.StatusCode}: {statusBody}");
        using (var statusDoc = JsonDocument.Parse(statusBody))
        {
            string status = null;
            foreach (var name in new[] { "status", "Status" })
            {
                if (statusDoc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    status = v.GetString();
                    break;
                }
            }
            Assert.True(string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase),
                $"OTP was consumed by the cross-store request (status={status}).");
        }

        // 4. Legitimate-flow regression: the same OTP must still work on its
        //    own store's route. (HTTP stays 200 even in EnableBoltzSupport=false
        //    builds - the inner BTC_LN result reports "Boltz support is not
        //    enabled in this build." but the action returns Ok.)
        var (legitResp, legitBody) = await PostProtocolWithRetry(anon, storeIdA, otp, payloadJson, "Same-store");
        Assert.True(legitResp.IsSuccessStatusCode,
            $"Same-store protocol POST expected 2xx, got {(int)legitResp.StatusCode}: {legitBody}");
    }

    // The protocol endpoint is rate limited per remote address; retry on 429
    // until the token bucket refills.
    private async Task<(HttpResponseMessage resp, string body)> PostProtocolWithRetry(
        HttpClient client, string storeId, string otp, string payloadJson, string label)
    {
        const int maxAttempts = 10;
        const int rateLimitDelayMs = 6000;
        for (var attempt = 1; ; attempt++)
        {
            var resp = await client.PostAsync(
                $"plugins/{storeId}/samrock/protocol?otp={otp}",
                new StringContent(payloadJson, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            _helper.WriteLine($"{label} protocol POST attempt {attempt} ({(int)resp.StatusCode}): {body}");
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt >= maxAttempts)
                return (resp, body);
            await Task.Delay(rateLimitDelayMs);
        }
    }
}
