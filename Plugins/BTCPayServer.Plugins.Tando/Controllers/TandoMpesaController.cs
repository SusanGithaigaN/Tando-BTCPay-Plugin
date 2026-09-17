using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/stores/{storeId}/mpesa")]
[IgnoreAntiforgeryToken]
public class TandoMpesaController(
    StoreRepository storeRepository,
    TandoMerchantSettingsService merchantSettings,
    MpesaPaymentProvider splicePsp,
    MpesaWorkflow workflow,
    MpesaWorkflowProvider workflowProvider,
    ILogger<TandoMpesaController> logger
) : Controller
{
    /// Called by the mobile app to initiate an STK Push for a customer payment.
    [HttpPost("pay")]
    [Authorize(
        Policy = Policies.CanModifyStoreSettings,
        AuthenticationSchemes = AuthenticationSchemes.Greenfield
    )]
    public async Task<IActionResult> InitiatePay(
        string storeId,
        [FromBody] MpesaPayRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CustomerPhone))
            return BadRequest(new { error = "customer_phone_required" });
        if (request.AmountKes <= 0 || request.AmountKes != decimal.Truncate(request.AmountKes))
            return BadRequest(
                new { error = "invalid_amount", detail = "Amount must be a positive whole-shilling value." }
            );
        if (string.IsNullOrWhiteSpace(request.OrderId))
            return BadRequest(new { error = "order_id_required" });

        var phoneError = splicePsp.NormalizePhone(request.CustomerPhone, out var normalizedPhone);
        if (phoneError is not null)
            return BadRequest(new { error = "invalid_phone", detail = phoneError });

        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var savedDestination = await merchantSettings.GetMpesaSettings(storeId);
        var merchantDestination = savedDestination?.DestinationType == TandoMpesaDestinationType.PayBill
            && !string.IsNullOrWhiteSpace(savedDestination.AccountNumber)
                ? $"{savedDestination.Destination}/{savedDestination.AccountNumber}"
                : savedDestination?.Destination;
        if (string.IsNullOrWhiteSpace(merchantDestination))
            return StatusCode(
                503,
                new
                {
                    error = "merchant_mpesa_not_configured",
                    detail = "The merchant has not set up their M-Pesa destination yet.",
                }
            );

        var callbackUrl = Url.Action(
            nameof(SpliceCallback),
            "TandoMpesa",
            new { storeId },
            Request.Scheme
        )!;

        try
        {
            var payment = await workflow.Start(new MpesaTransfer(storeId, request.OrderId,
                normalizedPhone!, merchantDestination, request.AmountKes), callbackUrl, cancellationToken);
            return Ok(payment);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(ex.Message == "mpesa_provider_not_ready" ? 503 : 409, new { error = ex.Message });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// Saves or updates the merchant's M-Pesa destination identifier on their store.
    [HttpPut("settings")]
    [Authorize(
        Policy = Policies.CanModifyStoreSettings,
        AuthenticationSchemes = AuthenticationSchemes.Greenfield
    )]
    public async Task<IActionResult> SaveMpesaSettings(
        string storeId,
        [FromBody] TandoMpesaSettingsRequest request
    )
    {
        if (string.IsNullOrWhiteSpace(request?.Destination))
            return BadRequest(new { error = "destination_required" });

        var validationError = merchantSettings.ValidateMpesaSettings(request);
        if (validationError is not null)
            return BadRequest(new { error = "validation_failed", field = validationError.Field, message = validationError.Message });

        var saved = await merchantSettings.SaveMpesaSettings(storeId, request);
        if (!saved)
            return NotFound(new { error = "store_not_found" });
        var destination = request.DestinationType == TandoMpesaDestinationType.PayBill
            && !string.IsNullOrWhiteSpace(request.AccountNumber)
                ? $"{request.Destination}/{request.AccountNumber}"
                : request.Destination!.Trim();

        return Ok(new { storeId, destination });
    }

    [HttpGet("status")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult ProviderStatus() => Ok(new { provider = workflowProvider.Name, ready = workflowProvider.Ready });

    [HttpGet("payments/{orderId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetPayment(string storeId, string orderId, CancellationToken cancellationToken)
    {
        var payment = await workflow.Get(storeId, orderId, cancellationToken);
        return payment is null ? NotFound(new { error = "payment_not_found" }) : Ok(payment);
    }

    [HttpPost("payments/{orderId}/reconcile")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> Reconcile(string storeId, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            var callbackUrl = Url.Action(nameof(SpliceCallback), "TandoMpesa", new { storeId }, Request.Scheme)!;
            return Ok(await workflow.Reconcile(storeId, orderId, callbackUrl, cancellationToken));
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Development callback simulation uses store-scoped Greenfield authentication.
    // It cannot act on real provider records and is absent when mock mode is not selected.
    [HttpPost("payments/{orderId}/mock-callback")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> MockCallback(string storeId, string orderId,
        [FromBody] MockMpesaCallback request, CancellationToken cancellationToken)
    {
        if (workflowProvider is not DevelopmentMpesaWorkflowProvider mock) return NotFound();
        if (request is null || !ModelState.IsValid) return BadRequest(new { error = "invalid_callback" });
        try
        {
            var entry = await workflow.Get(storeId, orderId, cancellationToken);
            if (entry is null) return NotFound(new { error = "payment_not_found" });
            if (entry.Provider != mock.Name) return Conflict(new { error = "provider_changed" });
            var observation = new MpesaObservation(entry.Reference, request.AmountKes, request.Destination,
                request.Collection, request.Disbursement, entry.CheckoutRequestId);
            // Persist the external outcome first. Skipping delivery simulates a lost callback.
            observation = await mock.Callback(entry.Reference, observation, cancellationToken);
            return request.Deliver
                ? Ok(await workflow.Apply(storeId, orderId, observation, cancellationToken))
                : Ok(new { delivered = false });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("callback")]
    [AllowAnonymous]
    public IActionResult SpliceCallback(string storeId, [FromBody] JObject payload) =>
        StatusCode(503, new { error = "splice_callback_contract_not_configured" });
}

public class MockMpesaCallback
{
    public decimal AmountKes { get; set; }
    public string Destination { get; set; }
    public MpesaLegState Collection { get; set; }
    public MpesaLegState Disbursement { get; set; }
    public bool Deliver { get; set; } = true;
}

[Route("~/plugins/api/tando/splice")]
[IgnoreAntiforgeryToken]
[AllowAnonymous]
public class TandoSpliceWebhookController : Controller
{
    // Do not acknowledge unverified callbacks or assert that an unknown identity is known.
    [HttpGet("identity")]
    public IActionResult Identity([FromQuery] string phone) => Unconfigured();
    [HttpPost("webhook/received")]
    public IActionResult PaymentReceived([FromBody] JObject payload) => Unconfigured();
    [HttpPost("webhook/sent")]
    public IActionResult PaymentSent([FromBody] JObject payload) => Unconfigured();
    private IActionResult Unconfigured() =>
        StatusCode(503, new { error = "splice_callback_contract_not_configured" });
}
