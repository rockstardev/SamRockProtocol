using System;
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

namespace SamRockProtocol.Controllers;

[Route("~/plugins/{storeId}/samrock")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ProtocolController(
    SamrockProtocolHostedService samrockProtocolService,
    PaymentMethodHandlerDictionary handlers,
    ExplorerClientProvider explorerProvider,
    BTCPayWalletProvider walletProvider,
    StoreRepository storeRepository,
    EventAggregator eventAggregator,
    ILogger<ProtocolController> logger,
    BoltzWrapper boltzWrapper)
    : Controller
{
    [FromRoute]
    public string StoreId { get; set; }

    [AllowAnonymous]
    [RateLimitsFilter("SamRockProtocol", Scope = RateLimitsScope.RemoteAddress)]
    [HttpPost("protocol")]
    public async Task<IActionResult> SamrockProtocol()
    {
        var otp = Request.Query["otp"].ToString();
        if (string.IsNullOrEmpty(otp) || !samrockProtocolService.TryGet(otp, out var importWalletModel))
            return NotFound(new SamRockProtocolResponse(false, "OTP not found or expired.", null));

        var storeData = await storeRepository.FindStore(importWalletModel.StoreId);
        if (storeData == null)
            return NotFound(new SamRockProtocolResponse(false, "Store not found.", null));

        var jsonField = Request.Form["json"];
        var setupModel = UtilJson.Parse<SamRockProtocolRequest>(jsonField, out var ex);
        if (setupModel == null)
            return BadRequest(new SamRockProtocolResponse(false, "Invalid JSON format.", ex));

        // Only allow to setup payment methods that were selected in the initial import step
        if (!importWalletModel.BtcChain && setupModel.BTC != null)
            setupModel.BTC = null;
        if (!importWalletModel.BtcLn && setupModel.BTCLN != null)
            setupModel.BTCLN = null;
        if (!importWalletModel.LiquidChain && setupModel.LBTC != null)
            setupModel.LBTC = null;
        
        logger.LogInformation("SamRockProtocol request initiated. setupModel={SetupModel}", setupModel.ToJson());
        return await processSamRockProtocolRequest(setupModel, storeData, otp);
    }

    private async Task<IActionResult> processSamRockProtocolRequest(SamRockProtocolRequest setupModel, StoreData storeData, string otp)
    {
        var result = new SamrockProtocolSetupResponse();
        if (setupModel.BTC != null && !string.IsNullOrEmpty(setupModel.BTC.Descriptor))
        {
            var key = SamrockProtocolKeys.BTC;
            try
            {
                // Parse output descriptor format and convert to NBXplorer format
                // Input: wpkh([8f681564/84'/0'/0']xpub...xxx/0/*)#8m68c9t7
                var descriptor = setupModel.BTC.Descriptor;

                // Extract script type, fingerprint, derivation path, xpub, and address derivation suffix
                var match = Regex.Match(descriptor, @"^(\w+)\(\[([a-fA-F0-9]{8})/([^\]]+)\](xpub[^/\)]+)(/[^\)]+)?\)(?:#[a-zA-Z0-9]+)?");
                if (!match.Success)
                {
                    result.Results[key] = new SamRockProtocolResponse(false,
                        "Invalid BTC descriptor format - could not parse script type, fingerprint, derivation path, and xpub.", null);
                }
                else
                {
                    var scriptType = match.Groups[1].Value;
                    var fingerprint = match.Groups[2].Value;
                    var basePath = match.Groups[3].Value;
                    var xpub = match.Groups[4].Value;
                    var addressSuffix = match.Groups[5].Value; // e.g., "/0/*"

                    // TODO: Check whether you need to combine base derivation path with address derivation suffix
                    var derivationPath = basePath; // + (addressSuffix ?? "");

                    // Convert script type to NBXplorer suffix format
                    var suffix = GetNBXplorerSuffix(scriptType, descriptor);
                    if (suffix == null)
                    {
                        result.Results[key] = new SamRockProtocolResponse(false, $"Unsupported BTC script type: {scriptType}", null);
                    }
                    else
                    {
                        // Create NBXplorer format derivation scheme
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
            var key = SamrockProtocolKeys.LBTC;

            if (explorerProvider.GetNetwork("LBTC") != null)
            {
                try
                {
                    // Parse LBTC output descriptor format and convert to NBXplorer format
                    // Input: ct(slip77(blinding_key),elsh(wpkh([fingerprint/path]xpub...)))
                    var descriptor = setupModel.LBTC.Descriptor;

                    // Extract slip77 blinding key, script type, fingerprint, derivation path, xpub, and address derivation suffix
                    var match = Regex.Match(descriptor,
                        @"^ct\(slip77\(([a-fA-F0-9]{64})\),elsh\((\w+)\(\[([a-fA-F0-9]{8})/([^\]]+)\](xpub[^/\)]+)(/[^\)]+)?\)\)\)(?:#[a-zA-Z0-9]+)?");
                    if (!match.Success)
                    {
                        result.Results[key] = new SamRockProtocolResponse(false,
                            "Invalid LBTC descriptor format - could not parse slip77, script type, fingerprint, derivation path, and xpub.", null);
                    }
                    else
                    {
                        var blindingKey = match.Groups[1].Value;
                        var scriptType = match.Groups[2].Value;
                        var fingerprint = match.Groups[3].Value;
                        var basePath = match.Groups[4].Value;
                        var xpub = match.Groups[5].Value;
                        var addressSuffix = match.Groups[6].Value; // e.g., "/0/*"

                        // TODO: Check whether you need to combine base derivation path with address derivation suffix
                        var derivationPath = basePath; // + (addressSuffix ?? "");

                        // Convert script type to NBXplorer suffix format
                        //var suffix = GetNBXplorerSuffix(scriptType, descriptor);
                        var suffix = "-[p2sh]"; // For LBTC at the moment of launch, we assume P2SH_P2WPKH
                        if (suffix == null)
                        {
                            result.Results[key] = new SamRockProtocolResponse(false, $"Unsupported LBTC script type: {scriptType}", null);
                        }
                        else
                        {
                            // Create NBXplorer format derivation scheme for LBTC: xpub + suffix + slip77
                            var derivationScheme = $"{xpub}{suffix}-[slip77={blindingKey}]";
                            await SetupWalletAsync(derivationScheme, fingerprint, derivationPath, "LBTC", storeData, key, result);
                        }
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
                await boltzWrapper.SetBoltz(StoreId, setupModel.BTCLN.LBTC.Descriptor, result);
            }
            else
            {
                result.Results[SamrockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false,
                    $"Lightning setup configured with unknown type: {setupModel.BTCLN.Type}", null);
            }
        }

        // TODO: If both LBTC is set and BtcLn is set, need to generate as many addresses for LiquidChain
        // as we have in setupModel.BtcLn.LiquidAddresses.Length to reserve them

        var allSuccess = result.Results.Values.All(a => a.Success);
        string errorMessage = null;
        if (!allSuccess && result.Results[SamrockProtocolKeys.BTC_LN] != null)
        {
            var res = result.Results[SamrockProtocolKeys.BTC_LN];
            errorMessage = res.Message;
        }

        samrockProtocolService.OtpUsed(otp, allSuccess, errorMessage);

        logger.LogInformation("SamRockProtocol setup completed. setupModel={SetupModel} result={Result}", setupModel.ToJson(), result.ToJson());

        return Ok(new
        {
            Success = true,
            Message = "Wallet setup successfully.",
            Result = result
        });
    }

    private async Task SetupWalletAsync(string derivationScheme, string fingerprint, string derivationPath, string networkCode,
        StoreData storeData, SamrockProtocolKeys key, SamrockProtocolSetupResponse result)
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
