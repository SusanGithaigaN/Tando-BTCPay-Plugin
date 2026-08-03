using System.Collections.Generic;
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

namespace BTCPayServer.Plugins.Tando;

[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("server/tando")]
public class UITandoSettingsController(TandoSubscriptionService subscriptionService, IStringLocalizer stringLocalizer) : Controller
{
    private IStringLocalizer StringLocalizer { get; } = stringLocalizer;

    [HttpGet]
    public async Task<IActionResult> Settings(string? offeringId = null)
    {
        ViewData["ActivePage"] = "Tando";
        return View(await BuildViewModel(offeringId, null));
    }

    [HttpPost]
    public async Task<IActionResult> Settings(TandoSettingsViewModel model)
    {
        ViewData["ActivePage"] = "Tando";
        if (string.IsNullOrEmpty(model.SubscriptionOfferingId))
        {
            ModelState.AddModelError(nameof(model.SubscriptionOfferingId), StringLocalizer["Select a subscription offering"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId));
        }

        var activePlans = await subscriptionService.GetActivePlans(model.SubscriptionOfferingId);
        if (activePlans.Length == 0)
        {
            ModelState.AddModelError(nameof(model.SubscriptionOfferingId), StringLocalizer["This offering has no active plans yet. Add at least one active plan to it before selecting it here."]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId));
        }

        if (string.IsNullOrEmpty(model.SubscriptionPlanId) || activePlans.All(p => p.Id != model.SubscriptionPlanId))
        {
            ModelState.AddModelError(nameof(model.SubscriptionPlanId), StringLocalizer["Select the plan merchants should be tied to"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId));
        }
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = model.SubscriptionOfferingId,
            SubscriptionPlanId = model.SubscriptionPlanId
        });
        TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Tando settings updated"].Value;
        return RedirectToAction(nameof(Settings));
    }

    private async Task<TandoSettingsViewModel> BuildViewModel(string? selectedOfferingId, string? selectedPlanId)
    {
        var settings = await subscriptionService.GetSettings();
        var offerings = await subscriptionService.GetAllOfferings();
        var offeringId = selectedOfferingId ?? settings.SubscriptionOfferingId;

        string? createOfferingUrl = null;
        var currentStoreId = HttpContext.GetUserPrefsCookie().CurrentStoreId;
        if (!string.IsNullOrEmpty(currentStoreId))
            createOfferingUrl = Url.Action("CreateOffering", "UIOffering", new { area = "Subscriptions", storeId = currentStoreId });

        var planItems = new List<SelectListItem>();
        if (!string.IsNullOrEmpty(offeringId))
        {
            var activePlans = await subscriptionService.GetActivePlans(offeringId);
            planItems = activePlans.Select(p => new SelectListItem($"{p.Name} - {p.Price} {p.Currency}", p.Id)).ToList();
        }
        return new TandoSettingsViewModel
        {
            SubscriptionOfferingId = offeringId,
            SubscriptionPlanId = selectedPlanId ?? settings.SubscriptionPlanId,
            Offerings = offerings.Select(o => new SelectListItem($"{o.Name} ({o.StoreName})", o.Id)).ToList(),
            Plans = planItems,
            CreateOfferingUrl = createOfferingUrl
        };
    }
}