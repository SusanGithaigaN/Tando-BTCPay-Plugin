namespace BTCPayServer.Plugins.Tando.ViewModels;

public enum TandoMpesaDestinationType { MobileNumber, TillNumber, PayBill }

public class TandoMpesaSettingsRequest
{
    public TandoMpesaDestinationType DestinationType { get; set; }
    public string? Destination { get; set; }
    public string? AccountNumber { get; set; }
}