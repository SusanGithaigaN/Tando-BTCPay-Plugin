using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BTCPayServer.Plugins.Tando.Services;

public record TandoPayment(string Id, string Status, string? LightningInvoice, long SatsTotal, string? PaymentNetworkReceipt, string? FailureReason);

public record TandoApiResult<T>(T? Value, string? ErrorCode, int StatusCode)
{
    public bool Success => ErrorCode is null;
}

public class TandoPaymentsClient(IHttpClientFactory httpClientFactory, TandoSubscriptionService subscriptionService)
{
    public const string HttpClientName = "TandoPayments";

    private static readonly JsonSerializerSettings BodySettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    public async Task<TandoApiResult<TandoPayment>> CreatePayment(decimal amountKes, TandoMpesaDestinationType destinationType,
        string destination, string? accountNumber, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            currencyAmount = amountKes,
            transactionType = ToTransactionType(destinationType),
            payToAccount = destination,
            payToRoutingNumber = destinationType == TandoMpesaDestinationType.PayBill ? accountNumber : null,
            paymentNetwork = "mpesa",
            idempotencyKey
        };
        return ReadPayment(await Send(HttpMethod.Post, "/v2/transactions/create", body, cancellationToken));
    }

    public async Task<TandoApiResult<TandoPayment>> GetPayment(string id, CancellationToken cancellationToken = default)
        => ReadPayment(await Send(HttpMethod.Get, $"/v2/transactions/{Uri.EscapeDataString(id)}", null, cancellationToken));

    public async Task<TandoApiResult<TandoPayment>> RecreateInvoice(string id, CancellationToken cancellationToken = default)
        => ReadPayment(await Send(HttpMethod.Post, $"/v2/transactions/{Uri.EscapeDataString(id)}/recreate-invoice", null, cancellationToken));

    public async Task<TandoApiResult<string>> LookupMerchant(TandoMpesaDestinationType destinationType, string account,
        CancellationToken cancellationToken = default)
    {
        var (json, error, status) = await Send(HttpMethod.Post, "/v2/merchant",
            new { account, transactionType = ToTransactionType(destinationType) }, cancellationToken);
        return json is null ? new(null, error, status) : new(json["merchantName"]?.ToString(), null, status);
    }

    private async Task<(JObject? Json, string? ErrorCode, int StatusCode)> Send(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        var settings = await subscriptionService.GetSettings();
        if (!settings.IsTandoPayoutConfigured)
            return (null, "tando_api_not_configured", 0);

        var url = settings.TandoApiBaseUrl!.TrimEnd('/') + path;
        var bytes = Encoding.UTF8.GetBytes(body is null ? "" : JsonConvert.SerializeObject(body, BodySettings));

        using var message = new HttpRequestMessage(method, url);
        message.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(settings.TandoApiKeyId!,
            DecodeSecret(settings.TandoApiSecret!), method.Method, url, bytes, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString()));
        if (body is not null)
        {
            // Send the exact bytes that were hashed, so the signature matches.
            message.Content = new ByteArrayContent(bytes);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            JObject? json = null;
            try { json = JObject.Parse(text); } catch (JsonReaderException) { }

            if (response.IsSuccessStatusCode && json?["success"]?.Type == JTokenType.Boolean && json["success"]!.Value<bool>())
                return (json, null, (int)response.StatusCode);

            var code = json?["error"]?["errorCode"]?.ToString();
            return (null, string.IsNullOrEmpty(code) ? $"tando_http_{(int)response.StatusCode}" : code, (int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return (null, "tando_unreachable", 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "tando_timeout", 0);
        }
    }

    private static TandoApiResult<TandoPayment> ReadPayment((JObject? Json, string? ErrorCode, int StatusCode) result)
    {
        if (result.Json?["transaction"] is not JObject tx || string.IsNullOrEmpty(tx["id"]?.ToString()))
            return new(null, result.ErrorCode ?? "tando_bad_response", result.StatusCode);

        return new(new TandoPayment(
            tx["id"]!.ToString(),
            tx["status"]?.ToString() ?? "",
            tx["lightningInvoice"]?.ToString(),
            tx["satsTotal"]?.Value<long?>() ?? 0,
            tx["paymentNetworkReceipt"]?.ToString(),
            tx["failureReason"]?.ToString()), null, result.StatusCode);
    }

    public static string BuildAuthorizationHeader(string keyId, byte[] secret, string method, string url, byte[] body, long ts, string nonce)
    {
        var timestamp = ts.ToString(CultureInfo.InvariantCulture);
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join('\n', "tando-hmac-1", method.ToUpperInvariant(), url, timestamp, nonce, bodyHash);
        var sig = Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)));
        return $"Tando-HMAC keyId={keyId}, ts={timestamp}, nonce={nonce}, sig={sig}";
    }

    public static byte[] DecodeSecret(string secret)
    {
        var s = secret.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static bool IsValidSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        try { return DecodeSecret(secret).Length == 32; }
        catch (FormatException) { return false; }
    }

    public static string ToTransactionType(TandoMpesaDestinationType type) => type switch
    {
        TandoMpesaDestinationType.MobileNumber => "SendMoney",
        TandoMpesaDestinationType.TillNumber => "BuyGoods",
        TandoMpesaDestinationType.PayBill => "PayBill",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}