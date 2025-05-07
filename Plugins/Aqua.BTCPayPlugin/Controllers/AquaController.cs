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
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using NicolasDorier.RateLimits;
using BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/{storeId}/aqua")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class AquaController : Controller
{
    private readonly EventAggregator _eventAggregator;
    private readonly ExplorerClientProvider _explorerProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly SamrockProtocolHostedService _samrockProtocolService;
    private readonly StoreRepository _storeRepository;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AquaController> _logger;

    public AquaController(
        SamrockProtocolHostedService samrockProtocolService,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerProvider,
        BTCPayWalletProvider walletProvider,
        StoreRepository storeRepository,
        EventAggregator eventAggregator,
        IServiceProvider serviceProvider,
        ILogger<AquaController> logger)
    {
        _samrockProtocolService = samrockProtocolService;
        _handlers = handlers;
        _explorerProvider = explorerProvider;
        _walletProvider = walletProvider;
        _storeRepository = storeRepository;
        _eventAggregator = eventAggregator;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [FromRoute]
    public string StoreId { get; set; }

    [HttpGet("testjson")]
    public IActionResult TestJson()
    {
        return View();
    }

    [HttpGet("import-wallets")]
    public IActionResult ImportWallets()
    {
        var model = new ImportWalletsViewModel
        {
            BtcChain = true,
            BtcLn = true,
            LiquidChain = true,
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

        model.Otp = OtpGenerator.Generate();
        model.StoreId = StoreId;
        model.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
        model.QrCode = GenerateSetupUrl(model, model.Otp);

        _samrockProtocolService.Add(model.Otp, model);
        return View(model);
    }

    [HttpGet("import-wallets/status")]
    public IActionResult ImportWalletStatus()
    {
        var otp = Request.Query["otp"].ToString();
        return Ok(new { status = _samrockProtocolService.OtpStatus(otp)?.ToString().ToLowerInvariant() });
    }

    [HttpGet("import-result")]
    public IActionResult ImportResult(string otp)
    {
        var otpStatus = _samrockProtocolService.OtpStatus(otp);
        var model = new ImportResultViewModel { OtpStatus = otpStatus };

        return View(model);
    }


    // maybe best to move it to a separate controller
    [AllowAnonymous]
    [RateLimitsFilter("SamrockProtocol", Scope = RateLimitsScope.RemoteAddress)]
    [HttpPost("samrockprotocol")]
    public async Task<IActionResult> SamrockProtocol()
    {
        var otp = Request.Query["otp"].ToString();
        if (string.IsNullOrEmpty(otp) || !_samrockProtocolService.TryGet(otp, out var importWalletModel))
            return NotFound(new SamrockProtocolResponse(false, "OTP not found or expired.", null));

        var storeData = await _storeRepository.FindStore(importWalletModel.StoreId);
        if (storeData == null) return NotFound(new SamrockProtocolResponse(false, "Store not found.", null));

        var jsonField = Request.Form["json"];
        var setupModel = TryDeserializeJson(jsonField, out var ex);
        if (setupModel == null) return BadRequest(new SamrockProtocolResponse(false, "Invalid JSON format.", ex));

        var result = new SamrockProtocolSetupResponse();

        if (setupModel.BtcChain != null)
            await SetupWalletAsync(setupModel.BtcChain.ToString(), setupModel.BtcChain.Fingerprint,
                setupModel.BtcChain.DerivationPath, "BTC", storeData, SamrockProtocolKeys.BtcChain, result);
        if (setupModel.LiquidChain != null)
            await SetupWalletAsync(setupModel.LiquidChain.ToString(), setupModel.LiquidChain.Fingerprint,
                setupModel.LiquidChain.DerivationPath, "LBTC", storeData, SamrockProtocolKeys.LiquidChain, result);
        if (setupModel.BtcLn != null)
            await SetupLightning(setupModel.BtcLn, result);

        var allSuccess = result.Results.Values.All(a => a.Success);
        _samrockProtocolService.OtpUsed(otp, allSuccess);
        return Ok(new
        {
            Success = true,
            Message = "Wallet setup successfully.",
            Result = result
        });
    }

    private async Task SetupLightning(BtcLnSetupModel setupModelBtcLn, SamrockProtocolSetupResponse result)
    {
        // var isBoltzPluginLoaded = _serviceProvider.GetService<BoltzExchangerService>() != null;
        // if (!isBoltzPluginLoaded)
        // {
        //     result.Results[SamrockProtocolKeys.BtcLn] = new SamrockProtocolResponse(false, "Boltz Exchanger Plugin is required but not loaded.", null);
        //     return;
        // }

        // Only proceed if UseLiquidBoltz is true and addresses are provided
        if (!setupModelBtcLn.UseLiquidBoltz || setupModelBtcLn.LiquidAddresses == null || !setupModelBtcLn.LiquidAddresses.Any())
        {
            result.Results[SamrockProtocolKeys.BtcLn] = new SamrockProtocolResponse(false,
                "Liquid Boltz setup requested but required data (UseLiquidBoltz=true, LiquidAddresses) is missing.", null);
            return;
        }

        try
        {
            var storeData = await _storeRepository.FindStore(StoreId);
            if (storeData == null)
            {
                result.Results[SamrockProtocolKeys.BtcLn] = new SamrockProtocolResponse(false, "Store not found.", null);
                return;
            }

            // Construct the connection string
            var addresses = string.Join(",", setupModelBtcLn.LiquidAddresses);
            var connectionString = $"type=boltzexchanger;apiurl=https://api.boltz.exchange/;swap-addresses={addresses}";

            var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");

            var paymentMethod = new LightningPaymentMethodConfig { ConnectionString = connectionString };

            // Update the settings in the store
            storeData.SetPaymentMethodConfig(_handlers[paymentMethodId], paymentMethod);

            // Save the store
            await _storeRepository.UpdateStore(storeData);

            result.Results[SamrockProtocolKeys.BtcLn] = new SamrockProtocolResponse(true, "Lightning payment method configured for Boltz Exchanger.", null);
        }
        catch (Exception ex)
        {
            result.Results[SamrockProtocolKeys.BtcLn] = new SamrockProtocolResponse(false, "An error occurred while configuring Lightning settings.", ex);
        }
    }

    private async Task SetupWalletAsync(string derivationScheme, string fingerprint, string derivationPath, string networkCode,
        StoreData storeData, SamrockProtocolKeys key, SamrockProtocolSetupResponse result)
    {
        if (string.IsNullOrEmpty(derivationScheme) || _explorerProvider.GetNetwork(networkCode) == null)
        {
            result.Results[key] =
                new SamrockProtocolResponse(false, $"{networkCode} is not supported on this server.", null);
            return;
        }
        
        if (string.IsNullOrEmpty(fingerprint) || !HDFingerprint.TryParse(fingerprint, out var hdFingerprint))
        {
            result.Results[key] =
                new SamrockProtocolResponse(false, $"Invalid fingerprint for wallet supplied", null);
            return;
        }

        try
        {
            var network = _explorerProvider.GetNetwork(networkCode);
            var strategy = ParseDerivationStrategy(derivationScheme, network);
            strategy.AccountKeySettings[0].RootFingerprint = hdFingerprint;
            strategy.AccountKeySettings[0].AccountKeyPath = new KeyPath(derivationPath);

            var wallet = _walletProvider.GetWallet(network);
            await wallet.TrackAsync(strategy.AccountDerivation);

            await ConfigureStorePaymentMethod(storeData, strategy, network);

            result.Results[key] = new SamrockProtocolResponse(true, null, null);
        }
        catch (Exception ex)
        {
            result.Results[key] = new SamrockProtocolResponse(false, null, ex);
        }
    }

    private async Task ConfigureStorePaymentMethod(StoreData storeData, DerivationSchemeSettings strategy,
        BTCPayNetwork network)
    {
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        storeData.SetPaymentMethodConfig(_handlers[paymentMethodId], strategy);

        var storeBlob = storeData.GetStoreBlob();
        storeBlob.SetExcluded(paymentMethodId, false);
        storeBlob.PayJoinEnabled = false;
        storeData.SetStoreBlob(storeBlob);

        await _storeRepository.UpdateStore(storeData);
        _eventAggregator.Publish(new WalletChangedEvent { WalletId = new WalletId(storeData.Id, network.CryptoCode) });
    }

    private static SamrockProtocolRequest TryDeserializeJson(string json, out Exception parsingException)
    {
        try
        {
            var model = JsonConvert.DeserializeObject<SamrockProtocolRequest>(json);
            parsingException = null;
            return model;
        }
        catch (Exception ex)
        {
            parsingException = ex;
            return null;
        }
    }

    private string GenerateSetupUrl(ImportWalletsViewModel model, string otp)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var setupParams = string.Join(",",
            new[] { model.BtcChain ? "btc-chain" : null, model.LiquidChain ? "liquid-chain" : null, model.BtcLn ? "btc-ln" : null }.Where(p => p != null));

        return
            $"{baseUrl}/plugins/{model.StoreId}/aqua/samrockprotocol?setup={Uri.EscapeDataString(setupParams)}&otp={Uri.EscapeDataString(otp)}";
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
    public string Otp { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool LiquidSupportedOnServer { get; set; }
}

public class ImportResultViewModel
{
    public bool? OtpStatus { get; set; }
}

public class SamrockProtocolRequest
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
    public string Fingerprint { get; set; }
    public string DerivationPath { get; set; }
    public AddressTypes Type { get; set; }

    public override string ToString()
    {
        return $"{Xpub}{SamrockProtocolRequest.TypeToString(Type)}";
    }
}

public class LiquidChainSetupModel
{
    public string Xpub { get; set; }
    public string Fingerprint { get; set; }
    public string DerivationPath { get; set; }
    public AddressTypes Type { get; set; }
    public string BlindingKey { get; set; }

    public override string ToString()
    {
        return $"{Xpub}{SamrockProtocolRequest.TypeToString(Type)}-[slip77={BlindingKey}]";
    }
}

public class BtcLnSetupModel
{
    public bool UseLiquidBoltz { get; set; }
    public string[] LiquidAddresses { get; set; }
}

public enum AddressTypes
{
    P2PKH,
    P2SH_P2WPKH,
    P2WPKH,
    P2TR
}

public class SamrockProtocolSetupResponse
{
    public Dictionary<SamrockProtocolKeys, SamrockProtocolResponse> Results { get; init; } = new();
}

public class SamrockProtocolResponse
{
    public SamrockProtocolResponse(bool success, string message, Exception exception)
    {
        Success = success;
        Message = message;
        Exception = exception;
    }

    public bool Success { get; set; }
    public string Message { get; set; }
    public Exception Exception { get; set; }
}

public enum SamrockProtocolKeys
{
    BtcChain,
    BtcLn,
    LiquidChain
}
