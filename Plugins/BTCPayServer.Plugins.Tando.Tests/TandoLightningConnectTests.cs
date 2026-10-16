using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Tando.Tests;

[Trait("Category", "Integration")]
public class TandoLightningConnectTests : UnitTestBase
{
    public TandoLightningConnectTests(ITestOutputHelper helper) : base(helper)
    {
    }

    private static async Task<HttpResponseMessage> Connect(HttpClient http, string apiKey, string storeId, string connectionString)
    {
        var request = new HttpRequestMessage(HttpMethod.Put,
            new Uri(http.BaseAddress!, $"/plugins/api/tando/stores/{storeId}/lightning/connect"));
        request.Headers.Authorization = new AuthenticationHeaderValue("token", apiKey);
        request.Content = new StringContent(JsonConvert.SerializeObject(new { connectionString }), Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    private static async Task<string> CreateKey(BTCPayServerClient client, params Permission[] permissions)
        => (await client.CreateAPIKey(new CreateApiKeyRequest { Label = $"tando-ln-{Guid.NewGuid():N}", Permissions = permissions })).ApiKey;

    [Fact]
    public async Task Connect_NonOwner_IsRejectedAndStoreUntouched()
    {
        using var s = CreateServerTester(newDb: true);
        await s.StartAsync();
        var owner = s.NewAccount();
        await owner.GrantAccessAsync();
        var ownerClient = await owner.CreateClient();
        var otherStore = await ownerClient.CreateStore(new CreateStoreRequest { Name = "Other store" });
        var scopedKey = await CreateKey(ownerClient, Permission.Create(Policies.CanModifyStoreSettings, owner.StoreId));

        using (var empty = await Connect(s.PayTester.HttpClient, scopedKey, owner.StoreId, " "))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using (var crossStore = await Connect(s.PayTester.HttpClient, scopedKey, otherStore.Id, "type=clightning;server=tcp://127.0.0.1:9735"))
            Assert.Equal(HttpStatusCode.Forbidden, crossStore.StatusCode);

        var untouched = await s.PayTester.StoreRepository.FindStore(otherStore.Id);
        Assert.Null(untouched!.GetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC")));
    }

    [Fact]
    public async Task Connect_InvalidOrInternalString_IsRejectedForNonAdmin()
    {
        using var s = CreateServerTester(newDb: true);
        await s.StartAsync();
        var user = s.NewAccount();
        await user.GrantAccessAsync();
        var key = await CreateKey(await user.CreateClient(), Permission.Create(Policies.CanModifyStoreSettings));

        using (var garbage = await Connect(s.PayTester.HttpClient, key, user.StoreId, "not-a-connection-string"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
            Assert.Equal("invalid_connection_string", await ErrorCode(garbage));
        }

        // A local address is the SSRF case: a merchant key must not point the server at its own network.
        using (var local = await Connect(s.PayTester.HttpClient, key, user.StoreId, "type=clightning;server=tcp://127.0.0.1:9735"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, local.StatusCode);
            Assert.Equal("invalid_connection_string", await ErrorCode(local));
        }

        var store = await s.PayTester.StoreRepository.FindStore(user.StoreId);
        Assert.Null(store!.GetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC")));
    }

    [Fact]
    public async Task Connect_UnreachableNode_IsRejected_ReachableNodeIsSaved()
    {
        using var s = CreateServerTester(newDb: true);
        await s.StartAsync();
        var admin = s.NewAccount();
        await admin.GrantAccessAsync(true);
        // Server permission lets the key use local addresses, so this test reaches the node checks rather than the SSRF one.
        var key = await CreateKey(await admin.CreateClient(),
            Permission.Create(Policies.CanModifyStoreSettings), Permission.Create(Policies.CanModifyServerSettings));

        using (var unreachable = await Connect(s.PayTester.HttpClient, key, admin.StoreId, "type=clightning;server=tcp://127.0.0.1:1"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unreachable.StatusCode);
            Assert.Equal("lightning_node_unreachable", await ErrorCode(unreachable));
        }

        var connectionString = s.GetLightningConnectionString(LightningTestImplementation.CoreLightning, true);
        using (var valid = await Connect(s.PayTester.HttpClient, key, admin.StoreId, connectionString))
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var store = await s.PayTester.StoreRepository.FindStore(admin.StoreId);
        var config = store!.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, s.PayTester.GetService<PaymentMethodHandlerDictionary>());
        Assert.Equal(connectionString, config?.ConnectionString);
        Assert.False(store.GetStoreBlob().IsExcluded(paymentMethodId));
    }
}