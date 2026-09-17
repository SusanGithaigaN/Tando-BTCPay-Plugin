using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Models.ServerViewModels;
using BTCPayServer.Plugins.Subscriptions;
using BTCPayServer.Plugins.Tando;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Template;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        /*services.AddSingleton<IUIExtension>(new UIExtension("TandoPluginHeaderNav", "header-nav"));*/
        services.AddSingleton<IUIExtension>(new UIExtension("TandoServerNav", "server-nav"));
        services.AddHostedService<TandoPlanFallbackHostedService>();
        services.AddSingleton<TandoSubscriptionService>();
        services.AddSingleton<SubscriptionHostedService>();
        services.AddScoped<TandoProductProvisioningService>();
        services.AddScoped<TandoMerchantSettingsService>();
        services.AddScoped<TandoSplitService>();
        services.AddTandoPhoneVerification();
        services.AddSingleton<MpesaPaymentProvider>(sp => sp.GetRequiredService<SplicePspService>());
        services.AddSingleton<MpesaPayoutProvider, UnconfiguredMpesaPayoutProvider>();
        services.AddSingleton<SplicePspService>();
        services.AddSingleton<MpesaLedger, BTCPayMpesaLedger>();
        services.AddSingleton<MpesaWorkflow>();
        services.AddSingleton<MpesaWorkflowProvider>(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var mode = config["Tando:Payments:Mode"] ?? "Splice";
            var environment = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            TandoProviderModeGuard.Validate(mode, "Splice", environment);
            if (string.Equals(mode, "Splice", System.StringComparison.OrdinalIgnoreCase))
                return new SpliceWorkflowProvider();
            return new DevelopmentMpesaWorkflowProvider(sp.GetRequiredService<MpesaLedger>(),
                sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>());
        });
        services.AddSingleton(new ServicesViewModel.OtherExternalService()
        {
            Name = "Tando",
            ControllerName = "UITandoSettings",
            ActionName = nameof(UITandoSettingsController.Settings)
        });
    }
}