using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Subscriptions.Controllers;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using BTCPayServer.Views.UIStoreMembership;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;


[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoSecurityTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoSecurityTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    // Creates a real offering + a trial-eligible plan via the actual Subscriptions
    // Greenfield API (client.CreateOffering / client.CreateOfferingPlan - the same
    // calls SubscriptionTests.CanUseSubscriptionAPI uses), then points Tando's own
    // settings at them via TandoSubscriptionService, resolved through
    // PayTester.GetService<T> rather than an in-process controller call. No browser,
    // no cheat mode, no manual step - runs cleanly on a fresh DB every session.
    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin)
    {
        var client = await admin.CreateClient();

        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel
        {
            AppName = "Tando Test Offering"
        });

        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new()
        {
            Name = "Trial Plan",
            Price = 0m,
            TrialDays = 7
        });

        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id
        });
    }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    // Mints a real API key by calling UIManageController's actual POST AddApiKey
    // action in-process, then reading it back via the equally real GET APIKeys
    // action rather than scraping the TempData status message (that message's
    // storage key/shape isn't shown anywhere in the controller source, so reading
    // it would just be a different guess). A fresh test account has exactly one
    // key at the point we read it back, so .Single() is safe here.
    // storeId == null mints an all-stores (admin) key; a non-null storeId mints
    // one scoped to only that store.
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
            PermissionValues = new List<UIManageController.AddApiKeyViewModel.PermissionValueItem> { permissionValue }
        });
        Assert.IsType<RedirectToActionResult>(createResult);

        var listResult = await manageController.APIKeys();
        var view = Assert.IsType<ViewResult>(listResult);
        var vm = Assert.IsType<UIManageController.ApiKeysViewModel>(view.Model);
        return vm.ApiKeyDatas.Single(k => k.Label == label).Id;
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

    private async Task<string> SignupAndGetStoreId(IAPIRequestContext api)
    {
        var response = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        var body = await response.TextAsync();
        Assert.True(response.Ok, $"Signup failed ({response.Status}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("storeId").GetString();
    }

    // Establishes the baseline: a configured subscription lets signup succeed,
    // creates a store, and a product added there shows up when listed back.
    [Fact]
    public async Task TandoSignup_ThenAddProduct_RoundTrips()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);

        var create = await api.PostAsync($"/plugins/api/tando/stores/{storeId}/products",
            new APIRequestContextOptions { DataObject = new { name = "Chapati", price = 20 } });
        Assert.Equal(200, create.Status);
        var productId = (await ParseJson(create)).GetProperty("id").GetString();

        var list = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/products");
        var products = await ParseJson(list);
        Assert.Contains(products.EnumerateArray(), p => p.GetProperty("id").GetString() == productId);

        await api.DisposeAsync();
    }

    // Cross-tenant boundary check, same class of bug rockstardev's VendorPaySecurityTests
    // targets for VendorPay: a caller reaching a store it shouldn't. An admin-wide key
    // can't expose this (admins bypass per-store checks), so this specifically uses a
    // key scoped to only store A to confirm it's rejected on store B.
    [Fact]
    public async Task ScopedApiKey_CannotAccessAnotherStoresProducts()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var adminApi = await ApiContextFor(adminKey);

        var storeAId = await SignupAndGetStoreId(adminApi);
        var storeBId = await SignupAndGetStoreId(adminApi);

        var scopedKey = await MintApiKey(admin, storeAId);
        var scopedApi = await ApiContextFor(scopedKey);

        var ownStore = await scopedApi.GetAsync($"/plugins/api/tando/stores/{storeAId}/products");
        Assert.Equal(200, ownStore.Status);

        var otherStore = await scopedApi.GetAsync($"/plugins/api/tando/stores/{storeBId}/products");
        Assert.True(otherStore.Status is 401 or 403,
            $"Expected 401/403 accessing another store's products with a key scoped to a " +
            $"different store, got {otherStore.Status}.");

        await adminApi.DisposeAsync();
        await scopedApi.DisposeAsync();
    }
}
