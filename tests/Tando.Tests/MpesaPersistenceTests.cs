using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Tando.Tests;

public class MpesaPersistenceTests
{
    [RegtestFact]
    public async Task ConcurrentDatabaseWritesAndNewRepositoryPreserveOneRecord()
    {
        var factory = new ApplicationDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("TANDO_TEST_POSTGRES")!
        }), NullLoggerFactory.Instance);
        var ledger = new BTCPayMpesaLedger(factory);
        var key = "Tando.Mpesa.Test." + Guid.NewGuid().ToString("N");
        var transfer = new MpesaTransfer("test-store", "test-order", "254701234567", "254702345678", 100);
        try
        {
            var entries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                ledger.Update(key, existing => existing ?? new MpesaLedgerEntry(transfer, key, "Mock"))));
            Assert.All(entries, entry => Assert.Equal(transfer, entry!.Transfer));
            await ledger.Update(key, entry => entry! with { Collection = MpesaLegState.Succeeded, Disbursement = MpesaLegState.Pending });
            var reloaded = await new BTCPayMpesaLedger(factory).Read(key);
            Assert.Equal(MpesaLegState.Succeeded, reloaded!.Collection);
            Assert.Equal(MpesaLegState.Pending, reloaded.Disbursement);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.Update(key, entry =>
                throw new InvalidOperationException("rollback")));
            Assert.Equal(reloaded, await ledger.Read(key));
        }
        finally
        {
            await using var db = factory.CreateContext();
            await db.Settings.Where(x => x.Id == key).ExecuteDeleteAsync();
        }
    }

    private sealed class RegtestFactAttribute : FactAttribute
    {
        public RegtestFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANDO_TEST_POSTGRES")))
                Skip = "Set TANDO_TEST_POSTGRES to an initialized BTCPay regtest database.";
        }
    }
}
