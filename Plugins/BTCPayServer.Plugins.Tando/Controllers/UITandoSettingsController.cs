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

        var payoutApiUrl = string.IsNullOrWhiteSpace(model.MpesaPayoutApiUrl) ? null : model.MpesaPayoutApiUrl.Trim();
        if (payoutApiUrl is not null && (!Uri.TryCreate(payoutApiUrl, UriKind.Absolute, out var payoutUri) || payoutUri.Scheme is not ("https" or "http")))
        {
            ModelState.AddModelError(nameof(model.MpesaPayoutApiUrl), StringLocalizer["Enter the full payout endpoint URL, starting with https://"]);
            return View(await BuildViewModel(model.SubscriptionOfferingId, model.SubscriptionPlanId, model.FallbackSubscriptionPlanId, model.TreasuryLightningAddress, model.MaxPayoutAmount));
        }
        var saved = await subscriptionService.GetSettings();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = model.SubscriptionOfferingId,
            SubscriptionPlanId = model.SubscriptionPlanId,
            FallbackSubscriptionPlanId = string.IsNullOrEmpty(model.FallbackSubscriptionPlanId) ? null : model.FallbackSubscriptionPlanId,
            TreasuryLightningAddress = treasuryAddress,
            MaxPayoutAmount = model.MaxPayoutAmount,
            MpesaPayoutApiUrl = payoutApiUrl,
            MpesaPayoutApiKey = string.IsNullOrWhiteSpace(model.MpesaPayoutApiKey) ? saved.MpesaPayoutApiKey : model.MpesaPayoutApiKey.Trim(),
            MpesaCallbackSecret = string.IsNullOrWhiteSpace(model.MpesaCallbackSecret) ? saved.MpesaCallbackSecret : model.MpesaCallbackSecret.Trim()
        });
        TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Tando settings updated"].Value;
        return RedirectToAction(nameof(Settings));
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
        string? selectedTreasuryLightningAddress, decimal? selectedMaxPayoutAmount)
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
            MpesaPayoutApiUrl = settings.MpesaPayoutApiUrl,
            HasMpesaPayoutApiKey = !string.IsNullOrWhiteSpace(settings.MpesaPayoutApiKey),
            HasMpesaCallbackSecret = !string.IsNullOrWhiteSpace(settings.MpesaCallbackSecret),
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
        var specUrl = Url.Action(nameof(OpenApiSpec), null, null, Request.Scheme);
        return Redirect($"https://editor.swagger.io/?url={Uri.EscapeDataString(specUrl!)}");
    }
}