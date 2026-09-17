using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.MassStoreGenerator;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Tando.Tests;

public class ProviderTests
{
    [Theory]
    [InlineData(false, false, true, 400)]
    [InlineData(false, true, true, 503)]
    [InlineData(false, true, false, 503)]
    [InlineData(true, true, true, 503)]
    public async Task BothSignupStepsBlockBeforeTouchingStores(bool matches, bool unavailable, bool configured, int status)
    {
        var provider = new MockDarajaProvider(new(matches, unavailable, "", configured));
        // Null downstream dependencies deliberately fail if the gate reaches store/subscription work.
        var controller = new TandoOnboardingController(null!, null!, null!, provider);
        var first = await controller.Signup(new TandoSignupRequest
        {
            PhoneNumber = "0701234567", IdNumber = "test-id"
        }, CancellationToken.None);
        var second = await controller.Subscribe(new TandoSubscribeRequest
        {
            PhoneNumber = "0701234567", IdNumber = "test-id", PlanId = "test-plan"
        }, CancellationToken.None);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(first).StatusCode);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(second).StatusCode);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task MatchingIdentityPassesTheSharedSignupGate()
    {
        var provider = new MockDarajaProvider(new(true, false, ""));
        var controller = new TandoOnboardingController(null!, null!, null!, provider);
        var method = typeof(TandoOnboardingController).GetMethod("ValidateKyc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<(bool verified, IActionResult? error)>)method.Invoke(controller,
            new object[] { "254701234567", "test-id", "01" })!;
        Assert.True(result.verified);
        Assert.Null(result.error);
    }

    [Fact]
    public async Task RetryAndReconciliationDoNotDuplicateTransfers()
    {
        var provider = new MockMpesaProvider { LoseNextAcknowledgement = true };
        var request = new MpesaPayoutRequest("store:invoice", "254701234567", 100);
        Assert.Equal(MpesaPayoutState.Unavailable, (await provider.Submit(request)).State);
        Assert.Equal(MpesaPayoutState.Pending, (await provider.Reconcile(request.Reference)).State);
        Assert.Equal(MpesaPayoutState.Pending, (await provider.Submit(request)).State);
        Assert.Equal(1, provider.Transfers);
        provider.Callback(request.Reference, MpesaPayoutState.Succeeded);
        provider.Callback(request.Reference, MpesaPayoutState.Succeeded);
        Assert.Equal(MpesaPayoutState.Succeeded, (await provider.Reconcile(request.Reference)).State);
        Assert.Equal(MpesaPayoutState.Succeeded, (await provider.Submit(request)).State);
        Assert.Equal(1, provider.Transfers);
        Assert.Throws<InvalidOperationException>(() => provider.Callback(request.Reference, MpesaPayoutState.Failed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Submit(request with { AmountKes = 200 }));
    }

    [Fact]
    public async Task UnavailableAndFailedPayoutsNeverReportSettlement()
    {
        var provider = new MockMpesaProvider { Available = false };
        var request = new MpesaPayoutRequest("store:invoice", "254701234567", 100);
        Assert.Equal(MpesaPayoutState.Unavailable, (await provider.Submit(request)).State);
        Assert.Equal(0, provider.Transfers);
        provider.Available = true;
        await provider.Submit(request);
        provider.Callback(request.Reference, MpesaPayoutState.Failed);
        Assert.Equal(MpesaPayoutState.Failed, (await provider.Reconcile(request.Reference)).State);
        Assert.Equal(MpesaPayoutState.Failed, (await provider.Submit(request)).State);
        Assert.Equal(1, provider.Transfers);
        Assert.Throws<InvalidOperationException>(() => provider.Callback("unknown", MpesaPayoutState.Succeeded));
        Assert.Equal(MpesaPayoutState.Unavailable, (await new UnconfiguredMpesaPayoutProvider().Submit(request)).State);
    }
}
