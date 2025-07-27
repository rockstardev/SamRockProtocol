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
        if (storeData == null) return NotFound(new SamRockProtocolResponse(false, "Store not found.", null));

        var jsonField = Request.Form["json"];
        var setupModel = SamRockProtocolRequest.Parse(jsonField, out var ex);
        if (setupModel == null)
            return BadRequest(new SamRockProtocolResponse(false, "Invalid JSON format.", ex));

        var result = new SamrockProtocolSetupResponse();

        if (setupModel.BTC != null && !string.IsNullOrEmpty(setupModel.BTC.Descriptor))
            await SetupWalletAsync(setupModel.BTC.Descriptor, null, null, "BTC", storeData, SamrockProtocolKeys.BtcChain, result);

        if (setupModel.LBTC != null && !string.IsNullOrEmpty(setupModel.LBTC.Descriptor))
        {
            if (explorerProvider.GetNetwork("LBTC") != null)
                await SetupWalletAsync(setupModel.LBTC.Descriptor, null, null, "LBTC", storeData, SamrockProtocolKeys.LiquidChain, result);
            else
                result.Results[SamrockProtocolKeys.LiquidChain] = new SamRockProtocolResponse(true,
                    "Warning: LBTC is not available on server, ignoring sent data", null);
        }

        if (setupModel.BTCLN != null)
        {
            result.Results[SamrockProtocolKeys.BtcLn] = new SamRockProtocolResponse(true,
                $"Lightning setup configured with type: {setupModel.BTCLN.Type}", null);
        }

        // TODO: If both LBTC is set and BtcLn is set, need to generate as many addresses for LiquidChain
        // as we have in setupModel.BtcLn.LiquidAddresses.Length to reserve them

        var allSuccess = result.Results.Values.All(a => a.Success);
        samrockProtocolService.OtpUsed(otp, allSuccess);
        return Ok(new
        {
            Success = true,
            Message = "Wallet setup successfully.",
            Result = result
        });
    }

    private Task SetupLightning(BtcLnSetupModel setupModelBtcLn, SamrockProtocolSetupResponse result)
    {
        return boltzWrapper.SetBoltz(StoreId, setupModelBtcLn.CtDescriptor, result);
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
}
