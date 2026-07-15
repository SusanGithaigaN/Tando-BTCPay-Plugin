using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>
/// Client for Safaricom's Mobile Number Validation API (Daraja).
/// Checks whether an MSISDN is registered under a given ID number on Safaricom's KYC database.
/// https://developer.safaricom.co.ke/apis/MobileNumberValidation
/// </summary>
public class DarajaMobileNumberValidationService(
    IHttpClientFactory httpClientFactory,
    ISettingsRepository settingsRepository,
    IMemoryCache memoryCache,
    ILogger<DarajaMobileNumberValidationService> logger)
{
    private const string SettingsKey = "TandoDarajaSettings";
    private const string TokenCacheKey = "TandoDarajaAccessToken";
    private const string SandboxBaseUrl = "https://sandbox.safaricom.co.ke";
    private const string ProductionBaseUrl = "https://api.safaricom.co.ke";

    public async Task<TandoDarajaSettings> GetSettings() =>
        await settingsRepository.GetSettingAsync<TandoDarajaSettings>(SettingsKey) ?? new TandoDarajaSettings();

    public async Task UpdateSettings(TandoDarajaSettings settings)
    {
        memoryCache.Remove(TokenCacheKey);
        await settingsRepository.UpdateSetting(settings, SettingsKey);
    }

    /// <param name="msisdn">Normalized phone number, 254XXXXXXXXX</param>
    /// <param name="idType">01 = National ID, 02 = Military ID, 05 = Passport</param>
    /// <param name="idNumber">ID number the phone is expected to be registered under</param>
    public async Task<DarajaValidationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber)
    {
        var settings = await GetSettings();
        if (!settings.IsConfigured())
            return new DarajaValidationResult(false, false, "Daraja credentials are not configured.");

        var baseUrl = settings.UseSandbox ? SandboxBaseUrl : ProductionBaseUrl;
        try
        {
            var token = await GetAccessToken(settings, baseUrl);
            var client = httpClientFactory.CreateClient(nameof(DarajaMobileNumberValidationService));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/KYC-validation/validateID");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = new JObject
            {
                ["requestRefID"] = Guid.NewGuid().ToString("N"),
                ["shortCode"] = settings.ShortCode,
                ["msisdn"] = msisdn,
                ["idType"] = idType,
                ["idNumber"] = idNumber
            };
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Daraja mobile number validation returned HTTP {StatusCode}: {Content}",
                    (int)response.StatusCode, content);
                return new DarajaValidationResult(false, false, $"Daraja returned HTTP {(int)response.StatusCode}.");
            }

            var json = JObject.Parse(content);
            var matches = string.Equals(json["status"]?.Value<string>(), "true", StringComparison.OrdinalIgnoreCase);
            return new DarajaValidationResult(true, matches, json["responseMessage"]?.Value<string>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling Daraja mobile number validation for {Msisdn}", msisdn);
            return new DarajaValidationResult(false, false, "Could not reach the Daraja API.");
        }
    }

    private async Task<string> GetAccessToken(TandoDarajaSettings settings, string baseUrl)
    {
        if (memoryCache.TryGetValue(TokenCacheKey, out string cached))
            return cached;

        var client = httpClientFactory.CreateClient(nameof(DarajaMobileNumberValidationService));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/oauth/v1/generate?grant_type=client_credentials");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ConsumerKey}:{settings.ConsumerSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        var token = json["access_token"]?.Value<string>()
            ?? throw new InvalidOperationException("Daraja token response did not contain access_token.");
        // expires_in is 3599 seconds; renew a bit early so a cached token is never used at the edge of expiry
        var expiresIn = json["expires_in"]?.Value<int?>() ?? 3599;
        memoryCache.Set(TokenCacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 120)));
        return token;
    }
}

/// <param name="Success">The API call itself succeeded (network, auth, subscription all OK)</param>
/// <param name="Matches">The phone number is registered under the provided ID</param>
/// <param name="Detail">Response message from Daraja, or an error description when Success is false</param>
public record DarajaValidationResult(bool Success, bool Matches, string Detail);
