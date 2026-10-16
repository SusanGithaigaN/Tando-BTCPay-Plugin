using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoTreasuryPayoutHostedService(EventAggregator eventAggregator, IServiceScopeFactory scopeFactory,
    ILogger<TandoTreasuryPayoutHostedService> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    protected override void SubscribeToEvents() => Subscribe<PayoutEvent>();

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not PayoutEvent { Payout: { State: PayoutState.Completed or PayoutState.Cancelled } payout })
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TandoSplitService>().OnTreasuryPayoutUpdated(payout);
    }
}