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
    // GET
    public async Task<IActionResult> ImportWallets()
    {
        return View(new PluginPageViewModel { Data = "hello world" });
    }
}

public class PluginPageViewModel
{
    public string Data { get; set; }
}
