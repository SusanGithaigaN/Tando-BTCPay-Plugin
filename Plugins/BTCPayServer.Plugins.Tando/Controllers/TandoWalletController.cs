using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoWalletController(TandoWalletService walletService) : Controller
{
    [HttpGet("wallet/backup")]
    public async Task<IActionResult> GetBackup(string storeId)
    {
        var backup = await walletService.GetBackup(storeId);
        return backup is null ? NotFound(new { error = "backup_not_found" }) : Ok(backup);
    }

    [HttpPut("wallet/backup")]
    public async Task<IActionResult> SaveBackup(string storeId, [FromBody] TandoSaveWalletBackupRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Ciphertext) || string.IsNullOrWhiteSpace(request.Nonce)
            || string.IsNullOrWhiteSpace(request.CredentialId) || string.IsNullOrWhiteSpace(request.PrfSalt))
            return BadRequest(new { error = "backup_incomplete" });

        var backup = await walletService.SaveBackup(storeId, request);
        return backup is null ? NotFound(new { error = "store_not_found" }) : Ok(backup);
    }

    [HttpPost("wallet/backup/verify")]
    public async Task<IActionResult> VerifyBackup(string storeId)
    {
        var (success, error) = await walletService.VerifyBackup(storeId);
        return success ? Ok() : NotFound(new { error });
    }

    [HttpGet("wallet/node")]
    public async Task<IActionResult> GetNode(string storeId)
    {
        var node = await walletService.GetNode(storeId);
        return node is null ? NotFound(new { error = "node_not_registered" }) : Ok(node);
    }

    [HttpPut("wallet/node")]
    public async Task<IActionResult> ClaimNode(string storeId, [FromBody] TandoClaimNodeRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.NodePubkey) || string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "node_pubkey_and_device_required" });

        var (node, error) = await walletService.ClaimNode(storeId, request);
        return error switch
        {
            null => Ok(node),
            "store_not_found" => NotFound(new { error }),
            _ => BadRequest(new { error })
        };
    }

    [HttpPost("invoices/{invoiceId}/lightning-payment")]
    public async Task<IActionResult> RecordLightningPayment(string storeId, string invoiceId, [FromBody] TandoRecordLightningPaymentRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Bolt11) || string.IsNullOrWhiteSpace(request.Preimage))
            return BadRequest(new { error = "bolt11_and_preimage_required" });

        var (proof, error) = await walletService.RecordLightningPayment(storeId, invoiceId, request);
        return error switch
        {
            null => Ok(proof),
            "invoice_not_found" => NotFound(new { error }),
            "stale_node" or "invoice_already_paid" or "payment_already_used" => Conflict(new { error }),
            _ => BadRequest(new { error })
        };
    }
}