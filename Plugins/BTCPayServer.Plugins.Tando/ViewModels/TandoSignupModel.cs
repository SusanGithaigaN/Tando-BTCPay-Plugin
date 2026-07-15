using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSignupRequest
{
    [Required]
    public string PhoneNumber { get; set; }

    // Required when Daraja mobile number validation is configured on the server.
    public string IdType { get; set; }
    public string IdNumber { get; set; }
}

public class TandoSignupResponse
{
    public string StoreId { get; set; }
    public string PhoneNumber { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? PosAppId { get; set; }
    public string? CartAppId { get; set; }
    public bool PhoneNumberVerified { get; set; }
}