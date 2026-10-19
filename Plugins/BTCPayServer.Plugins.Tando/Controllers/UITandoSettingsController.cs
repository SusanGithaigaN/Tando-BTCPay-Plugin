using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.Tando;

[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("server/tando")]
public class UITandoSettingsController(TandoSubscriptionService subscriptionService, IStringLocalizer stringLocalizer,
    IHttpClientFactory httpClientFactory) : Controller
{
    private IStringLocalizer StringLocalizer { get; } = stringLocalizer;
    private const string SpecResourceName = "BTCPayServer.Plugins.Tando.Resources.tando-openapi.yaml";

    [HttpGet]
    public async Task<IActionResult> Settings(string? offeringId = null)
    {
        ViewData["ActivePage"] = "Tando";
        return View(await BuildViewModel(offeringId, null, null, null, null));

    }

    [HttpPost]
    public async Task<IActionResult> Settings(TandoSettingsViewModel model)
    {
        ViewData["ActivePage"] = "Tando";
        if (model.Command == "refresh")
        {
            ModelState.Clear();
            return View(await BuildViewModel(model.SubscriptionOfferingId, null, null, model.TreasuryLightningAddress, model.MaxPayoutAmount, model.TandoApiBaseUrl, model.TandoApiKeyId));
        }
        if (string.IsNullOrEmpty(model.SubscriptionOfferingId))
        {
            ModelState.AddModelError(nameof(model.SubscriptionOfferingId), StringLocalizer["Select a subscription offering"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
        }

        var activePlans = await subscriptionService.GetActivePlans(model.SubscriptionOfferingId);
        if (activePlans.Length == 0)
        {
            ModelState.AddModelError(nameof(model.SubscriptionOfferingId), StringLocalizer["This offering has no active plans yet. Add at least one active plan to it before selecting it here."]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
        }

        if (string.IsNullOrEmpty(model.SubscriptionPlanId) || activePlans.All(p => p.Id != model.SubscriptionPlanId))
        {
            ModelState.AddModelError(nameof(model.SubscriptionPlanId), StringLocalizer["Select the plan merchants should be tied to"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
        }
        if (!string.IsNullOrEmpty(model.FallbackSubscriptionPlanId))
        {
            if (model.FallbackSubscriptionPlanId == model.SubscriptionPlanId)
            {
                ModelState.AddModelError(nameof(model.FallbackSubscriptionPlanId), StringLocalizer["Fallback plan must be different from the primary plan"]);
                return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
            }
            if (activePlans.All(p => p.Id != model.FallbackSubscriptionPlanId))
            {
                ModelState.AddModelError(nameof(model.FallbackSubscriptionPlanId), StringLocalizer["Selected fallback plan is not an active plan on this offering"]);
                return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
            }
        }
        var treasuryAddress = string.IsNullOrWhiteSpace(model.TreasuryLightningAddress) ? null : model.TreasuryLightningAddress.Trim();
        if (treasuryAddress is not null && treasuryAddress != (await subscriptionService.GetSettings()).TreasuryLightningAddress)
        {
            var treasuryError = await CheckTreasuryAddress(treasuryAddress, HttpContext.RequestAborted);
            if (treasuryError is not null)
            {
                ModelState.AddModelError(nameof(model.TreasuryLightningAddress), treasuryError);
                return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
            }
        }
        if (model.MaxPayoutAmount is <= 0)
        {
            ModelState.AddModelError(nameof(model.MaxPayoutAmount), StringLocalizer["The payout limit must be greater than zero, or left empty for no limit"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
        }

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
            TreasuryLightningAddress = treasuryAddress,
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
                model.TreasuryLightningAddress, model.MaxPayoutAmount, model.TandoApiBaseUrl, model.TandoApiKeyId));
        }
    }
    private static readonly Regex LightningAddressFormat = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private async Task<string?> CheckTreasuryAddress(string address, CancellationToken cancellationToken)
    {
        Uri endpoint;
        string? tag = null;
        try
        {
            endpoint = LightningAddressFormat.IsMatch(address)
                ? LNURL.LNURL.ExtractUriFromInternetIdentifier(address) : LNURL.LNURL.Parse(address, out tag);
        }
        catch (FormatException)
        {
            return StringLocalizer["Enter a Lightning address (name@domain) or an LNURL"];
        }
        if (tag is not null)
            return tag.Equals("payRequest", StringComparison.OrdinalIgnoreCase) ? null : StringLocalizer["This LNURL can't receive payments"];

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var info = await LNURL.LNURL.FetchInformation(endpoint, httpClientFactory.CreateClient(nameof(UITandoSettingsController)), timeout.Token);
            return info is LNURLPayRequest
                ? null : StringLocalizer["This address answered, but it can't receive Lightning payments"];
        }
        catch (Exception)
        {
            return StringLocalizer["Couldn't reach this Lightning address. Check the spelling and that the domain is online."];
        }
    }

    private async Task<TandoSettingsViewModel> BuildViewModel(string? selectedOfferingId, string? selectedPlanId, string? selectedFallbackPlanId,
    string? selectedTreasuryLightningAddress, decimal? selectedMaxPayoutAmount, string? selectedApiBaseUrl = null, string? selectedApiKeyId = null)
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
            TreasuryLightningAddress = selectedTreasuryLightningAddress ?? settings.TreasuryLightningAddress,
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