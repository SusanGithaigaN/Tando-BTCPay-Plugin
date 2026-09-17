using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoInvoiceSettlementHostedService(EventAggregator eventAggregator,
    ILogger<TandoInvoiceSettlementHostedService> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    protected override void SubscribeToEvents()
    {
        this.Subscribe<InvoiceEvent>();
    }

    // BTC invoice settlement must never trigger an M-Pesa transfer.
    protected override Task ProcessEvent(object evt, CancellationToken cancellationToken) => Task.CompletedTask;
}
