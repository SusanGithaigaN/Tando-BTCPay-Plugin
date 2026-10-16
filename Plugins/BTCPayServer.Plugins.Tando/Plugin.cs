using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Models.ServerViewModels;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.Services.Lightning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Tando;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.4.1" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<IUIExtension>(new UIExtension("TandoServerNav", "server-nav"));
        services.AddScoped<TandoSplitService>();
        services.AddScoped<TandoWalletService>();
        services.AddSingleton<TandoSubscriptionService>();
        services.AddScoped<TandoMerchantAccountService>();
        services.AddScoped<TandoMerchantSettingsService>();
        services.AddScoped<TandoProductProvisioningService>();
        services.AddHostedService<TandoSplitIndexHostedService>();
        services.AddHostedService<TandoPlanFallbackHostedService>();
        services.AddHostedService<TandoTreasuryPayoutHostedService>();
        services.AddSingleton<TandoLightningProvisionerFactory>();
        services.AddSingleton<ITandoLightningProvisioner, NwcLightningProvisioner>();
        services.AddSingleton<ITandoLightningProvisioner, PhoenixdLightningProvisioner>();
        //services.AddSingleton<ITandoMpesaPayoutClient, HttpTandoMpesaPayoutClient>();
        services.AddSingleton<ITandoMpesaPayoutClient, UnconfiguredTandoMpesaPayoutClient>();
        services.Configure<MvcOptions>(options => options.Filters.Add<TandoSubscriptionGateFilter>());
        services.AddHostedService<TandoInvoiceSettlementHostedService>();
        services.AddSingleton(new ServicesViewModel.OtherExternalService()
        {
            Name = "Tando",
            ControllerName = "UITandoSettings",
            ActionName = nameof(UITandoSettingsController.Settings)
        });
    }
}
