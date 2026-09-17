using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Tando.Services;

public enum TandoPullPaymentStatus { NotApplicable, Created, Claimed, Failed }

public record TandoSplitRecord
{
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "";
    public decimal BtcPortionAmount { get; set; }
    public decimal MpesaPortionAmount { get; set; }
    public decimal MpesaPercentage { get; set; }
    public TandoMpesaDestinationType? MpesaDestinationType { get; set; }
    public string? MpesaDestination { get; set; }
    public bool MpesaSettled { get; set; }
    public DateTimeOffset? MpesaSettledAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? PullPaymentId { get; set; }
    public TandoPullPaymentStatus PullPaymentStatus { get; set; } = TandoPullPaymentStatus.NotApplicable;
    public string? PullPaymentError { get; set; }
}


/// <summary>Read access to historical split records. Cross-rail conversion is outside v1.</summary>
public class TandoSplitService(InvoiceRepository invoiceRepository)
{
    public const string DisabledError = "cross_rail_conversion_not_supported";
    private const string SplitMetadataKey = "tandoSplit";

    public Task<(TandoSplitRecord? Record, string? Error)> ComputeAndRecordSplit(string storeId, string invoiceId) =>
        Task.FromResult<(TandoSplitRecord?, string?)>((null, DisabledError));

    public async Task<TandoSplitRecord?> GetSplit(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return null;
        return invoice.Metadata?.GetAdditionalData<TandoSplitRecord>(SplitMetadataKey);
    }

    public Task<(bool Success, string? Error)> MarkMpesaSettled(string storeId, string invoiceId) =>
        Task.FromResult<(bool, string?)>((false, DisabledError));
}
