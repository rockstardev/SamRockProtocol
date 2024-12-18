using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Client;
using BTCPayServer.Abstractions.Constants;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/{storeId}/aqua")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class AquaController() : Controller
{
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
        
        SamrockImportDictionary.Add(random21Charstring, model);
        
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var url = $"{baseUrl}/plugins/{model.StoreId}/aqua/samrockprotocol?setup=" +
                  $"{(model.BtcOnchain ? "btc-chain," : "")}{(model.LiquidOnchain ? "liquid-chain," : "")}{(model.BtcLightning ? "btc-ln," : "")}&otp={random21Charstring}";

        model.QrCode = url;
        return View(model);
    }

    public static Dictionary<string, ImportWalletsViewModel> SamrockImportDictionary = new();

    [HttpPost("samrockprotocol")]
    public async Task<IActionResult> SamrockProtocol(ImportWalletsViewModel model)
    {
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
}

public class SamrockProtocolModel
{
    public string Samson { get; set; }
    public string Rockstar { get; set; }
}
