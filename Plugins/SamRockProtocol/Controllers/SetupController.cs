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
using SamRockProtocol.Models;

namespace SamRockProtocol.Controllers;

[Route("~/plugins/{storeId}/samrock")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SetupController : Controller
{
    private readonly ExplorerClientProvider _explorerProvider;
    private readonly SamRockProtocolHostedService _samrockProtocolService;
    private readonly OtpService _otpService;

    public SetupController(
        SamRockProtocolHostedService samrockProtocolService,
        ExplorerClientProvider explorerProvider,
        OtpService otpService)
    {
        _samrockProtocolService = samrockProtocolService;
        _explorerProvider = explorerProvider;
        _otpService = otpService;
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
    [RateLimitsFilter("SamRockProtocol", Scope = RateLimitsScope.RemoteAddress)]
    public IActionResult ImportWallets(ImportWalletsViewModel model)
    {
        if (!ModelState.IsValid || (!model.Btc && !model.BtcLn && !model.Lbtc))
        {
            ModelState.AddModelError("", "At least one wallet type must be selected.");
            return View(model);
        }

        model.StoreId = StoreId;
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var created = _otpService.CreateOtp(StoreId, model.Btc, model.BtcLn, model.Lbtc, baseUrl);

        model.Otp = created.Otp;
        model.Expires = created.Expires;
        model.QrCode = created.QrCode;

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
}
