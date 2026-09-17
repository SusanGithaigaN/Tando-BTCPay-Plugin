using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.Services;

namespace Tando.Tests;

// Test assembly only: never registered by the production plugin.
public sealed class MockDarajaProvider(PhoneVerificationResult result) : PhoneVerificationProvider
{
    public int Calls { get; private set; }
    public override Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

public sealed class MockMpesaProvider : MpesaPayoutProvider
{
    private readonly Dictionary<string, MpesaPayoutRequest> requests = new();
    private readonly Dictionary<string, MpesaPayoutResult> results = new();
    private readonly object sync = new();
    public bool Available { get; set; } = true;
    public bool LoseNextAcknowledgement { get; set; }
    public int Transfers { get; private set; }

    public override Task<MpesaPayoutResult> Submit(MpesaPayoutRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!Available)
                return Task.FromResult(new MpesaPayoutResult(request.Reference, MpesaPayoutState.Unavailable));
            if (requests.TryGetValue(request.Reference, out var previous))
            {
                if (previous != request)
                    throw new InvalidOperationException("Idempotency key reused for a different payout.");
                return Task.FromResult(results[request.Reference]);
            }
            requests.Add(request.Reference, request);
            results.Add(request.Reference, new(request.Reference, MpesaPayoutState.Pending));
            Transfers++;
            if (LoseNextAcknowledgement)
            {
                LoseNextAcknowledgement = false;
                return Task.FromResult(new MpesaPayoutResult(request.Reference, MpesaPayoutState.Unavailable));
            }
            return Task.FromResult(results[request.Reference]);
        }
    }

    // Simulates an already authenticated, normalized provider callback.
    public void Callback(string reference, MpesaPayoutState state)
    {
        lock (sync)
        {
            if (!results.TryGetValue(reference, out var current))
                throw new InvalidOperationException("Unknown payout.");
            if (state is not (MpesaPayoutState.Succeeded or MpesaPayoutState.Failed))
                throw new InvalidOperationException("Expected a terminal callback.");
            if (current.State is MpesaPayoutState.Succeeded or MpesaPayoutState.Failed)
            {
                if (current.State != state)
                    throw new InvalidOperationException("Conflicting terminal callback requires investigation.");
                return;
            }
            results[reference] = new(reference, state);
        }
    }

    public override Task<MpesaPayoutResult> Reconcile(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
            return Task.FromResult(Available && results.TryGetValue(reference, out var result)
                ? result : new MpesaPayoutResult(reference, MpesaPayoutState.Unavailable));
    }
}

public sealed class MockMpesaPaymentProvider(StkPushResult result) : MpesaPaymentProvider
{
    public override string? NormalizePhone(string rawPhone, out string? normalized)
    {
        normalized = rawPhone;
        return null;
    }

    public override Task<StkPushResult> InitiateStkPush(string customerMsisdn, string merchantDestination,
        decimal amountKes, string orderId, string callbackUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}
