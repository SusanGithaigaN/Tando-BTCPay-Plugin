using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITandoSettingsController(TandoSubscriptionService subscriptionService, StoreRepository storeRepository, IStringLocalizer stringLocalizer) : Controller
{
    private IStringLocalizer StringLocalizer { get; } = stringLocalizer;

    [HttpGet("~/server/services/tando")]
    public async Task<IActionResult> Settings()
    {
        return View(await BuildViewModel(null));
    }

    [HttpPost]
    public async Task<IActionResult> Settings(TandoSettingsViewModel model)
    {
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
            createOfferingUrl = Url.Action("CreateApp", "UIApps", new { storeId = currentStoreId, appType = "Subscriptions" });

        return new TandoSettingsViewModel
        {
            SubscriptionOfferingId = selectedOfferingId ?? settings.SubscriptionOfferingId,
            Offerings = offerings
                .Select(o => new SelectListItem($"{o.Name} ({o.StoreName})", o.Id))
                .ToList(),
            CreateOfferingUrl = createOfferingUrl
        };
    }
}
