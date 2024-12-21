using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Aqua.BTCPayPlugin.Services;
using BTCPayServer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Client;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/{storeId}/aqua")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class AquaController(SamrockProtocolHostedService samrockProtocolHostedService,
    PaymentMethodHandlerDictionary handlers,
    ExplorerClientProvider explorerProvider,
    BTCPayWalletProvider walletProvider,
    StoreRepository storeRepo,
    EventAggregator eventAggregator) : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();
    
    [HttpGet("import-wallets")]
    public async Task<IActionResult> ImportWallets()
    {
        return View(new ImportWalletsViewModel { BtcChain = true, BtcLn = false, LiquidChain = false });
    }
    
    [HttpPost("import-wallets")]
    public async Task<IActionResult> ImportWallets(ImportWalletsViewModel model)
    {
        // TODO: Generate nonce that accepts derivations from Aqua wallet and applies them for this store
        if (!model.BtcChain && !model.BtcLn && !model.LiquidChain)
        {
            ModelState.AddModelError("", "At least one wallet type must be selected");
            return View(model);
        }
        
        var random21Charstring = new string(Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Take(21).ToArray());
        model.StoreId = StoreData.Id;
        model.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
        
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var setupParams = setupParamsFromModel(model);
        var url = $"{baseUrl}/plugins/{model.StoreId}/aqua/samrockprotocol?setup=" +
                  $"{setupParams}"+
                  $"&otp={random21Charstring}";

        model.QrCode = url;
        
        samrockProtocolHostedService.Add(random21Charstring, model);
        return View(model);
    }
    
    private string setupParamsFromModel(ImportWalletsViewModel model)
    {
        return $"{(model.BtcChain ? "btc-chain," : "")}{(model.LiquidChain ? "liquid-chain," : "")}{(model.BtcLn ? "btc-ln," : "")}";
    }

    

    [HttpPost("samrockprotocol")]
    public async Task<IActionResult> SamrockProtocol(string otp, [FromBody]SamrockProtocolModel json)
    {
        var importWalletModel = samrockProtocolHostedService.Get(StoreData.Id, otp);
        if (importWalletModel == null)
        {
            return NotFound();
        }
        
        // only setup onchain for now as proof of concept
        if (importWalletModel.BtcChain)
        {
            var network = explorerProvider.GetNetwork("BTC");

            DerivationSchemeSettings strategy = null;
            PaymentMethodId paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);

            strategy = ParseDerivationStrategy(json.BtcChain, network);
            strategy.Source = "ManualDerivationScheme";

            var wallet = walletProvider.GetWallet(network);
            await wallet.TrackAsync(strategy.AccountDerivation);
            StoreData.SetPaymentMethodConfig(handlers[paymentMethodId], strategy);
            var storeBlob = StoreData.GetStoreBlob();
            storeBlob.SetExcluded(paymentMethodId, false);
            storeBlob.PayJoinEnabled = false;
            StoreData.SetStoreBlob(storeBlob);

            await storeRepo.UpdateStore(StoreData);
            eventAggregator.Publish(
                new WalletChangedEvent { WalletId = new WalletId(StoreData.Id, network.CryptoCode) });
        }

        return Ok();
    }
    
    
    // TODO: Copied from BTCPayServer/Controllers/UIStoresController.cs, integrate together
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
            derivationSchemeSettings.AccountKeySettings = result.Item2?.Select((path, i) => new AccountKeySettings()
            {
                RootFingerprint = path?.MasterFingerprint,
                AccountKeyPath = path?.KeyPath,
                AccountKey = result.Item1.GetExtPubKeys().ElementAt(i).GetWif(parser.Network)
            }).ToArray() ?? new AccountKeySettings[result.Item1.GetExtPubKeys().Count()];
            return derivationSchemeSettings;
        }

        var strategy = parser.Parse(derivationScheme);
        return new DerivationSchemeSettings(strategy, network);
    }
}

public class ImportWalletsViewModel
{
    public string StoreId { get; set; }
    public bool BtcChain { get; set; }
    public bool BtcLn { get; set; }
    public bool LiquidChain { get; set; }
    public string QrCode { get; set; }
    public DateTimeOffset Expires { get; set; }
}

public class SamrockProtocolModel
{
    public string BtcChain { get; set; }
    public string BtcLn { get; set; }
    public string LiquidChain { get; set; }
}
