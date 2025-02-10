using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Aqua.BTCPayPlugin.Services;
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NBitcoin;
using Newtonsoft.Json;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/{storeId}/aqua")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class AquaController : Controller
{
    private readonly SamrockProtocolHostedService _samrockProtocolService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerProvider;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly StoreRepository _storeRepository;
    private readonly EventAggregator _eventAggregator;

    public AquaController(
        SamrockProtocolHostedService samrockProtocolService,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerProvider,
        BTCPayWalletProvider walletProvider,
        StoreRepository storeRepository,
        EventAggregator eventAggregator)
    {
        _samrockProtocolService = samrockProtocolService;
        _handlers = handlers;
        _explorerProvider = explorerProvider;
        _walletProvider = walletProvider;
        _storeRepository = storeRepository;
        _eventAggregator = eventAggregator;
    }

    private StoreData CurrentStore => HttpContext.GetStoreData();

    [HttpGet("import-wallets")]
    public IActionResult ImportWallets()
    {
        var model = new ImportWalletsViewModel
        {
            BtcChain = true,
            BtcLn = false,
            LiquidChain = false,
            LiquidSupportedOnServer = _explorerProvider.GetNetwork("LBTC") != null
        };
        return View(model);
    }

    [HttpPost("import-wallets")]
    public IActionResult ImportWallets(ImportWalletsViewModel model)
    {
        if (!ModelState.IsValid || (!model.BtcChain && !model.BtcLn && !model.LiquidChain))
        {
            ModelState.AddModelError("", "At least one wallet type must be selected.");
            return View(model);
        }

        var otp = GenerateOtp();
        model.StoreId = CurrentStore.Id;
        model.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
        model.QrCode = GenerateSetupUrl(model, otp);

        _samrockProtocolService.Add(otp, model);
        return View(model);
    }

    [HttpPost("samrockprotocol")]
    public async Task<IActionResult> SamrockProtocol(string otp)
    {
        if (!_samrockProtocolService.TryGet(CurrentStore.Id, otp, out var importWalletModel))
        {
            return NotFound(new { error = "OTP not found or expired." });
        }

        var jsonField = Request.Form["json"];
        if (!TryDeserializeJson(jsonField, out SamrockProtocolSetupModel setupModel))
        {
            return BadRequest(new { error = "Invalid JSON format." });
        }

        var result = new SamrockProtocolResultModel();

        if (setupModel.BtcChain != null)
        {
            await SetupWalletAsync(setupModel.BtcChain.ToString(), setupModel.BtcChain.DerivationPath,
                "BTC", SamrockProtocolKeys.BtcChain, result);
        }
        if (setupModel.LiquidChain != null)
        {
            await SetupWalletAsync(setupModel.LiquidChain.ToString(), setupModel.LiquidChain.DerivationPath,
                "LBTC", SamrockProtocolKeys.LiquidChain, result);
        }
        // TODO: Add support for lightning

        _samrockProtocolService.Remove(CurrentStore.Id, otp);
        return Ok(new { message = "Wallet setup successfully.", result });
    }

    private async Task SetupWalletAsync(string derivationScheme, string derivationPath, string networkCode, SamrockProtocolKeys key, SamrockProtocolResultModel result)
    {
        if (string.IsNullOrEmpty(derivationScheme) || _explorerProvider.GetNetwork(networkCode) == null)
        {
            result.Results[key] = new SamrockProtocolResultModel.Item { Success = false, Error = $"{networkCode} is not supported on this server." };
            return;
        }

        try
        {
            var network = _explorerProvider.GetNetwork(networkCode);
            var strategy = ParseDerivationStrategy(derivationScheme, network);
            strategy.AccountKeySettings[0].AccountKeyPath = new KeyPath(derivationPath);

            var wallet = _walletProvider.GetWallet(network);
            await wallet.TrackAsync(strategy.AccountDerivation);

            ConfigureStorePaymentMethod(strategy, network);

            result.Results[key] = new SamrockProtocolResultModel.Item { Success = true };
        }
        catch (Exception ex)
        {
            result.Results[key] = new SamrockProtocolResultModel.Item { Success = false, Error = ex.Message };
        }
    }

    private void ConfigureStorePaymentMethod(DerivationSchemeSettings strategy, BTCPayNetwork network)
    {
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        CurrentStore.SetPaymentMethodConfig(_handlers[paymentMethodId], strategy);

        var storeBlob = CurrentStore.GetStoreBlob();
        storeBlob.SetExcluded(paymentMethodId, false);
        storeBlob.PayJoinEnabled = false;
        CurrentStore.SetStoreBlob(storeBlob);

        _storeRepository.UpdateStore(CurrentStore);
        _eventAggregator.Publish(new WalletChangedEvent { WalletId = new WalletId(CurrentStore.Id, network.CryptoCode) });
    }

    private static bool TryDeserializeJson(string json, out SamrockProtocolSetupModel model)
    {
        try
        {
            model = JsonConvert.DeserializeObject<SamrockProtocolSetupModel>(json);
            return true;
        }
        catch
        {
            model = null;
            return false;
        }
    }

    private string GenerateOtp() => new(Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Take(21).ToArray());

    private string GenerateSetupUrl(ImportWalletsViewModel model, string otp)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var setupParams = string.Join(",", new[]
        {
            model.BtcChain ? "btc-chain" : null,
            model.LiquidChain ? "liquid-chain" : null,
            model.BtcLn ? "btc-ln" : null
        }.Where(p => p != null));

        return $"{baseUrl}/plugins/{model.StoreId}/aqua/samrockprotocol?setup={Uri.EscapeDataString(setupParams)}&otp={Uri.EscapeDataString(otp)}";
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

public class ImportWalletsViewModel
{
    public string StoreId { get; set; }
    [DisplayName("Bitcoin")]
    public bool BtcChain { get; set; }
    [DisplayName("Lightning")]
    public bool BtcLn { get; set; }
    [DisplayName("Liquid Bitcoin")]
    public bool LiquidChain { get; set; }
    public string QrCode { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool LiquidSupportedOnServer { get; set; }
}

public class SamrockProtocolSetupModel
{
    public BtcChainSetupModel BtcChain { get; set; }
    public BtcLnSetupModel BtcLn { get; set; }
    public LiquidChainSetupModel LiquidChain { get; set; }

    public static string TypeToString(AddressTypes type)
    {
        switch (type)
        {
            case AddressTypes.P2TR:
                return "-[taproot]";
            case AddressTypes.P2WPKH:
                return "";
            case AddressTypes.P2SH_P2WPKH:
                return "-[p2sh]";
            case AddressTypes.P2PKH:
                return "-[legacy]";
            default:
                throw new InvalidOperationException();
        }
    }
}

public class BtcChainSetupModel
{
    public string Xpub { get; set; }
    public string DerivationPath { get; set; }
    public AddressTypes Type { get; set; }

    public override string ToString()
    {
        return $"{Xpub}{SamrockProtocolSetupModel.TypeToString(Type)}";
    }
}

public class LiquidChainSetupModel
{
    public string Xpub { get; set; }
    public string DerivationPath { get; set; }
    public AddressTypes Type { get; set; }
    public string BlindingKey { get; set; }

    public override string ToString()
    {
        return $"{Xpub}{SamrockProtocolSetupModel.TypeToString(Type)}-[slip77={BlindingKey}]";
    }
}

public class BtcLnSetupModel
{
    public bool UseLiquidBoltz { get; set; }
}

public enum AddressTypes
{
    P2PKH, P2SH_P2WPKH, P2WPKH, P2TR
}

public class SamrockProtocolResultModel
{
    public Dictionary<SamrockProtocolKeys, Item> Results { get; init; } = new();

    public class Item
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}

public enum SamrockProtocolKeys
{
    BtcChain,
    BtcLn,
    LiquidChain
}
