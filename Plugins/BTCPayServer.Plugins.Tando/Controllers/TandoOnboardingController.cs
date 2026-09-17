using System;
using System.Linq;
using BTCPayServer.Plugins.Tando.Helper;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/")]
[Authorize(
    Policy = Policies.CanModifyStoreSettingsUnscoped,
    AuthenticationSchemes = AuthenticationSchemes.Greenfield
)]
[IgnoreAntiforgeryToken]
public class TandoOnboardingController(
    StoreRepository storeRepository,
    TandoSubscriptionService subscriptionService,
    TandoProductProvisioningService productProvisioningService,
    PhoneVerificationProvider phoneVerification
) : Controller
{
    private const string PreferredRateSource = "bitcoinkenya";
    private const string DefaultCurrency = "KES";
    private const string PhoneMetadataKey = "tandoPhoneNumber";
    private const string PlanMetadataKey = "tandoSubscriptionPlanId";

    [HttpGet("verification/status")]
    public IActionResult VerificationStatus() => Ok(new
    {
        mode = phoneVerification.Mode,
        mock = phoneVerification.IsMock
    });

    [HttpGet("subscription/status")]
    public async Task<IActionResult> SubscriptionStatus([FromQuery] string phoneNumber)
    {
        var normalizedPhone = NormalizePhone(phoneNumber, out var error);
        if (normalizedPhone is null)
            return error!;

        var status = await subscriptionService.GetStatus(normalizedPhone);
        return Ok(status);
    }

    [HttpGet("subscription/plans")]
    public async Task<IActionResult> SubscriptionPlans()
    {
        var plans = await subscriptionService.GetAvailablePlans();
        if (plans is null)
            return Ok(new { configured = false, plans = Array.Empty<TandoPlan>() });

        return Ok(new { configured = true, plans });
    }

    /// Verify identity before the existing trial subscription and store creation flow.
    [HttpPost("signup")]
    public async Task<IActionResult> Signup(
        [FromBody] TandoSignupRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(
                new
                {
                    error = "phone_number_required",
                    message = "A Safaricom phone number is required to sign up.",
                }
            );

        if (string.IsNullOrWhiteSpace(request.IdNumber))
            return BadRequest(
                new
                {
                    error = "id_number_required",
                    message = "Your National ID or Passport number is required for mobile number verification.",
                }
            );

        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var error);
        if (normalizedPhone is null)
            return error!;

        // KYC runs first — an invalid phone/ID pair is rejected before anything else.
        var (phoneNumberVerified, kycError) = await ValidateKyc(
            normalizedPhone,
            request.IdNumber,
            request.IdType
        );
        if (kycError is not null)
            return kycError;

        var status = await subscriptionService.GetStatus(normalizedPhone);

        if (!status.Configured)
            return StatusCode(
                503,
                new
                {
                    error = "subscription_not_configured",
                    message = "Tando is not yet available for sign-up. Please try again later.",
                }
            );

        if (status.Active)
            return await CreateOrReturnStore(
                normalizedPhone,
                status,
                phoneNumberVerified,
                cancellationToken
            );

        try
        {
            status = await subscriptionService.CreateFreeTrialSubscriber(
                normalizedPhone,
                Request.GetRequestBaseUrl(),
                cancellationToken
            );
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = "subscription_not_configured", message = ex.Message });
        }

        if (!status.Active)
            return StatusCode(500, new { error = "subscriber_creation_failed" });

        return await CreateOrReturnStore(
            normalizedPhone,
            status,
            phoneNumberVerified,
            cancellationToken
        );
    }

    /// Runs Daraja Mobile Number Validation KYC.
    /// Returns (true, null) only after successful verification,
    /// or (false, errorResult) when the phone/ID pair is rejected or the API is unavailable.
    private async Task<(bool verified, IActionResult? error)> ValidateKyc(
        string normalizedPhone,
        string idNumber,
        string idType
    )
    {
        var idTypeTrimmed = string.IsNullOrWhiteSpace(idType) ? "01" : idType.Trim();
        if (idTypeTrimmed is not ("01" or "02" or "05"))
            return (
                false,
                BadRequest(
                    new
                    {
                        error = "invalid_id_type",
                        message = "id_type must be 01 (National ID), 02 (Military ID), or 05 (Passport).",
                    }
                )
            );

        var validation = await phoneVerification.ValidateMobileNumber(
            normalizedPhone,
            idTypeTrimmed,
            idNumber.Trim()
        );

        if (!validation.Configured)
            return (false, StatusCode(503, new { error = "kyc_not_configured", message = "Phone number verification is not configured." }));

        if (validation.ServiceError)
            return (false, StatusCode(503, new { error = "phone_validation_unavailable", message = "Could not verify your phone number with Safaricom. Please try again later." }));

        if (!validation.Matches)
            return (
                false,
                BadRequest(
                    new
                    {
                        error = "phone_id_mismatch",
                        message = "The phone number is not registered under the provided ID. Please check your details and try again.",
                    }
                )
            );

        return (true, null);
    }

    private async Task<IActionResult> CreateOrReturnStore(
        string normalizedPhone,
        TandoSubscriptionStatus status,
        bool phoneNumberVerified,
        CancellationToken cancellationToken
    )
    {
        var callerId = User.GetId();
        var userStore = await storeRepository.GetStoresByUserId(callerId);
        var existingStore = userStore.FirstOrDefault(s => s.StoreName == normalizedPhone);
        if (existingStore is not null)
        {
            await RefreshPlanMetadata(existingStore, status.PlanId);
            var (posAppId, cartAppId) = await productProvisioningService.ProvisionDefaultApps(
                existingStore
            );
            return Ok(
                new TandoSignupResponse
                {
                    StoreId = existingStore.Id,
                    PhoneNumber = normalizedPhone,
                    AlreadyExisted = true,
                    PosAppId = posAppId,
                    CartAppId = cartAppId,
                    PhoneNumberVerified = phoneNumberVerified,
                }
            );
        }

        var store = await storeRepository.GetDefaultStoreTemplate();
        store.StoreName = normalizedPhone;
        var blob = store.GetStoreBlob();
        blob.DefaultCurrency = DefaultCurrency;
        var rate = blob.GetOrCreateRateSettings(false);
        rate.PreferredExchange = PreferredRateSource;
        rate.RateScripting = false;
        blob.AdditionalData[PhoneMetadataKey] = normalizedPhone;
        blob.AdditionalData[PlanMetadataKey] = status.PlanId;
        store.SetStoreBlob(blob);
        var result = await storeRepository.CreateStore(callerId, store);
        if (result != StoreRepository.CreateStoreResult.Created)
            return BadRequest(new { error = "store_creation_failed", detail = result.ToString() });

        var (newPosAppId, newCartAppId) = await productProvisioningService.ProvisionDefaultApps(
            store
        );
        return Ok(
            new TandoSignupResponse
            {
                StoreId = store.Id,
                PhoneNumber = normalizedPhone,
                AlreadyExisted = false,
                PosAppId = newPosAppId,
                CartAppId = newCartAppId,
                PhoneNumberVerified = phoneNumberVerified,
            }
        );
    }

    private async Task RefreshPlanMetadata(StoreData store, string? currentPlanId)
    {
        var blob = store.GetStoreBlob();
        if (blob.AdditionalData[PlanMetadataKey]?.ToString() == currentPlanId)
            return;

        blob.AdditionalData[PlanMetadataKey] = currentPlanId;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
    }

    private string? NormalizePhone(string phoneNumber, out IActionResult? error)
    {
        var normalized = KenyanPhoneNumber.Normalize(phoneNumber);
        if (normalized is null)
        {
            error = BadRequest(
                new
                {
                    error = "invalid_phone_number",
                    message = "Expected a Kenyan MSISDN, e.g. 0712345678 or +254712345678.",
                }
            );
            return null;
        }
        error = null;
        return normalized;
    }

    [HttpPut("stores/{storeId}/lightning/connect")]
    public async Task<IActionResult> ConnectLightning(
        string storeId,
        [FromBody] TandoConnectLightningRequest request
    )
    {
        if (string.IsNullOrWhiteSpace(request?.ConnectionString))
            return BadRequest(new { error = "connection_string_required" });

        var callerId = User.GetId();
        var ownedStores = await storeRepository.GetStoresByUserId(callerId);
        // Do not reveal another user's store through this endpoint.
        var store = ownedStores.FirstOrDefault(s => s.Id == storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = request.ConnectionString,
        };
        store.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(config));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, false);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return Ok(new { storeId, paymentMethodId = paymentMethodId.ToString() });
    }
}
