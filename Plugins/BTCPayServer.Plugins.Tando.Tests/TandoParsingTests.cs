using BTCPayServer.Plugins.Tando.Helper;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.Services.Lightning;
using BTCPayServer.Plugins.Tando.ViewModels;

namespace BTCPayServer.Plugins.Tando.Tests;

[Trait("FastTest", "FastTest")]
public class TandoParsingTests
{
    [Theory]
    [InlineData("0712345678", "254712345678")]
    [InlineData("0112345678", "254112345678")]
    [InlineData("+254712345678", "254712345678")]
    [InlineData("+254112345678", "254112345678")]
    [InlineData("254712345678", "254712345678")]
    [InlineData("254112345678", "254112345678")]
    [InlineData("  0712345678  ", "254712345678")]
    [InlineData("0712 345 678", "254712345678")]
    [InlineData("+254 712 345 678", "254712345678")]
    [InlineData("0712-345-678", "254712345678")]
    public void Normalize_AcceptsKenyanFormats(string input, string expected)
        => Assert.Equal(expected, KenyanPhoneNumber.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0812345678")]      // 08 is not a Kenyan mobile prefix
    [InlineData("071234567")]       // too short
    [InlineData("07123456789")]     // too long
    [InlineData("+255712345678")]   // Tanzania
    [InlineData("2540712345678")]   // country code plus the trunk 0
    [InlineData("07123x5678")]
    [InlineData("not-a-phone")]
    public void Normalize_RejectsInvalidInput(string? input)
        => Assert.Null(KenyanPhoneNumber.Normalize(input));

    // ValidateMpesaSettings never touches the store, so no repository is needed.
    private static readonly TandoMerchantSettingsService Settings = new(null!);

    private static TandoMpesaValidationError? Validate(TandoMpesaDestinationType type, string? destination, string? accountNumber = null)
        => Settings.ValidateMpesaSettings(new TandoMpesaSettingsRequest
        {
            DestinationType = type,
            Destination = destination,
            AccountNumber = accountNumber
        });

    [Theory]
    [InlineData("0712345678")]
    [InlineData("+254712345678")]
    [InlineData("254112345678")]
    public void Mpesa_MobileNumber_Valid(string destination)
        => Assert.Null(Validate(TandoMpesaDestinationType.MobileNumber, destination));

    [Fact]
    public void Mpesa_MobileNumber_Invalid()
        => Assert.Equal("Destination", Validate(TandoMpesaDestinationType.MobileNumber, "0812345678")?.Field);

    [Theory]
    [InlineData(TandoMpesaDestinationType.MobileNumber)]
    [InlineData(TandoMpesaDestinationType.TillNumber)]
    [InlineData(TandoMpesaDestinationType.PayBill)]
    public void Mpesa_MissingDestination_IsRejected(TandoMpesaDestinationType type)
    {
        Assert.Equal("Destination", Validate(type, null)?.Field);
        Assert.Equal("Destination", Validate(type, "  ")?.Field);
    }

    [Theory]
    [InlineData(TandoMpesaDestinationType.TillNumber, "12345")]       // 5 digits, the minimum
    [InlineData(TandoMpesaDestinationType.TillNumber, "1234567890")]  // 10 digits, the maximum
    [InlineData(TandoMpesaDestinationType.PayBill, "4141031")]
    public void Mpesa_TillAndPaybill_Valid(TandoMpesaDestinationType type, string destination)
        => Assert.Null(Validate(type, destination));

    [Theory]
    [InlineData(TandoMpesaDestinationType.TillNumber, "1234")]         // too short
    [InlineData(TandoMpesaDestinationType.TillNumber, "12345678901")]  // too long
    [InlineData(TandoMpesaDestinationType.TillNumber, "12a45")]
    [InlineData(TandoMpesaDestinationType.PayBill, "1234")]
    [InlineData(TandoMpesaDestinationType.PayBill, "41-41031")]
    public void Mpesa_TillAndPaybill_Invalid(TandoMpesaDestinationType type, string destination)
        => Assert.Equal("Destination", Validate(type, destination)?.Field);

    [Fact]
    public void Mpesa_PaybillAccountNumber_LengthRule()
    {
        Assert.Null(Validate(TandoMpesaDestinationType.PayBill, "4141031", new string('A', 20)));
        Assert.Equal("AccountNumber", Validate(TandoMpesaDestinationType.PayBill, "4141031")?.Field);
        Assert.Equal("AccountNumber", Validate(TandoMpesaDestinationType.PayBill, "4141031", new string('A', 21))?.Field);
    }

    [Fact]
    public void TandoHmac_MatchesSpec()
    {
        var secret = TandoPaymentsClient.DecodeSecret("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8");
        Assert.Equal(32, secret.Length);
        var body = System.Text.Encoding.UTF8.GetBytes("{\"account\":\"5123456\",\"transactionType\":\"BuyGoods\"}");
        var header = TandoPaymentsClient.BuildAuthorizationHeader("tando-pos-test", secret, "post",
            "https://api.tando.to/v2/merchant", body, 1760000000, "0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("Tando-HMAC keyId=tando-pos-test, ts=1760000000, nonce=0f8fad5b-d9cb-469f-a165-70867728950e, sig=x84Q9xhWghQwLQCrbd+jFyQqcI3U/1NLv/miRHTcG+w=", header);
    }

    [Theory]
    [InlineData(TandoMpesaDestinationType.MobileNumber, "SendMoney")]
    [InlineData(TandoMpesaDestinationType.TillNumber, "BuyGoods")]
    [InlineData(TandoMpesaDestinationType.PayBill, "PayBill")]
    public void TandoTransactionType_Mapping(TandoMpesaDestinationType type, string expected)
        => Assert.Equal(expected, TandoPaymentsClient.ToTransactionType(type));

    [Fact]
    public void Mpesa_TillAccountNumber_IsNotLengthChecked()
        => Assert.Null(Validate(TandoMpesaDestinationType.TillNumber, "123456", new string('A', 30)));

    private static readonly string Pubkey = new('a', 64);
    private static readonly string Secret = new('b', 64);

    [Fact]
    public void Nwc_ValidUri_Parses()
    {
        var uri = $"nostr+walletconnect://{Pubkey}?relay=wss%3A%2F%2Frelay.example.com&relay=wss%3A%2F%2Frelay2.example.com&secret={Secret}";
        Assert.True(NwcConnectionString.TryParse(uri, out var result, out var error));
        Assert.Null(error);
        Assert.Equal(Pubkey, result!.WalletPubkeyHex);
        Assert.Equal(Secret, result.SecretHex);
        Assert.Equal(new[] { "wss://relay.example.com", "wss://relay2.example.com" }, result.RelayUrls);
    }

    [Theory]
    [InlineData("https://{0}?relay=wss%3A%2F%2Fr.example&secret={1}", "invalid_nwc_uri_scheme")]
    [InlineData("nostr+walletconnect://abc123?relay=wss%3A%2F%2Fr.example&secret={1}", "invalid_nwc_wallet_pubkey")]
    [InlineData("nostr+walletconnect://{0}?secret={1}", "nwc_uri_missing_relay")]
    [InlineData("nostr+walletconnect://{0}?relay=wss%3A%2F%2Fr.example", "nwc_uri_missing_or_invalid_secret")]
    [InlineData("nostr+walletconnect://{0}?relay=wss%3A%2F%2Fr.example&secret=abc123", "nwc_uri_missing_or_invalid_secret")]
    public void Nwc_InvalidUri_IsRejected(string template, string expectedError)
    {
        var uri = string.Format(template, Pubkey, Secret);
        Assert.False(NwcConnectionString.TryParse(uri, out var result, out var error));
        Assert.Null(result);
        Assert.Equal(expectedError, error);
    }
}