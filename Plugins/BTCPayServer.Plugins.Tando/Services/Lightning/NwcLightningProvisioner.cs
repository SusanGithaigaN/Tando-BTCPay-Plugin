using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.Tando.Services.Lightning;

public class NwcLightningProvisioner(IEnumerable<ILightningConnectionStringHandler> handlers) : ITandoLightningProvisioner
{
    public TandoLightningProviderType ProviderType => TandoLightningProviderType.Nwc;

    public Task<TandoLightningProvisionResult> Provision(TandoLightningProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NwcConnectionUri))
            return Task.FromResult(new TandoLightningProvisionResult(null, "nwc_connection_uri_required"));

        if (!NwcConnectionString.TryParse(request.NwcConnectionUri, out _, out var error))
            return Task.FromResult(new TandoLightningProvisionResult(null, error));

        // NWC is handled by the Nostr plugin; without it installed no handler recognises the URI.
        if (!handlers.Any(h => h.GetType().FullName == "BTCPayServer.Plugins.NIP05.NostrWalletConnectLightningConnectionStringHandler"))
            return Task.FromResult(new TandoLightningProvisionResult(null, "nwc_requires_nostr_plugin"));

        return Task.FromResult(new TandoLightningProvisionResult(request.NwcConnectionUri, null));
    }
}