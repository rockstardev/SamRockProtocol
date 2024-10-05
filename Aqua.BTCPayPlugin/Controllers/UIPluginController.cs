using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Aqua.BTCPayPlugin.Services;

namespace Aqua.BTCPayPlugin.Controllers;

[Route("~/plugins/aqua-btcpay-plugin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPluginController : Controller
{
    private readonly MyPluginService _PluginService;

    public UIPluginController(MyPluginService PluginService)
    {
        _PluginService = PluginService;
    }

    // GET
    public async Task<IActionResult> Index()
    {
        return View(new PluginPageViewModel { Data = await _PluginService.Get() });
    }
}

public class PluginPageViewModel
{
    public List<PluginData> Data { get; set; }
}
