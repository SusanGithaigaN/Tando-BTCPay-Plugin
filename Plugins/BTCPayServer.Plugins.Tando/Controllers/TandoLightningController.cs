using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/lightning")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoLightningController(StoreRepository storeRepository, TandoLightningProvisionerFactory lightningProvisionerFactory,
    PaymentMethodHandlerDictionary handlers, IAuthorizationService authorizationService) : Controller
{
    [HttpPut("connect")]
    public async Task<IActionResult> ConnectLightning(string storeId, [FromBody] TandoConnectLightningRequest request)
    {
        var ownedStore = (await storeRepository.GetStoresByUserId(User.GetId())).FirstOrDefault(s => s.Id == storeId);
        if (ownedStore is null)
            return NotFound(new { error = "store_not_found" });

        string connectionString;
        if (!string.IsNullOrWhiteSpace(request?.ConnectionString))
        {
            connectionString = request.ConnectionString.Trim();
        }
        else if (request?.LightningProvision is not null)
        {
            var provisioner = lightningProvisionerFactory.Get(request.LightningProvision.ProviderType);
            if (provisioner is null)
                return BadRequest(new { error = "unsupported_provider_type" });

            var result = await provisioner.Provision(request.LightningProvision);
            if (!result.IsSuccess)
                return BadRequest(new { error = result.Error });

            connectionString = result.ConnectionString!;
        }
        else
        {
            return BadRequest(new { error = "connection_string_required" });
        }

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var handler = (LightningLikePaymentHandler)handlers[paymentMethodId];
        var validation = new PaymentMethodConfigValidationContext(authorizationService, ModelState,
            new JObject { ["connectionString"] = connectionString }, User, ownedStore.GetPaymentMethodConfig(paymentMethodId), ownedStore);
        try
        {
            await handler.ValidatePaymentMethodConfig(validation);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "invalid_connection_string", detail = ex.Message });
        }
        if (validation.MissingPermission is not null)
            return StatusCode(403, new { error = "missing_permission", detail = validation.MissingPermission.Message });
        if (!ModelState.IsValid)
            return BadRequest(new
            {
                error = "invalid_connection_string",
                detail = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
            });

        var config = handler.ParsePaymentMethodConfig(validation.Config);

        // Only save a node that answers.
        try
        {
            await handler.GetNodeInfo(config, null, throws: true);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "lightning_node_unreachable", detail = ex.Message });
        }

        ownedStore.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(config, handler.Serializer));
        var blob = ownedStore.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, false);
        ownedStore.SetStoreBlob(blob);
        await storeRepository.UpdateStore(ownedStore);
        return Ok(new { storeId, paymentMethodId = paymentMethodId.ToString() });
    }
}