using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

public enum MpesaPayoutState { Unavailable, Pending, Succeeded, Failed }

/// <summary>The reference is a stable idempotency key, scoped to a store and invoice.
/// Adapters must reconcile an uncertain submission before resending money.</summary>
public record MpesaPayoutRequest(string Reference, string Destination, decimal AmountKes);
public record MpesaPayoutResult(string Reference, MpesaPayoutState State, string? Error = null);

/// <summary>External B2C boundary. Acceptance is Pending; only confirmed delivery is Succeeded.
/// Callback authentication and translation belong in the approved adapter.</summary>
public abstract class MpesaPayoutProvider
{
    public abstract Task<MpesaPayoutResult> Submit(MpesaPayoutRequest request, CancellationToken cancellationToken = default);
    public abstract Task<MpesaPayoutResult> Reconcile(string reference, CancellationToken cancellationToken = default);
}

/// <summary>No B2C wire protocol is assumed until Tando supplies the approved integration.</summary>
public sealed class UnconfiguredMpesaPayoutProvider : MpesaPayoutProvider
{
    public override Task<MpesaPayoutResult> Submit(MpesaPayoutRequest request, CancellationToken cancellationToken = default) =>
        Reconcile(request.Reference, cancellationToken);

    public override Task<MpesaPayoutResult> Reconcile(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MpesaPayoutResult(reference, MpesaPayoutState.Unavailable, "M-Pesa payouts are not configured."));
}
