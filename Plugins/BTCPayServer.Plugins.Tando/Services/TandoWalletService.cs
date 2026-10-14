using System;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Dapper;
using Microsoft.EntityFrameworkCore;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoWalletService(StoreRepository storeRepository, InvoiceRepository invoiceRepository,
    BTCPayNetworkProvider networkProvider, ApplicationDbContextFactory dbContextFactory)
{
    private const string BackupKey = "tandoWalletBackup";
    private const string NodeKey = "tandoNodeRegistration";
    public const string PaymentProofKey = "tandoLightningPayment";

    public async Task<TandoWalletBackup?> GetBackup(string storeId) => await Read<TandoWalletBackup>(storeId, BackupKey);

    public async Task<TandoWalletBackup?> SaveBackup(string storeId, TandoSaveWalletBackupRequest request)
    {
        var backup = new TandoWalletBackup
        {
            Ciphertext = request.Ciphertext,
            Nonce = request.Nonce,
            CredentialId = request.CredentialId,
            PrfSalt = request.PrfSalt,
            CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = null
        };
        return await Write(storeId, BackupKey, backup) ? backup : null;
    }

    public async Task<(bool Success, string? Error)> VerifyBackup(string storeId)
    {
        var backup = await GetBackup(storeId);
        if (backup is null)
            return (false, "backup_not_found");
        backup.VerifiedAt ??= DateTimeOffset.UtcNow;
        return await Write(storeId, BackupKey, backup) ? (true, null) : (false, "store_not_found");
    }

    public async Task<TandoNodeRegistration?> GetNode(string storeId) => await Read<TandoNodeRegistration>(storeId, NodeKey);

    public async Task<(TandoNodeRegistration? Node, string? Error)> ClaimNode(string storeId, TandoClaimNodeRequest request)
    {
        if (!NBitcoin.PubKey.TryCreatePubKey(Encoders.Hex.DecodeData(request.NodePubkey), out _))
            return (null, "invalid_node_pubkey");

        var backup = await GetBackup(storeId);
        if (backup?.VerifiedAt is null)
            return (null, "backup_not_verified");

        var current = await GetNode(storeId);
        var node = new TandoNodeRegistration
        {
            NodePubkey = request.NodePubkey.ToLowerInvariant(),
            DeviceId = request.DeviceId,
            Epoch = (current?.Epoch ?? 0) + 1,
            ClaimedAt = DateTimeOffset.UtcNow
        };
        return await Write(storeId, NodeKey, node) ? (node, null) : (null, "store_not_found");
    }

    public async Task<(TandoLightningPaymentProof? Proof, string? Error)> RecordLightningPayment(
        string storeId, string invoiceId, TandoRecordLightningPaymentRequest request)
    {
        var backup = await GetBackup(storeId);
        if (backup?.VerifiedAt is null)
            return (null, "backup_not_verified");

        var node = await GetNode(storeId);
        if (node is null)
            return (null, "node_not_registered");
        if (request.Epoch != node.Epoch)
            return (null, "stale_node");

        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.StoreId != storeId)
            return (null, "invoice_not_found");

        var network = networkProvider.BTC.NBitcoinNetwork;
        if (!BOLT11PaymentRequest.TryParse(request.Bolt11, out var bolt11, network) || bolt11 is null)
            return (null, "invalid_bolt11");
        if (!string.Equals(bolt11.GetPayeePubKey().ToHex(), node.NodePubkey, StringComparison.OrdinalIgnoreCase))
            return (null, "wrong_payee");
        if (bolt11.MinimumAmount.MilliSatoshi <= 0)
            return (null, "amount_required");

        byte[] preimage;
        try { preimage = Encoders.Hex.DecodeData(request.Preimage); }
        catch (FormatException) { return (null, "invalid_preimage"); }
        // PaymentHash prints as the hash's natural hex, which is what hex-encoding SHA256(preimage) gives.
        var paymentHash = bolt11.PaymentHash!.ToString();
        if (preimage.Length != 32 || !string.Equals(Encoders.Hex.EncodeData(Hashes.SHA256(preimage)), paymentHash, StringComparison.OrdinalIgnoreCase))
            return (null, "preimage_mismatch");

        // Reporting the same payment again for the same invoice is fine; reusing it for another invoice is not.
        var existing = invoice.Metadata?.AdditionalData is { } data && data.TryGetValue(PaymentProofKey, out var token) && token.Type == JTokenType.Object
            ? token.ToObject<TandoLightningPaymentProof>()
            : null;
        if (existing is not null)
            return existing.PaymentHash == paymentHash ? (existing, null) : (null, "invoice_already_paid");
        if (invoice.Status == InvoiceStatus.Settled)
            return (null, "invoice_already_paid");
        if (await PaymentHashUsedElsewhere(storeId, invoiceId, paymentHash))
            return (null, "payment_already_used");

        var proof = new TandoLightningPaymentProof
        {
            Bolt11 = request.Bolt11,
            PaymentHash = paymentHash,
            Preimage = request.Preimage.ToLowerInvariant(),
            AmountSats = (long)(bolt11.MinimumAmount.MilliSatoshi / 1000),
            NodePubkey = node.NodePubkey,
            RecordedAt = DateTimeOffset.UtcNow
        };
        await invoiceRepository.UpdateInvoiceMetadata(invoiceId, PaymentProofKey, proof);

        // v1: BTCPay 2.4.1 has no external payment method, so the invoice is marked settled and the proof above is the
        // record of what was paid. Moving to a real payment entry (own payment handler, or #6635 upstream) won't change the API.
        if (!await invoiceRepository.MarkInvoiceStatus(invoiceId, InvoiceStatus.Settled))
            return (null, "invoice_cannot_be_settled");
        return (proof, null);
    }

    private async Task<bool> PaymentHashUsedElsewhere(string storeId, string invoiceId, string paymentHash)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var hit = await RelationalDatabaseFacadeExtensions.GetDbConnection(ctx.Database).ExecuteScalarAsync<int?>(
            """
            SELECT 1 FROM "Invoices"
            WHERE "StoreDataId" = @storeId
              AND "Id" <> @invoiceId
              AND "Blob2"->'metadata'->'tandoLightningPayment'->>'PaymentHash' = @paymentHash
            LIMIT 1
            """,
            new { storeId, invoiceId, paymentHash });
        return hit is not null;
    }

    private async Task<T?> Read<T>(string storeId, string key) where T : class
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return null;
        return store.GetStoreBlob().AdditionalData.TryGetValue(key, out var token) && token.Type == JTokenType.Object
            ? token.ToObject<T>()
            : null;
    }

    private async Task<bool> Write<T>(string storeId, string key, T value) where T : class
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return false;
        var blob = store.GetStoreBlob();
        blob.AdditionalData[key] = JToken.FromObject(value);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return true;
    }
}