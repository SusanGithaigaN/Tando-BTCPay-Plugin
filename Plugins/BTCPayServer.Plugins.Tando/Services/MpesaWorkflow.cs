#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Tando.Services;

[JsonConverter(typeof(StringEnumConverter))]
public enum MpesaLegState { Unknown, Pending, Succeeded, Failed }

public record MpesaTransfer(string StoreId, string OrderId, string Phone, string Destination, decimal AmountKes);
public record MpesaObservation(string Reference, decimal AmountKes, string Destination,
    MpesaLegState Collection, MpesaLegState Disbursement, string? CheckoutRequestId = null);
public record MpesaLookup(bool Available, bool NotFound, MpesaObservation? Payment = null);
public record MpesaLedgerEntry(MpesaTransfer Transfer, string Reference, string Provider)
{
    public MpesaLegState Collection { get; init; } = MpesaLegState.Unknown;
    public MpesaLegState Disbursement { get; init; } = MpesaLegState.Unknown;
    public string? CheckoutRequestId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool NeedsReconciliation => Collection is MpesaLegState.Unknown or MpesaLegState.Pending ||
        (Collection == MpesaLegState.Succeeded && Disbursement is MpesaLegState.Unknown or MpesaLegState.Pending);
}

/// <summary>Adapters own protocol/authentication and MUST honor reference idempotency.
/// NotFound must be authoritative. An outage must never be reported as NotFound.
/// A PSP orchestrates collection and KES disbursement; this contract never sends BTC.</summary>
public abstract class MpesaWorkflowProvider
{
    public abstract string Name { get; }
    public abstract bool Ready { get; }
    public abstract Task<MpesaLookup> Submit(string reference, MpesaTransfer transfer, string callbackUrl, CancellationToken ct);
    public abstract Task<MpesaLookup> Lookup(string reference, CancellationToken ct);
}

/// <summary>Atomic durable mutation; null results are not saved.</summary>
public abstract class MpesaLedger
{
    public abstract Task<MpesaLedgerEntry?> Read(string key, CancellationToken ct = default);
    public abstract Task<MpesaLedgerEntry?> Update(string key, Func<MpesaLedgerEntry?, MpesaLedgerEntry?> update, CancellationToken ct = default);
}

public sealed class MpesaWorkflow(MpesaLedger ledger, MpesaWorkflowProvider provider)
{
    public static string Reference(string store, string order) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new[] { store, order })))).ToLowerInvariant();
    public static string Key(string reference) => "Tando.Mpesa.Payment." + reference;

    public Task<MpesaLedgerEntry?> Get(string store, string order, CancellationToken ct = default) =>
        ledger.Read(Key(Reference(store, order)), ct);

    public async Task<MpesaLedgerEntry> Start(MpesaTransfer transfer, string callbackUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transfer.StoreId) || string.IsNullOrWhiteSpace(transfer.OrderId) ||
            transfer.OrderId.Length > 200 || transfer.AmountKes <= 0 || transfer.AmountKes != decimal.Truncate(transfer.AmountKes) ||
            string.IsNullOrWhiteSpace(transfer.Phone) || string.IsNullOrWhiteSpace(transfer.Destination))
            throw new ArgumentException("A store, order, phone, destination and positive whole-shilling amount are required.");
        if (!provider.Ready) throw new InvalidOperationException("mpesa_provider_not_ready");
        var reference = Reference(transfer.StoreId, transfer.OrderId);
        var created = false;
        var entry = await ledger.Update(Key(reference), existing =>
        {
            created = false; // The database execution strategy may replay this mutation.
            if (existing is not null)
            {
                if (existing.Transfer != transfer || existing.Provider != provider.Name)
                    throw new InvalidOperationException("idempotency_conflict");
                return existing;
            }
            created = true;
            return new MpesaLedgerEntry(transfer, reference, provider.Name);
        }, ct);
        // Intent is committed before network I/O. An interrupted submission remains Unknown.
        if (!created) return await Reconcile(transfer.StoreId, transfer.OrderId, callbackUrl, ct);
        return await ApplyLookup(entry!, await Observe(() => provider.Submit(reference, transfer, callbackUrl, ct), ct), ct);
    }

    public async Task<MpesaLedgerEntry> Reconcile(string store, string order, string callbackUrl, CancellationToken ct = default)
    {
        var entry = await Get(store, order, ct) ?? throw new ArgumentException("payment_not_found");
        if (entry.Provider != provider.Name) throw new InvalidOperationException("provider_changed");
        if (!entry.NeedsReconciliation) return entry;
        var lookup = await Observe(() => provider.Lookup(entry.Reference, ct), ct);
        // Only authoritative absence permits resubmission, using exactly the same reference.
        if (lookup.Available && lookup.NotFound && entry.Collection == MpesaLegState.Unknown)
            lookup = await Observe(() => provider.Submit(entry.Reference, entry.Transfer, callbackUrl, ct), ct);
        return await ApplyLookup(entry, lookup, ct);
    }

    private static async Task<MpesaLookup> Observe(Func<Task<MpesaLookup>> call, CancellationToken ct)
    {
        try { return await call(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new MpesaLookup(false, false); }
    }

    private Task<MpesaLedgerEntry> ApplyLookup(MpesaLedgerEntry entry, MpesaLookup lookup, CancellationToken ct) =>
        lookup.Available && lookup.Payment is not null
            ? Apply(entry.Transfer.StoreId, entry.Transfer.OrderId, lookup.Payment, ct)
            : Task.FromResult(entry);

    // Only authenticated/normalized adapter observations may reach this method.
    public async Task<MpesaLedgerEntry> Apply(string store, string order, MpesaObservation observation, CancellationToken ct = default) =>
        (await ledger.Update(Key(Reference(store, order)), existing =>
        {
            if (existing is null) throw new ArgumentException("payment_not_found");
            if (existing.Provider != provider.Name) throw new InvalidOperationException("provider_changed");
            return Merge(existing, observation);
        }, ct))!;

    public static MpesaLedgerEntry Merge(MpesaLedgerEntry entry, MpesaObservation observation)
    {
        if (entry.Reference != observation.Reference || entry.Transfer.AmountKes != observation.AmountKes ||
            entry.Transfer.Destination != observation.Destination)
            throw new InvalidOperationException("callback_details_mismatch");
        if (!Enum.IsDefined(observation.Collection) || !Enum.IsDefined(observation.Disbursement))
            throw new ArgumentException("invalid_state");
        if (entry.CheckoutRequestId is not null && observation.CheckoutRequestId is not null &&
            entry.CheckoutRequestId != observation.CheckoutRequestId)
            throw new InvalidOperationException("checkout_reference_conflict");
        var collection = Advance(entry.Collection, observation.Collection);
        var payout = Advance(entry.Disbursement, observation.Disbursement);
        if (payout is MpesaLegState.Pending or MpesaLegState.Succeeded or MpesaLegState.Failed &&
            collection != MpesaLegState.Succeeded)
            throw new InvalidOperationException("collection_not_confirmed");
        return entry with { Collection = collection, Disbursement = payout,
            CheckoutRequestId = entry.CheckoutRequestId ?? observation.CheckoutRequestId, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private static MpesaLegState Advance(MpesaLegState current, MpesaLegState next)
    {
        if (current is MpesaLegState.Succeeded or MpesaLegState.Failed)
        {
            if (next is MpesaLegState.Succeeded or MpesaLegState.Failed && next != current)
                throw new InvalidOperationException("conflicting_terminal_state");
            return current; // Duplicate or stale pending event.
        }
        return next == MpesaLegState.Unknown ? current : next;
    }
}
