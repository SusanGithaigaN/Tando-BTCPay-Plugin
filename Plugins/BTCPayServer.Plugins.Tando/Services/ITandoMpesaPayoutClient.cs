using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.ViewModels;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

public interface ITandoMpesaPayoutClient
{
    Task<TandoPayoutTriggerResult> TriggerPayout(TandoPayoutRequest request);
}

public class UnconfiguredTandoMpesaPayoutClient : ITandoMpesaPayoutClient
{
    public Task<TandoPayoutTriggerResult> TriggerPayout(TandoPayoutRequest request)
    {
        return Task.FromResult(new TandoPayoutTriggerResult(Success: false, PayoutReference: null, Error: "mpesa_payout_client_not_configured"));
    }
}

public class HttpTandoMpesaPayoutClient(IHttpClientFactory httpClientFactory, TandoSubscriptionService subscriptionService) : ITandoMpesaPayoutClient
{
    public async Task<TandoPayoutTriggerResult> TriggerPayout(TandoPayoutRequest request)
    {
        var settings = await subscriptionService.GetSettings();
        if (!settings.IsMpesaPayoutConfigured)
            return new TandoPayoutTriggerResult(false, null, "mpesa_payout_client_not_configured");

        using var message = new HttpRequestMessage(HttpMethod.Post, settings.MpesaPayoutApiUrl);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MpesaPayoutApiKey);
        message.Headers.Add("Idempotency-Key", request.InvoiceId);
        message.Content = JsonContent.Create(new
        {
            reference = request.InvoiceId,
            amount = request.AmountKes,
            currency = "KES",
            destinationType = request.DestinationType.ToString(),
            destination = request.Destination
        });
        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(HttpTandoMpesaPayoutClient)).SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return new TandoPayoutTriggerResult(false, null, $"mpesa_payout_http_{(int)response.StatusCode}");

            JObject? json = null;
            try { json = JObject.Parse(body); } catch (Newtonsoft.Json.JsonReaderException) { }
            var reference = json?["reference"]?.ToString();
            return new TandoPayoutTriggerResult(true, string.IsNullOrWhiteSpace(reference) ? null : reference, null);
        }
        catch (HttpRequestException)
        {
            return new TandoPayoutTriggerResult(false, null, "mpesa_payout_unreachable");
        }
        catch (TaskCanceledException)
        {
            return new TandoPayoutTriggerResult(false, null, "mpesa_payout_timeout");
        }
    }
}

public record TandoPayoutRequest(string InvoiceId, string StoreId, decimal AmountKes,
    TandoMpesaDestinationType DestinationType, string Destination);

public record TandoPayoutTriggerResult(bool Success, string? PayoutReference, string? Error);
