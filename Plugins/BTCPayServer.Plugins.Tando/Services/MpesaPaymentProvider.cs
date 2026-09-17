using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

public abstract class MpesaPaymentProvider
{
    public abstract string? NormalizePhone(string rawPhone, out string? normalized);

    public abstract Task<StkPushResult> InitiateStkPush(string customerMsisdn, string merchantDestination,
        decimal amountKes, string orderId, string callbackUrl, CancellationToken cancellationToken = default);
}

public record StkPushResult(bool Success, string? CheckoutRequestId, string? Error);
