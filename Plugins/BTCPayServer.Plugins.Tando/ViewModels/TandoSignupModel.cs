using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSignupRequest
{
    [Required]
    public string PhoneNumber { get; set; }
    public string? DeviceName { get; set; }
}

public class TandoSignupResponse
{
    public string StoreId { get; set; }
    public string PhoneNumber { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? PosAppId { get; set; }
    public string? CartAppId { get; set; }
    public string UserId { get; set; }
    public string ApiKey { get; set; }
}

public class TandoIssueAppKeyRequest
{
    [Required]
    public string PhoneNumber { get; set; }
    public string? DeviceName { get; set; }
}

public class TandoIssueAppKeyResponse
{
    public string UserId { get; set; }
    public string PhoneNumber { get; set; }
    public string ApiKey { get; set; }
    public TandoStoreAccess[] Stores { get; set; }
}

public class TandoStoreAccess
{
    public string StoreId { get; set; }
    public string StoreName { get; set; }
    public string? Role { get; set; }
}
public class TandoLoginRequest
{
    [Required]
    public string PhoneNumber { get; set; }
}

public class TandoLoginResponse
{
    public string UserId { get; set; }
    public string PhoneNumber { get; set; }
    public TandoLoginStore[] Stores { get; set; }
}

public class TandoLoginStore
{
    public string StoreId { get; set; }
    public string StoreName { get; set; }
    public string? Role { get; set; }
    public TandoLoginSubscription? Subscription { get; set; }
    public TandoLoginEmployee[]? Employees { get; set; }
}

public class TandoLoginSubscription
{
    public bool Active { get; set; }
    public string? Phase { get; set; }
    public string? PlanId { get; set; }
    public DateTimeOffset? NextPaymentDue { get; set; }
    public bool Suspended { get; set; }
    public string? RenewalUrl { get; set; }
    public DateTimeOffset? RenewalUrlExpiresAt { get; set; }
}

public class TandoLoginEmployee
{
    public string UserId { get; set; }
    public string? PhoneNumber { get; set; }
}