using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private const string SpecResourceName = "BTCPayServer.Plugins.Tando.Resources.tando-openapi.yaml";

    [HttpGet]
    public async Task<IActionResult> Settings(string? offeringId = null)
    {
        ViewData["ActivePage"] = "Tando";
        return View(await BuildViewModel(offeringId, null, null, null));
    }

    [HttpPost]
    public async Task<IActionResult> Settings(TandoSettingsViewModel model)
    {
        ViewData["ActivePage"] = "Tando";
        if (model.Command == "refresh")
        {
            ModelState.Clear();
            return View(await BuildViewModel(model.SubscriptionOfferingId, null, null, model.MaxPayoutAmount, model.TandoApiBaseUrl, model.TandoApiKeyId));
        }

        if (string.IsNullOrEmpty(model.SubscriptionOfferingId))
            return await Invalid(nameof(model.SubscriptionOfferingId), StringLocalizer["Select a subscription offering"]);

        var activePlans = await subscriptionService.GetActivePlans(model.SubscriptionOfferingId);
        if (activePlans.Length == 0)
            return await Invalid(nameof(model.SubscriptionOfferingId), StringLocalizer["This offering has no active plans yet. Add at least one active plan to it before selecting it here."]);

        if (string.IsNullOrEmpty(model.SubscriptionPlanId) || activePlans.All(p => p.Id != model.SubscriptionPlanId))
            return await Invalid(nameof(model.SubscriptionPlanId), StringLocalizer["Select the plan merchants should be tied to"]);

        if (!string.IsNullOrEmpty(model.FallbackSubscriptionPlanId))
        {
            if (model.FallbackSubscriptionPlanId == model.SubscriptionPlanId)
                return await Invalid(nameof(model.FallbackSubscriptionPlanId), StringLocalizer["Fallback plan must be different from the primary plan"]);
            if (activePlans.All(p => p.Id != model.FallbackSubscriptionPlanId))
                return await Invalid(nameof(model.FallbackSubscriptionPlanId), StringLocalizer["Selected fallback plan is not an active plan on this offering"]);
        }

        if (model.MaxPayoutAmount is <= 0)
            return await Invalid(nameof(model.MaxPayoutAmount), StringLocalizer["The payout limit must be greater than zero, or left empty for no limit"]);

        var saved = await subscriptionService.GetSettings();
        var apiBaseUrl = string.IsNullOrWhiteSpace(model.TandoApiBaseUrl) ? null : model.TandoApiBaseUrl.Trim().TrimEnd('/');
        var apiKeyId = string.IsNullOrWhiteSpace(model.TandoApiKeyId) ? null : model.TandoApiKeyId.Trim();
        var apiSecret = string.IsNullOrWhiteSpace(model.TandoApiSecret) ? saved.TandoApiSecret : model.TandoApiSecret.Trim();
        var tandoOff = apiBaseUrl is null && apiKeyId is null && string.IsNullOrWhiteSpace(model.TandoApiSecret);

        if (!tandoOff)
        {
            if (apiBaseUrl is null || !Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                return await Invalid(nameof(model.TandoApiBaseUrl), StringLocalizer["Enter the Tando API address, starting with https://"]);
            if (apiKeyId is null)
                return await Invalid(nameof(model.TandoApiKeyId), StringLocalizer["Enter the key ID Tando gave you"]);
            if (!TandoPaymentsClient.IsValidSecret(apiSecret))
                return await Invalid(nameof(model.TandoApiSecret), StringLocalizer["Enter the 43-character secret Tando gave you"]);
        }

        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = model.SubscriptionOfferingId,
            SubscriptionPlanId = model.SubscriptionPlanId,
            FallbackSubscriptionPlanId = string.IsNullOrEmpty(model.FallbackSubscriptionPlanId) ? null : model.FallbackSubscriptionPlanId,
            MaxPayoutAmount = model.MaxPayoutAmount,
            TandoApiBaseUrl = tandoOff ? null : apiBaseUrl,
            TandoApiKeyId = tandoOff ? null : apiKeyId,
            TandoApiSecret = tandoOff ? null : apiSecret
        });
        TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Tando settings updated"].Value;
        return RedirectToAction(nameof(Settings));

        async Task<IActionResult> Invalid(string field, string message)
        {
            ModelState.AddModelError(field, message);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId,
                model.MaxPayoutAmount, model.TandoApiBaseUrl, model.TandoApiKeyId));
        }
    }

    private async Task<TandoSettingsViewModel> BuildViewModel(string? selectedOfferingId, string? selectedPlanId, string? selectedFallbackPlanId,
        decimal? selectedMaxPayoutAmount, string? selectedApiBaseUrl = null, string? selectedApiKeyId = null)
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
            FallbackSubscriptionPlanId = selectedFallbackPlanId ?? settings.FallbackSubscriptionPlanId,
            MaxPayoutAmount = selectedMaxPayoutAmount ?? settings.MaxPayoutAmount,
            TandoApiBaseUrl = selectedApiBaseUrl ?? settings.TandoApiBaseUrl,
            TandoApiKeyId = selectedApiKeyId ?? settings.TandoApiKeyId,
            HasTandoApiSecret = !string.IsNullOrWhiteSpace(settings.TandoApiSecret),
            Offerings = offerings.Select(o => new SelectListItem($"{o.Name} ({o.StoreName})", o.Id)).ToList(),
            Plans = planItems,
            CreateOfferingUrl = createOfferingUrl
        };
    }

    [HttpGet("openapi.yaml")]
    [AllowAnonymous]
    public IActionResult OpenApiSpec()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(SpecResourceName);
        if (stream is null)
            return NotFound();

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/yaml");
    }

    [HttpGet("docs")]
    public IActionResult Docs()
    {
        return View();
    }
}