using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using NicolasDorier.RateLimits;
using SamRockProtocol.Services;

namespace SamRockProtocol.Controllers;

[Route("~/plugins/{storeId}/samrock")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SetupController : Controller
{
    private readonly ExplorerClientProvider _explorerProvider;
    private readonly SamrockProtocolHostedService _samrockProtocolService;

    public SetupController(
        SamrockProtocolHostedService samrockProtocolService,
        ExplorerClientProvider explorerProvider)
    {
        _samrockProtocolService = samrockProtocolService;
        _explorerProvider = explorerProvider;
    }

    [FromRoute]
    public string StoreId { get; set; }

    [HttpGet("TestJson")]
    public IActionResult TestJson()
    {
        return View();
    }

    [HttpGet("ImportWallets")]
    public IActionResult ImportWallets()
    {
        var model = new ImportWalletsViewModel
        {
            Btc = true,
            BtcLn = true,
            Lbtc = true,
            LiquidSupportedOnServer = _explorerProvider.GetNetwork("LBTC") != null
        };
        return View(model);
    }

    [HttpPost("ImportWallets")]
    public IActionResult ImportWallets(ImportWalletsViewModel model)
    {
        if (!ModelState.IsValid || (!model.Btc && !model.BtcLn && !model.Lbtc))
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

    [HttpPost("ImportWalletsClear")]
    public IActionResult ImportWalletsClear(ImportWalletsViewModel model)
    {
        if (!string.IsNullOrEmpty(model.Otp))
        {
            _samrockProtocolService.Remove(model.Otp);
        }

        return RedirectToAction(nameof(ImportWallets), new { storeId = model.StoreId });
    }

    [HttpGet("ImportWalletsStatus")]
    public IActionResult ImportWalletsStatus()
    {
        var otp = Request.Query["otp"].ToString();
        string res = null;
        var otpStatus = _samrockProtocolService.OtpStatus(otp);
        if (otpStatus != null)
            res = _samrockProtocolService.OtpStatus(otp).ImportSuccessful.ToString().ToLowerInvariant();
        return Ok(new { status = res });
    }

    [HttpGet("ImportResult")]
    public IActionResult ImportResult(string otp)
    {
        var otpStatus = _samrockProtocolService.OtpStatus(otp);
        ImportResultViewModel model = new ImportResultViewModel { OtpStatus = null };
        if (otpStatus != null)
        {
            model = new ImportResultViewModel { OtpStatus = otpStatus.ImportSuccessful, ErrorMessage = otpStatus.ErrorMessage };
        }

        return View(model);
    }


    private string GenerateSetupUrl(ImportWalletsViewModel model, string otp)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var setupParams = string.Join(",",
            new[] { model.Btc ? "btc-chain" : null, model.Lbtc ? "liquid-chain" : null, model.BtcLn ? "btc-ln" : null }.Where(p => p != null));
            //new[] { model.Btc ? "btc" : null, model.Lbtc ? "lbtc" : null, model.BtcLn ? "btcln" : null }.Where(p => p != null));

        return
            $"{baseUrl}/plugins/{model.StoreId}/samrock/protocol?setup={Uri.EscapeDataString(setupParams)}&otp={Uri.EscapeDataString(otp)}";
    }
}

public class ImportWalletsViewModel
{
    public string StoreId { get; set; }

    [DisplayName("Bitcoin")]
    public bool Btc { get; set; }

    [DisplayName("Lightning")]
    public bool BtcLn { get; set; }

    [DisplayName("Liquid Bitcoin")]
    public bool Lbtc { get; set; }

    public string QrCode { get; set; }
    public string Otp { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool LiquidSupportedOnServer { get; set; }
}

public class ImportResultViewModel
{
    public bool? OtpStatus { get; set; }
    public string ErrorMessage { get; set; }
}
