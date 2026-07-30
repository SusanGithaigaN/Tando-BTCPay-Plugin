using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoProductProvisioningService(AppService appService)
{
    private const string PosLabel = "POS";
    private const string CartLabel = "Cart";

    public async Task<(string PosAppId, string CartAppId)> ProvisionDefaultApps(StoreData store, CancellationToken cancellationToken = default)
    {
        var posApp = await CreatePosApp(store, PosLabel, PosViewType.Static, cancellationToken);
        var cartApp = await CreatePosApp(store, CartLabel, PosViewType.Cart, cancellationToken);
        return (posApp.Id, cartApp.Id);
    }

    public async Task<(bool HasPos, bool HasCart)> GetProvisioningStatus(string storeId, CancellationToken cancellationToken = default)
    {
        var apps = await appService.GetApps(new[] { storeId });
        var posApps = apps.Where(a => a.AppType == PointOfSaleAppType.AppType).ToList();
        var hasPos = posApps.Any(a => a.Name.EndsWith($"- {PosLabel}"));
        var hasCart = posApps.Any(a => a.Name.EndsWith($"- {CartLabel}"));
        return (hasPos, hasCart);
    }

    private async Task<AppData> CreatePosApp(StoreData store, string label, PosViewType viewType, CancellationToken cancellationToken)
    {
        var app = new AppData
        {
            StoreDataId = store.Id,
            Name = $"{store.StoreName} - {label}",
            AppType = PointOfSaleAppType.AppType
        };
        var settings = new PointOfSaleSettings
        {
            DefaultView = viewType,
            Title = label == PosLabel ? "Point of Sale" : "Shop",
            Currency = store.GetStoreBlob().DefaultCurrency,
            Template = string.Empty
        };
        app.SetSettings(settings);
        await appService.UpdateOrCreateApp(app);
        return app;
    }
}

