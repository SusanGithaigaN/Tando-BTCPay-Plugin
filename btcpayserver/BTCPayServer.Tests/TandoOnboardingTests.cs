using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.MassStoreGenerator;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Tests;

public class TandoOnboardingTests : UnitTestBase
{
    public TandoOnboardingTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    public async Task Signup_WithMissingPhoneNumber_ReturnsBadRequest()
    {
        var controller = new TandoOnboardingController(null!, null!, null!);

        var result = await controller.Signup(new TandoSignupRequest { PhoneNumber = "" }, default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    [Theory]
    [InlineData("0712345678", "254712345678")]
    [InlineData("+254712345678", "254712345678")]
    [InlineData(" 0712345678 ", "254712345678")]
    public void NormalizePhone_FormatsValidKenyanMsisdn(string input, string expected)
    {
        var controller = new TandoOnboardingController(null!, null!, null!);
        var method = typeof(TandoOnboardingController).GetMethod("NormalizePhone", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var parameters = new object[] { input, null };
        var result = method.Invoke(controller, parameters);

        Assert.Null(parameters[1]);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("123")]
    [InlineData("+255712345678")]
    public void NormalizePhone_RejectsInvalidPhoneNumber(string input)
    {
        var controller = new TandoOnboardingController(null!, null!, null!);
        var method = typeof(TandoOnboardingController).GetMethod("NormalizePhone", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var parameters = new object[] { input, null };
        var result = method.Invoke(controller, parameters);

        Assert.Null(result);
        Assert.NotNull(parameters[1]);
        var errorResult = Assert.IsType<BadRequestObjectResult>(parameters[1]);
        Assert.Equal(400, errorResult.StatusCode ?? 400);
    }

    [Fact]
    public async Task Signup_WhenSubscriptionIsNotConfigured_ReturnsServiceUnavailable()
    {
        var settingsRepository = new StubSettingsRepository(new TandoSettings
        {
            SubscriptionOfferingId = null,
            SubscriptionPlanId = null
        });
        var subscriptionService = new TandoSubscriptionService(null!, settingsRepository, null!);
        var controller = new TandoOnboardingController(null!, subscriptionService, null!);

        var result = await controller.Signup(new TandoSignupRequest { PhoneNumber = "0712345678" }, default);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        var errorValue = objectResult.Value?.GetType().GetProperty("error")?.GetValue(objectResult.Value);
        Assert.Equal("subscription_not_configured", errorValue);
    }

    [Fact]
    public async Task GetProvisioningStatus_WhenStoreHasKeypadAndCartApps_ReturnsTrueForBoth()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"tando-onboarding-{Guid.NewGuid():N}")
            .Options;
        var factory = new InMemoryApplicationDbContextFactory(options);

        await using (var ctx = factory.CreateContext())
        {
            var store = new StoreData { Id = "store-tando-1", StoreName = "Tando Store", StoreBlob = "{}" };
            ctx.Stores.Add(store);
            ctx.Apps.AddRange(
                new AppData { Id = "keypad-app", StoreDataId = store.Id, AppType = PointOfSaleAppType.AppType, Name = TandoProductProvisioningService.KeypadAppName, Created = DateTimeOffset.UtcNow },
                new AppData { Id = "cart-app", StoreDataId = store.Id, AppType = PointOfSaleAppType.AppType, Name = TandoProductProvisioningService.CartAppName, Created = DateTimeOffset.UtcNow }
            );
            await ctx.SaveChangesAsync();
        }

        var service = new TandoProductProvisioningService(factory, null!);

        var result = await service.GetProvisioningStatus("store-tando-1");

        Assert.True(result.HasPos);
        Assert.True(result.HasCart);
    }

    private sealed class InMemoryApplicationDbContextFactory(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = "Host=localhost;Database=tando-tests" }), NullLoggerFactory.Instance)
    {
        private readonly DbContextOptions<ApplicationDbContext> _options = options;

        public override ApplicationDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
        {
            return new ApplicationDbContext(_options);
        }
    }

    private sealed class StubSettingsRepository(TandoSettings? settings) : ISettingsRepository
    {
        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        {
            if (settings is T typedSettings)
                return Task.FromResult<T?>(typedSettings);
            return Task.FromResult<T?>(null);
        }

        public Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            return Task.CompletedTask;
        }

        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        {
            throw new NotSupportedException();
        }
    }
}
