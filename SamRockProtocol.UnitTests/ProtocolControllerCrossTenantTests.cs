using System.Text;
using BTCPayServer.HostedServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SamRockProtocol.Controllers;
using SamRockProtocol.Models;
using SamRockProtocol.Services;
using Xunit;

namespace SamRockProtocol.UnitTests;

/// <summary>
/// Tests that ProtocolController.SamRockProtocol only honors an OTP on the
/// route of the store the OTP was created for, and rejects mismatches before
/// touching any store-scoped dependency.
///
/// The harness deliberately leaves every store-scoped dependency
/// (StoreRepository, ExplorerClientProvider, BTCPayWalletProvider,
/// PaymentMethodHandlerDictionary, EventAggregator) null: correct behavior
/// rejects a mismatched request right after the OTP lookup, so none of them
/// may be dereferenced.
/// </summary>
public class ProtocolControllerCrossTenantTests
{
    private const string OtpStoreId = "store-otp";
    private const string RouteStoreId = "store-route";
    private const string Otp = "otp-0123456789";

    // AQUA-shape Liquid descriptor, copied from DescriptorParserTests.
    private const string LiquidDescriptor =
        "ct(slip77(c82e173e7eb01dd024136f0c956a2ec078ff04c6abf5611c5db41e16d1326403),elsh(wpkh([e17c2d80/49'/1776'/0']xpub6BemYiVNp19a2CyepSKDsDp2LgfvzZHvmepc5yM656fFDf93qcZ8UpgNwK9EwNbBimkr4mjNbK7anPqKS9M3pa9sGtve9seQaHuQJjJU6ps/0/*)))#ugh3xr7l";

    [Fact]
    public async Task OtpMintedForOtherStore_IsRejectedBeforeAnyProcessing()
    {
        var (controller, hostedService, boltzLogger) = CreateController(RouteStoreId, withOtp: true);

        IActionResult result;
        try
        {
            result = await controller.SamRockProtocol();
        }
        catch (NullReferenceException ex)
        {
            Assert.Fail(
                "The cross-store request was not rejected at the OTP boundary: " +
                "execution reached store-scoped code (" + ex.GetType().Name +
                " on a dependency this harness leaves null).");
            throw; // unreachable; Assert.Fail always throws
        }

        // Rejected with 404 before any store is touched.
        Assert.IsType<NotFoundObjectResult>(result);

        // OtpUsed only runs at the end of processing, so a still-pending OTP
        // proves no BTC/LBTC/BTC-LN processing ran.
        Assert.True(hostedService.TryGet(Otp, out _),
            "OTP was consumed by a request sent to another store's route.");

        // SetBoltz logs at Information level as its first statement; any
        // captured entry means the Lightning setup path executed.
        Assert.Empty(boltzLogger.Messages);
    }

    [Fact]
    public async Task UnknownOtp_IsRejected()
    {
        // Canary: the OTP lookup rejects unknown OTPs on both old and new code.
        // If this test ever fails, the harness (not the controller) is broken.
        var (controller, _, _) = CreateController(RouteStoreId, withOtp: false);

        var result = await controller.SamRockProtocol();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    private static (ProtocolController controller, SamRockProtocolHostedService hostedService, ListLogger<BoltzWrapper> boltzLogger)
        CreateController(string routeStoreId, bool withOtp)
    {
        // Real OTP store. EventAggregator and RateLimitService are never touched by
        // Add/TryGet/OtpUsed (dictionary-only operations), so null is safe.
        var hostedService = new SamRockProtocolHostedService(
            null,
            NullLogger<PendingTransactionService>.Instance,
            null);

        if (withOtp)
        {
            hostedService.Add(Otp, new ImportWalletsViewModel
            {
                StoreId = OtpStoreId,
                Btc = false,
                BtcLn = true,
                Lbtc = false,
                Expires = DateTimeOffset.UtcNow.AddMinutes(5)
            });
        }

        var boltzLogger = new ListLogger<BoltzWrapper>();
        var boltzWrapper = CreateBoltzWrapper(boltzLogger);

        var routeData = new RouteData();
        routeData.Values["storeId"] = routeStoreId;

        var controller = new ProtocolController(
            hostedService,
            null, // PaymentMethodHandlerDictionary - must never be reached
            null, // ExplorerClientProvider        - must never be reached
            null, // BTCPayWalletProvider          - must never be reached
            null, // StoreRepository               - must never be reached
            null, // EventAggregator               - must never be reached
            NullLogger<ProtocolController>.Instance,
            boltzWrapper)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(withOtp ? Otp : "no-such-otp"),
                RouteData = routeData
            }
        };

        return (controller, hostedService, boltzLogger);
    }

    private static DefaultHttpContext BuildHttpContext(string otp)
    {
        var payload = "{\"Version\":\"1.0\",\"BTC-LN\":{\"Type\":\"Boltz\",\"LBTC\":{\"Descriptor\":\"" +
                      LiquidDescriptor + "\"}}}";
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json";
        http.Request.QueryString = new QueryString("?otp=" + Uri.EscapeDataString(otp));
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        return http;
    }

    private static BoltzWrapper CreateBoltzWrapper(ILogger<BoltzWrapper> logger)
    {
        // BoltzWrapper's primary constructor shape differs between BOLTZ_SUPPORT
        // and non-BOLTZ_SUPPORT builds; resolve it reflectively so this test
        // compiles and runs in both flavors.
        var ctor = typeof(BoltzWrapper).GetConstructors().Single();
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(ILogger<BoltzWrapper>) ? (object)logger : null)
            .ToArray();
        return (BoltzWrapper)ctor.Invoke(args);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
