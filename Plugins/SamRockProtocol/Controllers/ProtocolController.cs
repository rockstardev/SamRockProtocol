using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Client;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using NBitcoin;
using NicolasDorier.RateLimits;
using SamRockProtocol.Services;
using SamRockProtocol.Models;
using Microsoft.Extensions.Logging;
using BTCPayServer.Common;
using Microsoft.AspNetCore.Http;

namespace SamRockProtocol.Controllers;

[Route("~/plugins/{storeId}/samrock")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ProtocolController(
    SamRockProtocolHostedService samrockProtocolService,
    PaymentMethodHandlerDictionary handlers,
    ExplorerClientProvider explorerProvider,
    BTCPayWalletProvider walletProvider,
    StoreRepository storeRepository,
    EventAggregator eventAggregator,
    ILogger<ProtocolController> logger,
    BoltzWrapper boltzWrapper)
    : Controller
{
    [AllowAnonymous]
    [IgnoreAntiforgeryToken] // dart/dio update now causes Form[""] to break, so this is needed
    [RateLimitsFilter("SamRockProtocol", Scope = RateLimitsScope.RemoteAddress)]
    [HttpPost("protocol")]
    public async Task<IActionResult> SamRockProtocol()
    {
        var otp = Request.Query["otp"].ToString();
        if (string.IsNullOrEmpty(otp) || !samrockProtocolService.TryGet(otp, out var importWalletModel))
            return NotFound(new SamRockProtocolResponse(false, "OTP not found or expired.", null));

        // An OTP may only be redeemed on the route of the store it was created for.
        var routeStoreId = RouteData.Values["storeId"] as string;
        if (!string.Equals(routeStoreId, importWalletModel.StoreId, StringComparison.Ordinal))
            return NotFound(new SamRockProtocolResponse(false, "OTP not found or expired.", null));

        var storeData = await storeRepository.FindStore(importWalletModel.StoreId);
        if (storeData == null)
            return NotFound(new SamRockProtocolResponse(false, "Store not found.", null));

        var jsonField = await ReadJsonField();
        var setupModel = UtilJson.Parse<SamRockProtocolRequest>(jsonField, out var ex);
        if (setupModel == null)
            return BadRequest(new SamRockProtocolResponse(false, "Invalid JSON format.", ex));

        // Only allow to setup payment methods that were selected in the initial import step
        if (!importWalletModel.Btc && setupModel.BTC != null)
            setupModel.BTC = null;
        if (!importWalletModel.BtcLn && setupModel.BTCLN != null)
            setupModel.BTCLN = null;
        if (!importWalletModel.Lbtc && setupModel.LBTC != null)
            setupModel.LBTC = null;
        
        logger.LogInformation("SamRockProtocol request initiated. setupModel={SetupModel}", setupModel.ToJson());
        return await processSamRockProtocolRequest(setupModel, storeData, otp);
    }

    /// <summary>
    /// Reads the "json" field from the request body, tolerating multiple content types.
    /// AQUA wallet (Dart/Dio) switched from application/x-www-form-urlencoded to
    /// multipart/form-data with a malformed boundary prefix, which ASP.NET Core's
    /// form parser rejects. This method falls back to raw body parsing when that happens.
    /// </summary>
    private async Task<string> ReadJsonField()
    {
        Request.EnableBuffering();

        try
        {
            var formValue = Request.Form["json"].ToString();
            if (!string.IsNullOrEmpty(formValue))
                return formValue;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Form parsing failed, falling back to raw body parsing");
        }

        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrEmpty(body))
            return null;

        var trimmed = body.Trim();

        // Direct JSON body (application/json)
        if (trimmed.StartsWith("{"))
            return trimmed;

        // URL-encoded: json={...}
        var jsonIdx = body.IndexOf("json=", StringComparison.Ordinal);
        if (jsonIdx >= 0)
        {
            var value = body.Substring(jsonIdx + 5);
            var ampIdx = value.IndexOf('&');
            if (ampIdx >= 0)
                value = value.Substring(0, ampIdx);
            return Uri.UnescapeDataString(value);
        }

        // Multipart: extract content after name="json" field header
        var nameIdx = body.IndexOf("name=\"json\"", StringComparison.Ordinal);
        if (nameIdx >= 0)
        {
            var afterName = body.Substring(nameIdx);
            var blankLine = afterName.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (blankLine < 0)
                blankLine = afterName.IndexOf("\n\n", StringComparison.Ordinal);
            if (blankLine >= 0)
            {
                var headerEnd = afterName[blankLine] == '\r' ? blankLine + 4 : blankLine + 2;
                var content = afterName.Substring(headerEnd);
                var boundaryIdx = content.IndexOf("\r\n--", StringComparison.Ordinal);
                if (boundaryIdx < 0)
                    boundaryIdx = content.IndexOf("\n--", StringComparison.Ordinal);
                if (boundaryIdx >= 0)
                    content = content.Substring(0, boundaryIdx);
                return content.Trim();
            }
        }

        return null;
    }

    private async Task<IActionResult> processSamRockProtocolRequest(SamRockProtocolRequest setupModel, StoreData storeData, string otp)
    {
        var result = new SamRockProtocolSetupResponse();
        if (setupModel.BTC != null && !string.IsNullOrEmpty(setupModel.BTC.Descriptor))
        {
            var key = SamRockProtocolKeys.BTC;
            try
            {
                var descriptor = DescriptorParser.NormalizeDescriptor(setupModel.BTC.Descriptor);
                if (!DescriptorParser.TryParseBitcoinDescriptor(descriptor,
                        out var scriptType, out var fingerprint, out var derivationPath, out var xpub, out var error))
                {
                    result.Results[key] = new SamRockProtocolResponse(false, error, null);
                }
                else
                {
                    var suffix = GetNBXplorerSuffix(scriptType, descriptor);
                    if (suffix == null)
                    {
                        result.Results[key] = new SamRockProtocolResponse(false, $"Unsupported BTC script type: {scriptType}", null);
                    }
                    else
                    {
                        var derivationScheme = xpub + suffix;
                        await SetupWalletAsync(derivationScheme, fingerprint, derivationPath, "BTC", storeData, key, result);
                    }
                }
            }
            catch (Exception btcex)
            {
                result.Results[key] = new SamRockProtocolResponse(false, null, btcex);
            }
        }

        if (setupModel.LBTC != null && !string.IsNullOrEmpty(setupModel.LBTC.Descriptor))
        {
            var key = SamRockProtocolKeys.LBTC;

            if (explorerProvider.GetNetwork("LBTC") != null)
            {
                try
                {
                    var descriptor = DescriptorParser.NormalizeDescriptor(setupModel.LBTC.Descriptor);
                    if (!DescriptorParser.TryParseLiquidDescriptor(descriptor, out var blindingKey, out var suffix, out var fingerprint,
                            out var derivationPath, out var xpub, out var error))
                    {
                        result.Results[key] = new SamRockProtocolResponse(false, error, null);
                    }
                    else
                    {
                        var derivationScheme = $"{xpub}{suffix}-[slip77={blindingKey}]";
                        await SetupWalletAsync(derivationScheme, fingerprint, derivationPath, "LBTC", storeData, key, result);
                    }
                }
                catch (Exception lbtcex)
                {
                    result.Results[key] = new SamRockProtocolResponse(false, null, lbtcex);
                }
            }
            else
            {
                result.Results[key] = new SamRockProtocolResponse(true,
                    "Warning: LBTC is not available on server, ignoring sent data", null);
            }
        }

        if (setupModel.BTCLN != null)
        {
            if (string.Equals(setupModel.BTCLN.Type, "Boltz", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(setupModel.BTCLN.LBTC?.Descriptor))
                {
                    result.Results[SamRockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false,
                        "Boltz setup requires a Liquid descriptor.", null);
                }
                else
                {
                    await boltzWrapper.SetBoltz(storeData.Id, DescriptorParser.NormalizeDescriptor(setupModel.BTCLN.LBTC.Descriptor), result);
                }
            }
            else
            {
                result.Results[SamRockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false,
                    $"Lightning setup configured with unknown type: {setupModel.BTCLN.Type}", null);
            }
        }

        // TODO: If both LBTC is set and BtcLn is set, need to generate as many addresses for LiquidChain
        // as we have in setupModel.BtcLn.LiquidAddresses.Length to reserve them

        var allSuccess = result.Results.Count > 0 && result.Results.Values.All(a => a.Success);
        string errorMessage = null;
        if (!allSuccess && result.Results.TryGetValue(SamRockProtocolKeys.BTC_LN, out var lnResult))
        {
            errorMessage = lnResult.Message;
        }

        samrockProtocolService.OtpUsed(otp, allSuccess, errorMessage);

        logger.LogInformation("SamRockProtocol setup completed. setupModel={SetupModel} result={Result}", setupModel.ToJson(), result.ToJson());

        return Ok(new
        {
            Success = allSuccess,
            Message = allSuccess ? "Wallet setup successfully." : "Wallet setup failed.",
            Result = result
        });
    }

    private async Task SetupWalletAsync(string derivationScheme, string fingerprint, string derivationPath, string networkCode,
        StoreData storeData, SamRockProtocolKeys key, SamRockProtocolSetupResponse result)
    {
        if (string.IsNullOrEmpty(derivationScheme) || explorerProvider.GetNetwork(networkCode) == null)
        {
            result.Results[key] =
                new SamRockProtocolResponse(false, $"{networkCode} is not supported on this server.", null);
            return;
        }

        if (string.IsNullOrEmpty(fingerprint) || !HDFingerprint.TryParse(fingerprint, out var hdFingerprint))
        {
            result.Results[key] =
                new SamRockProtocolResponse(false, $"Invalid fingerprint for wallet supplied", null);
            return;
        }

        try
        {
            var network = explorerProvider.GetNetwork(networkCode);
            var strategy = ParseDerivationStrategy(derivationScheme, network);
            strategy.AccountKeySettings[0].RootFingerprint = hdFingerprint;
            strategy.AccountKeySettings[0].AccountKeyPath = new KeyPath(derivationPath);

            var wallet = walletProvider.GetWallet(network);
            await wallet.TrackAsync(strategy.AccountDerivation);

            await ConfigureStorePaymentMethod(storeData, strategy, network);

            result.Results[key] = new SamRockProtocolResponse(true, null, null);
        }
        catch (Exception ex)
        {
            result.Results[key] = new SamRockProtocolResponse(false, null, ex);
        }
    }

    private async Task ConfigureStorePaymentMethod(StoreData storeData, DerivationSchemeSettings strategy,
        BTCPayNetwork network)
    {
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        storeData.SetPaymentMethodConfig(handlers[paymentMethodId], strategy);

        var storeBlob = storeData.GetStoreBlob();
        storeBlob.SetExcluded(paymentMethodId, false);
        storeBlob.PayJoinEnabled = false;
        storeData.SetStoreBlob(storeBlob);

        await storeRepository.UpdateStore(storeData);
        eventAggregator.Publish(new WalletChangedEvent { WalletId = new WalletId(storeData.Id, network.CryptoCode) });
    }

    private DerivationSchemeSettings ParseDerivationStrategy(string derivationScheme, BTCPayNetwork network)
    {
        var parser = new DerivationSchemeParser(network);
        var isOD = Regex.Match(derivationScheme, @"\(.*?\)");
        if (isOD.Success)
        {
            var derivationSchemeSettings = new DerivationSchemeSettings();
            var result = parser.ParseOutputDescriptor(derivationScheme);
            derivationSchemeSettings.AccountOriginal = derivationScheme.Trim();
            derivationSchemeSettings.AccountDerivation = result.Item1;
            derivationSchemeSettings.AccountKeySettings = result.Item2?.Select((path, i) => new AccountKeySettings
                {
                    RootFingerprint = path?.MasterFingerprint,
                    AccountKeyPath = path?.KeyPath,
                    AccountKey = result.Item1.GetExtPubKeys().ElementAt(i).GetWif(parser.Network)
                })
                .ToArray() ?? new AccountKeySettings[result.Item1.GetExtPubKeys().Count()];
            return derivationSchemeSettings;
        }

        var strategy = parser.Parse(derivationScheme);
        return new DerivationSchemeSettings(strategy, network);
    }

    private string GetNBXplorerSuffix(string scriptType, string descriptor = null)
    {
        switch (scriptType.ToLower())
        {
            case "wpkh":
                return ""; // P2WPKH - no suffix
            case "pkh":
                return "-[legacy]"; // P2PKH
            case "sh":
                // For BTC, check if it's sh(wpkh(...)) for P2SH-P2WPKH
                if (descriptor != null && descriptor.Contains("sh(wpkh("))
                    return "-[p2sh]";
                else
                    return "-[p2sh]"; // Generic P2SH
            case "tr":
                return "-[taproot]"; // P2TR
            default:
                return null; // Indicates unsupported script type
        }
    }
}
