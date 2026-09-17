using System.Threading.Tasks;
using BTCPayServer.Plugins.MassStoreGenerator;
using BTCPayServer.Plugins.Tando;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Tando.Tests;

public class IndependentRailsTests
{
    [Fact]
    public async Task LegacySplitWritesCannotAccessInvoicesOrCreatePayouts()
    {
        // A null repository fails if either mutation touches BTCPay persistence.
        var service = new TandoSplitService(null!);
        var controller = new TandoSplitController(service);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.Compute("store", "invoice")).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.Settle("store", "invoice")).StatusCode);
        Assert.Equal(TandoSplitService.DisabledError, (await service.ComputeAndRecordSplit("store", "invoice")).Error);
        Assert.False((await service.MarkMpesaSettled("store", "invoice")).Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void LegacySplitConfigurationCannotEnableConversion(int percentage)
    {
        var controller = new TandoMerchantSettingsController(null!);
        var result = controller.SaveSplitConfig("store", new TandoSplitConfigRequest { MpesaPercentage = percentage });
        Assert.Equal(410, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
