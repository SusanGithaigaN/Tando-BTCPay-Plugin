using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSettingsViewModel
{
    public string? SubscriptionOfferingId { get; set; }
    public string? SubscriptionPlanId { get; set; }
    public List<SelectListItem> Offerings { get; set; } = new();
    public List<SelectListItem> Plans { get; set; } = new();
    public string? CreateOfferingUrl { get; set; }
}

public class TandoSettings
{
    public string? SubscriptionOfferingId { get; set; }
    public string? SubscriptionPlanId { get; set; }
}