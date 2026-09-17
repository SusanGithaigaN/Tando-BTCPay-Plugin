#nullable enable
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>Plugin-owned JSON records in BTCPay's Settings table, read without its settings cache.
/// Transaction-scoped PostgreSQL locks serialize creation and callbacks across server instances.
/// No network calls occur inside the transaction.</summary>
public sealed class BTCPayMpesaLedger(ApplicationDbContextFactory factory) : MpesaLedger
{
    public override async Task<MpesaLedgerEntry?> Read(string key, CancellationToken ct = default)
    {
        await using var db = factory.CreateContext();
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == key, ct);
        return row is null ? null : JsonConvert.DeserializeObject<MpesaLedgerEntry>(row.Value);
    }

    public override async Task<MpesaLedgerEntry?> Update(string key, Func<MpesaLedgerEntry?, MpesaLedgerEntry?> update, CancellationToken ct = default)
    {
        await using var strategyContext = factory.CreateContext();
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = factory.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockId = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", ct);
            var row = await db.Settings.SingleOrDefaultAsync(x => x.Id == key, ct);
            var result = update(row is null ? null : JsonConvert.DeserializeObject<MpesaLedgerEntry>(row.Value));
            if (result is not null)
            {
                if (row is null) { row = new SettingData { Id = key }; db.Settings.Add(row); }
                row.Value = JsonConvert.SerializeObject(result);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
