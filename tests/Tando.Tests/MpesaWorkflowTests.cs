using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Xunit;

namespace Tando.Tests;

public class MpesaWorkflowTests
{
    private static readonly MpesaTransfer Transfer = new("store", "order", "254701234567", "254702345678", 100);
    private static DevelopmentMpesaWorkflowProvider Provider(MpesaLedger ledger) => new(ledger, new TestEnvironment());

    [Fact]
    public async Task DuplicateConcurrentRequestsAndRestartReuseOnePayment()
    {
        var ledger = new MemoryLedger();
        var service = new MpesaWorkflow(ledger, Provider(ledger));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.Start(Transfer, "")));
        Assert.Single(results.Select(x => x.Reference).Distinct());
        Assert.All(results, x => Assert.Equal(MpesaLegState.Pending, x.Collection));
        Assert.Equal(2, ledger.Count); // One intent and one provider record.
        var restarted = new MpesaWorkflow(ledger, Provider(ledger));
        Assert.Equal(results[0].Reference, (await restarted.Start(Transfer, "")).Reference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Start(Transfer with { AmountKes = 200 }, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Start(Transfer with { Destination = "other" }, ""));
        Assert.NotEqual(results[0].Reference, (await restarted.Start(Transfer with { StoreId = "other-store" }, "")).Reference);
    }

    [Fact]
    public async Task LostAcknowledgementAndLostCallbackRecoverThroughSharedReconciliation()
    {
        var ledger = new MemoryLedger();
        var provider = Provider(ledger);
        var workflow = new MpesaWorkflow(ledger, provider);
        var transfer = Transfer with { OrderId = "mock-lost-ack-order" };
        var unknown = await workflow.Start(transfer, "");
        Assert.Equal(MpesaLegState.Unknown, unknown.Collection);
        var pending = await workflow.Reconcile(transfer.StoreId, transfer.OrderId, "");
        Assert.Equal(MpesaLegState.Pending, pending.Collection);
        var paid = new MpesaObservation(pending.Reference, 100, transfer.Destination,
            MpesaLegState.Succeeded, MpesaLegState.Pending, pending.CheckoutRequestId);
        await provider.Callback(pending.Reference, paid, default); // callback delivery lost
        Assert.Equal(MpesaLegState.Pending, (await workflow.Get(transfer.StoreId, transfer.OrderId))!.Collection);
        var recovered = await new MpesaWorkflow(ledger, Provider(ledger)).Reconcile(transfer.StoreId, transfer.OrderId, "");
        Assert.Equal(MpesaLegState.Succeeded, recovered.Collection);
        Assert.Equal(MpesaLegState.Pending, recovered.Disbursement);
        var settled = paid with { Disbursement = MpesaLegState.Succeeded };
        await provider.Callback(pending.Reference, settled, default);
        var result = await workflow.Apply(transfer.StoreId, transfer.OrderId, settled);
        Assert.False(result.NeedsReconciliation);
        Assert.Equal(MpesaLegState.Succeeded, (await workflow.Apply(transfer.StoreId, transfer.OrderId, settled)).Disbursement);
        Assert.Equal(MpesaLegState.Succeeded, (await workflow.Apply(transfer.StoreId, transfer.OrderId, paid)).Disbursement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Apply(transfer.StoreId, transfer.OrderId,
            settled with { Disbursement = MpesaLegState.Failed }));
    }

    [Fact]
    public async Task FailedAndMismatchedEventsCannotSettleOrResubmit()
    {
        var ledger = new MemoryLedger();
        var workflow = new MpesaWorkflow(ledger, Provider(ledger));
        var entry = await workflow.Start(Transfer, "");
        var observation = new MpesaObservation(entry.Reference, 100, Transfer.Destination,
            MpesaLegState.Succeeded, MpesaLegState.Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Apply("store", "order", observation with { AmountKes = 101 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Apply("store", "order", observation with { Destination = "wrong" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Apply("store", "order", observation with { Collection = MpesaLegState.Pending }));
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.Apply("other-store", "order", observation));
        await workflow.Apply("store", "order", observation with { Collection = MpesaLegState.Failed, Disbursement = MpesaLegState.Unknown });
        Assert.Equal(MpesaLegState.Failed, (await workflow.Start(Transfer, "")).Collection);
        Assert.Equal(2, ledger.Count);
    }

    [Fact]
    public async Task OutageAndSpliceWithoutApprovedContractNeverClaimSuccess()
    {
        var ledger = new MemoryLedger();
        var workflow = new MpesaWorkflow(ledger, Provider(ledger));
        var transfer = Transfer with { OrderId = "mock-outage-order" };
        Assert.Equal(MpesaLegState.Unknown, (await workflow.Start(transfer, "")).Collection);
        Assert.Equal(MpesaLegState.Unknown, (await workflow.Reconcile("store", transfer.OrderId, "")).Collection);
        Assert.Equal(1, ledger.Count); // Only intent exists; no provider transfer.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MpesaWorkflow(ledger, new SpliceWorkflowProvider()).Start(Transfer, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.Start(Transfer with { AmountKes = 1.5m }, ""));
        Assert.Throws<InvalidOperationException>(() => new DevelopmentMpesaWorkflowProvider(ledger,
            new TestEnvironment { EnvironmentName = "Production" }));
    }

    // Copy through JSON on every read/write to exercise restart/serialization behavior.
    private sealed class MemoryLedger : MpesaLedger
    {
        private readonly Dictionary<string, string> rows = new();
        public int Count { get { lock (rows) return rows.Count; } }
        public override Task<MpesaLedgerEntry?> Read(string key, CancellationToken ct = default)
        {
            lock (rows) return Task.FromResult(rows.TryGetValue(key, out var value) ? JsonConvert.DeserializeObject<MpesaLedgerEntry>(value) : null);
        }
        public override Task<MpesaLedgerEntry?> Update(string key, Func<MpesaLedgerEntry?, MpesaLedgerEntry?> update, CancellationToken ct = default)
        {
            lock (rows)
            {
                var result = update(rows.TryGetValue(key, out var value) ? JsonConvert.DeserializeObject<MpesaLedgerEntry>(value) : null);
                if (result is not null) rows[key] = JsonConvert.SerializeObject(result);
                return Task.FromResult(result);
            }
        }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tando.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
