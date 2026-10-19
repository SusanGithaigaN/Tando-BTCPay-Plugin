using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.PayoutProcessors.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

[JsonConverter(typeof(StringEnumConverter))]
public enum TandoPullPaymentStatus { NotApplicable, Created, Claimed, Failed, Completed }

[JsonConverter(typeof(StringEnumConverter))]
public enum TandoMpesaPayoutStatus { NotTriggered, Triggered, Confirmed, Failed }
public record TandoUpdateMpesaPayoutStatusRequest(TandoMpesaPayoutStatus Status, string? PayoutReference, string? Error);

public record TandoSplitRecord
{
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "";
    public decimal BtcPortionAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal MpesaPortionAmount { get; set; }
    public decimal MpesaPercentage { get; set; }
    public TandoMpesaDestinationType? MpesaDestinationType { get; set; }
    public string? MpesaDestination { get; set; }
    public string? MpesaAccountNumber { get; set; }
    public string? TandoTransactionId { get; set; }
    public int TandoInvoiceAttempts { get; set; }
    public bool MpesaSettled { get; set; }
    public DateTimeOffset? MpesaSettledAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? PullPaymentId { get; set; }
    public string? PayoutId { get; set; }
    public TandoPullPaymentStatus PullPaymentStatus { get; set; } = TandoPullPaymentStatus.NotApplicable;
    public string? PullPaymentError { get; set; }
    public TandoMpesaPayoutStatus MpesaPayoutStatus { get; set; } = TandoMpesaPayoutStatus.NotTriggered;
    public string? MpesaPayoutReference { get; set; }
    public string? MpesaPayoutError { get; set; }
}


public class TandoSplitService(InvoiceRepository invoiceRepository, TandoSubscriptionService subscriptionService,
    StoreRepository storeRepository, TandoMerchantSettingsService merchantSettingsService, TandoPaymentsClient tandoPaymentsClient, BTCPayNetworkProvider networkProvider,
    PullPaymentHostedService pullPaymentHostedService, ApplicationDbContextFactory dbContextFactory,
    PayoutProcessorService payoutProcessorService, EventAggregator eventAggregator)
{
    private const string SplitMetadataKey = "tandoSplit";

    private static readonly SemaphoreSlim SplitLock = new(1, 1);

    public static bool IsSplittable(InvoiceEntity invoice) => invoice switch
    {
        { Status: InvoiceStatus.Settled } => true,
        { Status: InvoiceStatus.Expired, ExceptionStatus: InvoiceExceptionStatus.PaidLate }
            => invoice.GetPayments(true).All(p => p.Status == PaymentStatus.Settled),
        _ => false
    };

    public async Task<(TandoSplitRecord? Record, string? Error)> ComputeAndRecordSplit(string storeId, string invoiceId)
    {
        await SplitLock.WaitAsync();
        try
        {
            return await ComputeAndRecordSplitCore(storeId, invoiceId);
        }
        finally
        {
            SplitLock.Release();
        }
    }

    private async Task<(TandoSplitRecord? Record, string? Error)> ComputeAndRecordSplitCore(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (null, "invoice_not_found");

        var existing = GetRecord(invoice);
        if (existing is not null)
            return (existing, null);

        if (!IsSplittable(invoice))
            return (null, "invoice_not_settled");

        var splitConfig = await merchantSettingsService.GetSplitConfig(storeId);
        if (splitConfig.MpesaPercentage > 0 && !(await subscriptionService.GetSettings()).IsTandoPayoutConfigured)
            return (null, "payouts_not_configured");
        var mpesaSettings = await merchantSettingsService.GetMpesaSettings(storeId);

        var paidAmount = Math.Max(0m, invoice.PaidAmount?.Net ?? 0m);
        var mpesaPortion = Math.Round(paidAmount * (splitConfig.MpesaPercentage / 100m), 2);
        var record = new TandoSplitRecord
        {
            TotalAmount = invoice.Price,
            PaidAmount = paidAmount,
            Currency = invoice.Currency,
            MpesaPortionAmount = mpesaPortion,
            BtcPortionAmount = paidAmount - mpesaPortion,
            MpesaPercentage = splitConfig.MpesaPercentage,
            MpesaDestinationType = mpesaSettings?.DestinationType,
            MpesaDestination = mpesaSettings?.Destination,
            MpesaAccountNumber = mpesaSettings?.AccountNumber,
            MpesaSettled = mpesaPortion <= 0,
            PullPaymentStatus = mpesaPortion <= 0 ? TandoPullPaymentStatus.NotApplicable : TandoPullPaymentStatus.Created,
            RecordedAt = DateTimeOffset.UtcNow
        };
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        if (mpesaPortion > 0)
            record = await CreateAndClaimPayout(storeId, invoiceId, record);

        return (record, null);
    }

    public async Task<TandoSplitRecord?> GetSplit(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return null;

        return GetRecord(invoice);
    }

    public async Task<(bool Success, string? Error)> MarkMpesaSettled(string storeId, string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (false, "invoice_not_found");

        var record = GetRecord(invoice);
        if (record is null)
            return (false, "split_not_recorded");
        if (record.MpesaSettled)
            return (true, null);

        record.MpesaSettled = true;
        record.MpesaSettledAt = DateTimeOffset.UtcNow;
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return (true, null);
    }

    public const int MaxListTake = 200;

    /// <summary>
    /// Recorded splits for the store, newest first. Postgres filters to invoices that carry a split, orders and pages them,
    /// and only the split JSON comes back, so the cost follows the page size rather than the store's invoice count.
    /// </summary>
    public async Task<IReadOnlyList<TandoSplitRecord>> ListSplits(string storeId, int skip, int take)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxListTake);

        await using var ctx = dbContextFactory.CreateContext();
        // The WHERE and ORDER BY expressions match IX_Invoices_TandoSplit (see TandoSplitIndexHostedService) so it can be used.
        // RecordedAt is sorted as text: every record is written with DateTimeOffset.UtcNow, so all values share the +00:00
        // offset and text order is time order.
        var rows = await ctx.Database.GetDbConnection().QueryAsync<string>(
            """
            SELECT "Blob2"->'metadata'->>'tandoSplit'
            FROM "Invoices"
            WHERE "StoreDataId" = @storeId
              AND ("Blob2"->'metadata'->'tandoSplit') IS NOT NULL
              AND NOT "Archived"
            ORDER BY ("Blob2"->'metadata'->'tandoSplit'->>'RecordedAt') DESC, "Id"
            OFFSET @skip LIMIT @take
            """,
            new { storeId, skip, take });

        return rows
            .Select(json => JsonConvert.DeserializeObject<TandoSplitRecord>(json))
            .Where(r => r is not null)
            .Cast<TandoSplitRecord>()
            .ToList();
    }

    public async Task<(bool Success, string? Error)> UpdateMpesaPayoutStatus(
        string storeId, string invoiceId, TandoMpesaPayoutStatus status, string? payoutReference = null, string? error = null)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (false, "invoice_not_found");

        var record = GetRecord(invoice);
        if (record is null)
            return (false, "split_not_recorded");

        record.MpesaPayoutStatus = status;
        record.MpesaPayoutReference = payoutReference ?? record.MpesaPayoutReference;
        record.MpesaPayoutError = error;
        if (status == TandoMpesaPayoutStatus.Confirmed)
        {
            record.MpesaSettled = true;
            record.MpesaSettledAt = DateTimeOffset.UtcNow;
        }
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return (true, null);
    }


    private const int MaxTandoInvoiceAttempts = 3;

    private async Task<TandoSplitRecord> CreateAndClaimPayout(string storeId, string invoiceId, TandoSplitRecord record)
    {
        var settings = await subscriptionService.GetSettings();
        if (!settings.IsTandoPayoutConfigured)
            return record;

        try
        {
            if (settings.MaxPayoutAmount is { } maxPayout && record.MpesaPortionAmount > maxPayout)
                MarkFailed(record, "payout_limit_exceeded");
            else if (await storeRepository.FindStore(storeId) is not { } store)
                MarkFailed(record, "store_not_found");
            else if (record.MpesaDestinationType is null || string.IsNullOrWhiteSpace(record.MpesaDestination))
                MarkFailed(record, "no_mpesa_destination_configured");
            else
            {
                // The invoice id doubles as Tando's idempotency key, so a retry never creates a second payment.
                var created = await tandoPaymentsClient.CreatePayment(record.MpesaPortionAmount, record.MpesaDestinationType.Value,
                    record.MpesaDestination, record.MpesaAccountNumber, invoiceId);
                if (created.Value is { } payment)
                    await ClaimTandoInvoice(store, invoiceId, record, payment);
                else
                    MarkFailed(record, created.ErrorCode ?? "tando_no_invoice");
            }
        }
        catch (Exception ex)
        {
            MarkFailed(record, ex.Message);
        }
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        return record;
    }

    private async Task ClaimTandoInvoice(Data.StoreData store, string invoiceId, TandoSplitRecord record, TandoPayment payment)
    {
        record.TandoTransactionId = payment.Id;
        if (string.IsNullOrEmpty(payment.LightningInvoice))
        {
            MarkFailed(record, "tando_no_invoice");
            return;
        }

        var paymentRequest = BOLT11PaymentRequest.Parse(payment.LightningInvoice, networkProvider.BTC.NBitcoinNetwork);
        if (paymentRequest.MinimumAmount == LightMoney.Zero
            || (payment.SatsTotal > 0 && paymentRequest.MinimumAmount.ToDecimal(LightMoneyUnit.Satoshi) != payment.SatsTotal))
        {
            MarkFailed(record, "tando_invoice_amount_mismatch");
            return;
        }

        record.TandoInvoiceAttempts++;
        await Claim(store, invoiceId, record, new BoltInvoiceClaimDestination(payment.LightningInvoice, paymentRequest),
            paymentRequest.MinimumAmount.ToDecimal(LightMoneyUnit.BTC));
    }

    private async Task RetryTandoInvoice(Data.StoreData store, string invoiceId, TandoSplitRecord record)
    {
        try
        {
            var recreated = await tandoPaymentsClient.RecreateInvoice(record.TandoTransactionId!);
            if (recreated.Value is not { } payment)
                MarkFailed(record, recreated.ErrorCode ?? "tando_recreate_failed");
            else if (payment.Status != "pending")
            {
                // Tando already has the money, so don't pay again. The status poller settles the M-Pesa side.
                record.PullPaymentStatus = TandoPullPaymentStatus.Completed;
                record.MpesaPayoutStatus = TandoMpesaPayoutStatus.Triggered;
            }
            else
                await ClaimTandoInvoice(store, invoiceId, record, payment);
        }
        catch (Exception ex)
        {
            MarkFailed(record, ex.Message);
        }
    }

    private async Task Claim(Data.StoreData store, string invoiceId, TandoSplitRecord record, IClaimDestination destination, decimal amountBtc)
    {
        await EnsureLightningPayoutProcessor(store.Id);
        var payoutMethodId = PayoutTypes.LN.GetPayoutMethodId("BTC");
        var pullPaymentId = await pullPaymentHostedService.CreatePullPayment(store, new CreatePullPaymentRequest
        {
            Name = $"Tando split payout - invoice {invoiceId}",
            Amount = amountBtc,
            Currency = "BTC",
            PayoutMethods = [payoutMethodId.ToString()],
            AutoApproveClaims = true
        });
        var claimResult = await pullPaymentHostedService.Claim(new ClaimRequest
        {
            Destination = destination,
            PullPaymentId = pullPaymentId,
            ClaimedAmount = amountBtc,
            PayoutMethodId = payoutMethodId,
            StoreId = store.Id
        });
        record.PullPaymentId = pullPaymentId;
        if (claimResult.Result == ClaimRequest.ClaimResult.Ok)
        {
            // Claimed is not paid. The M-Pesa side waits for the payout's Completed event (OnTreasuryPayoutUpdated).
            record.PullPaymentStatus = TandoPullPaymentStatus.Claimed;
            record.PullPaymentError = null;
            record.PayoutId = claimResult.PayoutData?.Id;
        }
        else
        {
            MarkFailed(record, ClaimRequest.GetErrorMessage(claimResult.Result));
        }
    }

    private static void MarkFailed(TandoSplitRecord record, string error)
    {
        record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
        record.PullPaymentError = error;
    }

    public async Task OnTreasuryPayoutUpdated(Data.PayoutData payout)
    {
        if (payout.State is not (PayoutState.Completed or PayoutState.Cancelled) || payout.PullPaymentDataId is null)
            return;

        var invoiceId = await FindInvoiceByPullPayment(payout.StoreDataId, payout.PullPaymentDataId);
        if (invoiceId is null)
            return;

        await SplitLock.WaitAsync();
        try
        {
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            var record = invoice is null ? null : GetRecord(invoice);
            if (record is not { PullPaymentStatus: TandoPullPaymentStatus.Claimed })
                return; // not ours, or already handled

            record.PayoutId ??= payout.Id;
            if (payout.State == PayoutState.Completed)
            {
                record.PullPaymentStatus = TandoPullPaymentStatus.Completed;
                // Tando pays M-Pesa itself once its invoice is paid; TandoPayoutStatusHostedService picks up the result.
                if (record.TandoTransactionId is not null)
                    record.MpesaPayoutStatus = TandoMpesaPayoutStatus.Triggered;
            }
            else if (record.TandoTransactionId is not null && record.TandoInvoiceAttempts < MaxTandoInvoiceAttempts
                     && await storeRepository.FindStore(payout.StoreDataId) is { } store)
            {
                // Tando's invoice expired before the store paid it. Get a fresh one for the same payment and claim again.
                await RetryTandoInvoice(store, invoiceId, record);
            }
            else
            {
                record.PullPaymentStatus = TandoPullPaymentStatus.Failed;
                record.PullPaymentError = "lightning_payout_cancelled";
            }
            await invoiceRepository.UpdateInvoiceMetadata(invoiceId, SplitMetadataKey, record);
        }
        finally
        {
            SplitLock.Release();
        }
    }

    private async Task<string?> FindInvoiceByPullPayment(string storeId, string pullPaymentId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await RelationalDatabaseFacadeExtensions.GetDbConnection(ctx.Database).ExecuteScalarAsync<string?>(
            """
            SELECT "Id" FROM "Invoices"
            WHERE "StoreDataId" = @storeId
              AND "Blob2"->'metadata'->'tandoSplit'->>'PullPaymentId' = @pullPaymentId
            LIMIT 1
            """,
            new { storeId, pullPaymentId });
    }

    private async Task EnsureLightningPayoutProcessor(string storeId)
    {
        var payoutMethodId = PayoutTypes.LN.GetPayoutMethodId("BTC");
        var existing = await payoutProcessorService.GetProcessors(new PayoutProcessorService.PayoutProcessorQuery
        {
            Stores = new[] { storeId },
            Processors = new[] { LightningAutomatedPayoutSenderFactory.ProcessorName },
            PayoutMethods = new[] { payoutMethodId }
        });
        if (existing.Any())
            return;

        var processor = new Data.PayoutProcessorData
        {
            StoreId = storeId,
            PayoutMethodId = payoutMethodId.ToString(),
            Processor = LightningAutomatedPayoutSenderFactory.ProcessorName
        };
        processor.HasTypedBlob<LightningAutomatedPayoutBlob>().SetBlob(new LightningAutomatedPayoutBlob
        {
            ProcessNewPayoutsInstantly = true,
            Interval = TimeSpan.FromMinutes(5)
        });
        var processed = new TaskCompletionSource();
        eventAggregator.Publish(new PayoutProcessorUpdated { Data = processor, Id = processor.Id, Processed = processed });
        await processed.Task;
    }

    public async Task<IReadOnlyList<(string InvoiceId, string StoreId, string TandoTransactionId)>> ListPendingTandoPayouts(int take = 100)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var rows = await ctx.Database.GetDbConnection().QueryAsync<(string, string, string)>(
            """
            SELECT "Id", "StoreDataId", "Blob2"->'metadata'->'tandoSplit'->>'TandoTransactionId'
            FROM "Invoices"
            WHERE ("Blob2"->'metadata'->'tandoSplit'->>'MpesaPayoutStatus') = 'Triggered'
              AND ("Blob2"->'metadata'->'tandoSplit'->>'TandoTransactionId') IS NOT NULL
            LIMIT @take
            """,
            new { take });
        return rows.ToList();
    }

    public async Task ApplyTandoPaymentStatus(string storeId, string invoiceId, TandoPayment payment)
    {
        switch (payment.Status)
        {
            case "completed":
                await UpdateMpesaPayoutStatus(storeId, invoiceId, TandoMpesaPayoutStatus.Confirmed, payment.PaymentNetworkReceipt);
                break;
            case "refused" or "failed" or "refunded":
                await UpdateMpesaPayoutStatus(storeId, invoiceId, TandoMpesaPayoutStatus.Failed, null,
                    payment.FailureReason ?? $"tando_{payment.Status}");
                break;
        }
    }

    private static TandoSplitRecord? GetRecord(InvoiceEntity invoice) =>
        invoice.Metadata?.AdditionalData is { } data && data.TryGetValue(SplitMetadataKey, out var token)
        && token.Type == JTokenType.Object ? token.ToObject<TandoSplitRecord>() : null;
}