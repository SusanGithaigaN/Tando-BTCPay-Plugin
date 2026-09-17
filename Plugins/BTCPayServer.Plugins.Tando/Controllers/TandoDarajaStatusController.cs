using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/")]
[Authorize(Policy = Policies.CanModifyStoreSettingsUnscoped, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public class TandoDarajaStatusController(DarajaMobileNumberValidationService darajaValidation) : Controller
{
    [HttpGet("daraja/status")]
    public async Task<IActionResult> DarajaStatus()
    {
        var settings = await darajaValidation.GetSettings();
        return Ok(
            new
            {
                configured = settings.IsConfigured(),
                sandbox = settings.UseSandbox,
                shortCode = settings.IsConfigured() ? settings.ShortCode : null,
                consumerKeySet = !string.IsNullOrWhiteSpace(settings.ConsumerKey),
                consumerSecretSet = !string.IsNullOrWhiteSpace(settings.ConsumerSecret),
            }
        );
    }

}
