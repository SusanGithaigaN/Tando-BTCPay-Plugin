using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BTCPayServer.Plugins.Tando.Services;


public class TandoSubscriptionGateFilter(TandoSubscriptionService subscriptionService, StoreRepository storeRepository, 
    AppService appService) : IAsyncActionFilter
{
    public const string StorePhoneMetadataKey = "tandoPhoneNumber";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var storeId = await GetStoreCreatingInvoice(context);
        if (storeId is not null && await IsLapsed(storeId))
        {
            context.Result = new ObjectResult(new
            {
                error = "subscription_inactive",
                message = "This store's subscription has lapsed. The owner can renew it to start taking payments again."
            })
            { StatusCode = StatusCodes.Status402PaymentRequired };
            return;
        }
        await next();
    }

    // Only the endpoints that open a new sale. Everything else passes straight through.
    private async Task<string?> GetStoreCreatingInvoice(ActionExecutingContext context)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method) || context.ActionDescriptor is not ControllerActionDescriptor action)
            return null;

        switch (action.ControllerName, action.ActionName)
        {
            case ("GreenfieldInvoice", "CreateInvoice"):
                return context.RouteData.Values["storeId"] as string;
            case ("UIPointOfSale", "ViewPointOfSale"):
                if (context.RouteData.Values["appId"] is not string appId)
                    return null;
                return (await appService.GetApp(appId, null))?.StoreDataId;
            default:
                return null;
        }
    }

    private async Task<bool> IsLapsed(string storeId)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null
            || !store.GetStoreBlob().AdditionalData.TryGetValue(StorePhoneMetadataKey, out var phone)
            || phone?.ToString() is not { Length: > 0 } ownerPhone)
            return false; 

        var status = await subscriptionService.GetStatus(ownerPhone);
        // Phase is null when there's no subscriber record; Configured is false when subscriptions aren't set up.
        // Neither is a lapse, so neither locks the store.
        return status.Configured && status.Phase is not null && !status.Active;
    }
}