#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>Durable simulated PSP state, separate from the merchant ledger, for reconciliation testing.</summary>
public sealed class DevelopmentMpesaWorkflowProvider : MpesaWorkflowProvider
{
    private readonly MpesaLedger ledger;
    public DevelopmentMpesaWorkflowProvider(MpesaLedger ledger, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Mock payments require Development.");
        this.ledger = ledger;
    }
    public override string Name => "Mock";
    public override bool Ready => true;
    private static string Key(string reference) => "Tando.Mpesa.Mock." + reference;
    private static MpesaLookup Result(MpesaLedgerEntry entry) => new(true, false,
        new(entry.Reference, entry.Transfer.AmountKes, entry.Transfer.Destination,
            entry.Collection, entry.Disbursement, entry.CheckoutRequestId));

    public override async Task<MpesaLookup> Submit(string reference, MpesaTransfer transfer, string callbackUrl, CancellationToken ct)
    {
        if (transfer.OrderId.StartsWith("mock-outage-", StringComparison.Ordinal)) return new(false, false);
        var entry = await ledger.Update(Key(reference), existing =>
        {
            if (existing is not null)
            {
                if (existing.Transfer != transfer) throw new InvalidOperationException("idempotency_conflict");
                return existing;
            }
            return new MpesaLedgerEntry(transfer, reference, Name)
            { Collection = MpesaLegState.Pending, CheckoutRequestId = "mock-" + reference };
        }, ct);
        // Provider accepted the request, but the caller did not receive the acknowledgement.
        return transfer.OrderId.StartsWith("mock-lost-ack-", StringComparison.Ordinal) ? new(false, false) : Result(entry!);
    }

    public override async Task<MpesaLookup> Lookup(string reference, CancellationToken ct)
    {
        var entry = await ledger.Read(Key(reference), ct);
        return entry is null ? new(true, true) : Result(entry);
    }

    public async Task<MpesaObservation> Callback(string reference, MpesaObservation observation, CancellationToken ct)
    {
        var updated = await ledger.Update(Key(reference), existing =>
            MpesaWorkflow.Merge(existing ?? throw new ArgumentException("payment_not_found"), observation), ct);
        return Result(updated!).Payment!;
    }
}

/// <summary>Splice is the reference PSP. Real processing stays blocked until its
/// authenticated callback, lookup and idempotency contracts have been approved.</summary>
public sealed class SpliceWorkflowProvider : MpesaWorkflowProvider
{
    public override string Name => "Splice";
    public override bool Ready => false;
    public override Task<MpesaLookup> Submit(string reference, MpesaTransfer transfer, string callbackUrl, CancellationToken ct) =>
        Task.FromResult(new MpesaLookup(false, false));
    public override Task<MpesaLookup> Lookup(string reference, CancellationToken ct) =>
        Task.FromResult(new MpesaLookup(false, false));
}
