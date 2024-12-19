using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aqua.BTCPayPlugin.Services;
using BTCPayServer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Client;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/{storeId}/aqua")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class AquaController(SamrockProtocolHostedService samrockProtocolHostedService) : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();
    
    [HttpGet("import-wallets")]
    public async Task<IActionResult> ImportWallets()
    {
        return View(new ImportWalletsViewModel { BtcOnchain = true, BtcLightning = false, LiquidOnchain = false });
    }
    
    [HttpPost("import-wallets")]
    public async Task<IActionResult> ImportWallets(ImportWalletsViewModel model)
    {
        // TODO: Generate nonce that accepts derivations from Aqua wallet and applies them for this store
        if (!model.BtcOnchain && !model.BtcLightning && !model.LiquidOnchain)
        {
            ModelState.AddModelError("", "At least one wallet type must be selected");
            return View(model);
        }
        
        var random21Charstring = new string(Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Take(21).ToArray());
        model.StoreId = StoreData.Id;
        model.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
        
        samrockProtocolHostedService.Add(random21Charstring, model);
        
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var setupParams = $"{(model.BtcOnchain ? "btc-chain," : "")}{(model.LiquidOnchain ? "liquid-chain," : "")}{(model.BtcLightning ? "btc-ln," : "")}";
        var url = $"{baseUrl}/plugins/{model.StoreId}/aqua/samrockprotocol?setup=" +
                  $"{setupParams}"+
                  $"&otp={random21Charstring}";

        model.QrCode = url;
        return View(model);
    }

    

    [HttpPost("samrockprotocol")]
    public async Task<IActionResult> SamrockProtocol(string otp, string setup)
    {
        var importWalletModel = samrockProtocolHostedService.Get(StoreData.Id, otp);
        if (importWalletModel == null)
        {
            return NotFound();
        }
        
        throw new NotImplementedException();
    }
}

public class ImportWalletsViewModel
{
    public string StoreId { get; set; }
    public bool BtcOnchain { get; set; }
    public bool BtcLightning { get; set; }
    public bool LiquidOnchain { get; set; }
    public string QrCode { get; set; }
    public DateTimeOffset Expires { get; set; }
}

public class SamrockProtocolModel
{
    public string Samson { get; set; }
    public string Rockstar { get; set; }
}
