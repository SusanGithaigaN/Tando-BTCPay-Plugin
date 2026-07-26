using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("server/tando")]
public class UITandoSettingsController(TandoSubscriptionService subscriptionService, IStringLocalizer stringLocalizer) : Controller
{
    private IStringLocalizer StringLocalizer { get; } = stringLocalizer;

    [HttpGet]
    public async Task<IActionResult> Settings()
    {
        ViewData["ActivePage"] = "Tando";
        return View(await BuildViewModel(null));
    }

    [HttpPost]
    public async Task<IActionResult> Settings(TandoSettingsViewModel model)
    {
        ViewData["ActivePage"] = "Tando";

        if (string.IsNullOrEmpty(model.SubscriptionOfferingId))
        {
            ModelState.AddModelError(nameof(model.SubscriptionOfferingId), StringLocalizer["Select a subscription offering"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId));
        }
        await subscriptionService.SaveSettings(new TandoSettings { SubscriptionOfferingId = model.SubscriptionOfferingId });
        TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Tando settings updated"].Value;
        return RedirectToAction(nameof(Settings));
    }

    private async Task<TandoSettingsViewModel> BuildViewModel(string? selectedOfferingId)
    {
        var settings = await subscriptionService.GetSettings();
        var offerings = await subscriptionService.GetAllOfferings();
        string? createOfferingUrl = null;
        var currentStoreId = HttpContext.GetUserPrefsCookie().CurrentStoreId;
        if (!string.IsNullOrEmpty(currentStoreId))
            createOfferingUrl = Url.Action("CreateOffering", "UIOffering", new { area = "Subscriptions", storeId = currentStoreId });

        return new TandoSettingsViewModel
        {
            SubscriptionOfferingId = selectedOfferingId ?? settings.SubscriptionOfferingId,
            Offerings = offerings.Select(o => new SelectListItem($"{o.Name} ({o.StoreName})", o.Id)).ToList(),
            CreateOfferingUrl = createOfferingUrl
        };
    }
}