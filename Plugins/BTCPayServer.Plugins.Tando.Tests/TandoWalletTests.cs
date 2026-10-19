using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;
using NBitcoin;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoWalletTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoWalletTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin, bool tandoPayouts = true)
    {
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel { AppName = "Tando Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Trial Plan", Price = 0m, TrialDays = 7 });
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id,
            // Payouts on, but pointed at a port nothing listens on, so tests never reach Tando.
            TandoApiBaseUrl = tandoPayouts ? "http://127.0.0.1:1" : null,
            TandoApiKeyId = tandoPayouts ? "test-key" : null,
            TandoApiSecret = tandoPayouts ? "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8" : null
        });
    }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    private async Task<string> MintApiKey(TestAccount account, string storeId = null)
    {
        var manageController = ServerTester.PayTester.GetController<UIManageController>(
            account.UserId, account.StoreId, account.IsAdmin);

        var permissionValue = new UIManageController.AddApiKeyViewModel.PermissionValueItem
        {
            Permission = Policies.CanModifyStoreSettings,
            Value = true,
            StoreMode = storeId is null
                ? UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.AllStores
                : UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.Specific,
            SpecificStores = storeId is null ? new List<string>() : new List<string> { storeId }
        };

        var label = $"tando-test-{Guid.NewGuid():N}";
        var createResult = await manageController.AddApiKey(new UIManageController.AddApiKeyViewModel
        {
            Label = label,
            PermissionValues = storeId is null
                ? new List<UIManageController.AddApiKeyViewModel.PermissionValueItem>
                {
                    permissionValue,
                    new() { Permission = Policies.CanModifyServerSettings, Value = true }
                }
                : new List<UIManageController.AddApiKeyViewModel.PermissionValueItem> { permissionValue }
        });
        Assert.IsType<RedirectToActionResult>(createResult);

        var listResult = await manageController.APIKeys();
        var view = Assert.IsType<ViewResult>(listResult);
        var vm = Assert.IsType<UIManageController.ApiKeysViewModel>(view.Model);
        return vm.ApiKeyDatas.Single(k => k.Label == label).Key;
    }

    private async Task<IAPIRequestContext> ApiContextFor(string apiKey)
    {
        return await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = ServerUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"token {apiKey}",
                ["Content-Type"] = "application/json"
            },
            IgnoreHTTPSErrors = true
        });
    }

    private static string RandomKenyanPhone() => $"07{Random.Shared.Next(10000000, 99999999)}";

    private static async Task<JsonElement> ParseJson(IAPIResponse response)
        => JsonDocument.Parse(await response.TextAsync()).RootElement;

    private readonly Dictionary<string, string> _merchantKeys = new();

    private async Task<(string StoreId, IAPIRequestContext Api)> SignupMerchant(IAPIRequestContext backendApi, string phone = null)
    {
        var response = await backendApi.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone ?? RandomKenyanPhone() } });
        var body = await response.TextAsync();
        Assert.True(response.Ok, $"Signup failed ({response.Status}): {body}");
        var root = JsonDocument.Parse(body).RootElement;
        var storeId = root.GetProperty("storeId").GetString();
        var apiKey = root.GetProperty("apiKey").GetString();
        _merchantKeys[storeId] = apiKey;
        return (storeId, await ApiContextFor(apiKey));
    }

    private BTCPayServerClient MerchantClient(string storeId) => new(ServerUri, _merchantKeys[storeId]);

    private async Task<string> CreateInvoice(string storeId, decimal amount)
    {
        var client = MerchantClient(storeId);
        await EnsureStoreWallet(client, storeId);
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest { Amount = amount, Currency = "KES" });
        return invoice.Id;
    }

    private readonly HashSet<string> _storesWithWallet = new();
    private async Task EnsureStoreWallet(BTCPayServerClient client, string storeId)
    {
        if (_storesWithWallet.Add(storeId))
        {
            await client.GenerateOnChainWallet(storeId, "BTC-CHAIN", new GenerateOnChainWalletRequest());
            await client.UpdateStoreRateConfiguration(storeId, new StoreRateConfiguration
            {
                IsCustomScript = true,
                EffectiveScript = "BTC_KES = 10000000;"
            });
        }
    }

    private static object SampleBackup() => new
    {
        ciphertext = Convert.ToBase64String(RandomUtils.GetBytes(64)),
        nonce = Convert.ToBase64String(RandomUtils.GetBytes(12)),
        credentialId = "cred-" + Guid.NewGuid().ToString("N"),
        prfSalt = Convert.ToBase64String(RandomUtils.GetBytes(32))
    };

    private static string NewNodePubkey() => new Key().PubKey.ToHex();

    // Saves and verifies a backup, then registers a node. Returns the epoch.
    private static async Task<long> SetUpWallet(IAPIRequestContext merchantApi, string storeId, string nodePubkey, string deviceId)
    {
        Assert.Equal(200, (await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup",
            new APIRequestContextOptions { DataObject = SampleBackup() })).Status);
        Assert.Equal(200, (await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup/verify")).Status);
        var claim = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/node",
            new APIRequestContextOptions { DataObject = new { nodePubkey, deviceId } });
        Assert.Equal(200, claim.Status);
        return (await ParseJson(claim)).GetProperty("epoch").GetInt64();
    }

    private async Task<(IAPIRequestContext BackendApi, string StoreId, IAPIRequestContext MerchantApi)> NewMerchant()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var api = await ApiContextFor(await MintApiKey(admin));
        var (storeId, merchantApi) = await SignupMerchant(api);
        return (api, storeId, merchantApi);
    }

    [Fact]
    public async Task Backup_SaveThenVerify_RoundTrips()
    {
        var (_, storeId, merchantApi) = await NewMerchant();

        var missing = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup");
        Assert.Equal(404, missing.Status);

        var backup = SampleBackup();
        Assert.Equal(200, (await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup",
            new APIRequestContextOptions { DataObject = backup })).Status);

        var saved = await ParseJson(await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup"));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("verifiedAt").ValueKind);

        Assert.Equal(200, (await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup/verify")).Status);
        var verified = await ParseJson(await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup"));
        Assert.NotEqual(JsonValueKind.Null, verified.GetProperty("verifiedAt").ValueKind);
    }

    [Fact]
    public async Task Backup_Incomplete_ReturnsBadRequest()
    {
        var (_, storeId, merchantApi) = await NewMerchant();
        var save = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup",
            new APIRequestContextOptions { DataObject = new { ciphertext = "abc" } });
        Assert.Equal(400, save.Status);
        Assert.Equal("backup_incomplete", (await ParseJson(save)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClaimNode_BeforeBackupVerified_IsRefused()
    {
        var (_, storeId, merchantApi) = await NewMerchant();
        await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup",
            new APIRequestContextOptions { DataObject = SampleBackup() });

        // Saved but not verified: the node can't go live yet.
        var claim = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/node",
            new APIRequestContextOptions { DataObject = new { nodePubkey = NewNodePubkey(), deviceId = "phone-1" } });
        Assert.Equal(400, claim.Status);
        Assert.Equal("backup_not_verified", (await ParseJson(claim)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClaimNode_OnNewDevice_ShutsOutTheOldOne()
    {
        var (_, storeId, merchantApi) = await NewMerchant();
        var firstEpoch = await SetUpWallet(merchantApi, storeId, NewNodePubkey(), "phone-1");

        // Restored on a new phone: the claim bumps the epoch.
        var reclaim = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/node",
            new APIRequestContextOptions { DataObject = new { nodePubkey = NewNodePubkey(), deviceId = "phone-2" } });
        Assert.Equal(200, reclaim.Status);
        Assert.Equal(firstEpoch + 1, (await ParseJson(reclaim)).GetProperty("epoch").GetInt64());

        // The old phone reporting a payment with its epoch is refused before anything else is looked at.
        var invoiceId = await CreateInvoice(storeId, 100m);
        var report = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/lightning-payment",
            new APIRequestContextOptions { DataObject = new { bolt11 = "lnbcrt1", preimage = new string('0', 64), epoch = firstEpoch } });
        Assert.Equal(409, report.Status);
        Assert.Equal("stale_node", (await ParseJson(report)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClaimNode_InvalidPubkey_ReturnsBadRequest()
    {
        var (_, storeId, merchantApi) = await NewMerchant();
        var claim = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/node",
            new APIRequestContextOptions { DataObject = new { nodePubkey = "02deadbeef", deviceId = "phone-1" } });
        Assert.Equal(400, claim.Status);
        Assert.Equal("invalid_node_pubkey", (await ParseJson(claim)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RecordLightningPayment_WithoutWallet_IsRefused()
    {
        var (_, storeId, merchantApi) = await NewMerchant();
        var invoiceId = await CreateInvoice(storeId, 100m);
        var report = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/lightning-payment",
            new APIRequestContextOptions { DataObject = new { bolt11 = "lnbcrt1", preimage = new string('0', 64), epoch = 1 } });
        Assert.Equal(400, report.Status);
        Assert.Equal("backup_not_verified", (await ParseJson(report)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RecordLightningPayment_InvalidBolt11_ReturnsBadRequest()
    {
        var (_, storeId, merchantApi) = await NewMerchant();
        var epoch = await SetUpWallet(merchantApi, storeId, NewNodePubkey(), "phone-1");
        var invoiceId = await CreateInvoice(storeId, 100m);

        var report = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/lightning-payment",
            new APIRequestContextOptions { DataObject = new { bolt11 = "not-an-invoice", preimage = new string('0', 64), epoch } });
        Assert.Equal(400, report.Status);
        Assert.Equal("invalid_bolt11", (await ParseJson(report)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task WalletEndpoints_AreOwnerOnly()
    {
        var (backendApi, storeId, merchantApi) = await NewMerchant();
        var employeePhone = RandomKenyanPhone();
        Assert.Equal(200, (await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = employeePhone } })).Status);

        var issue = await backendApi.PostAsync("/plugins/api/tando/users/api-keys",
            new APIRequestContextOptions { DataObject = new { phoneNumber = employeePhone } });
        var employeeApi = await ApiContextFor((await ParseJson(issue)).GetProperty("apiKey").GetString());

        // The seed backup belongs to the owner; an employee must not be able to read or replace it.
        Assert.Equal(403, (await employeeApi.GetAsync($"/plugins/api/tando/stores/{storeId}/wallet/backup")).Status);
        Assert.Equal(403, (await employeeApi.PutAsync($"/plugins/api/tando/stores/{storeId}/wallet/node",
            new APIRequestContextOptions { DataObject = new { nodePubkey = NewNodePubkey(), deviceId = "employee-phone" } })).Status);
    }
}