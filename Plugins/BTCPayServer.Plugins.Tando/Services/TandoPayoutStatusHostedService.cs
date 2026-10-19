using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>Tando has no callbacks, so this polls each paid split until Tando reports the M-Pesa payout done or failed.</summary>
public class TandoPayoutStatusHostedService(IServiceScopeFactory scopeFactory, TandoSubscriptionService subscriptionService,
    ILogger<TandoPayoutStatusHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if ((await subscriptionService.GetSettings()).IsTandoPayoutConfigured)
                    await PollOnce(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Checking Tando payout status failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnce(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var splits = scope.ServiceProvider.GetRequiredService<TandoSplitService>();
        var client = scope.ServiceProvider.GetRequiredService<TandoPaymentsClient>();

        foreach (var (invoiceId, storeId, transactionId) in await splits.ListPendingTandoPayouts())
        {
            var result = await client.GetPayment(transactionId, cancellationToken);
            if (result.Value is { } payment)
                await splits.ApplyTandoPaymentStatus(storeId, invoiceId, payment);
            else
                logger.LogDebug("Tando status for {TransactionId} unavailable: {Error}", transactionId, result.ErrorCode);
        }
    }
}