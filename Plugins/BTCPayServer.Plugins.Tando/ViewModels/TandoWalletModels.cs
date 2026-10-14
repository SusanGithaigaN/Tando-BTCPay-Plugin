using System;
using System.ComponentModel.DataAnnotations;
using NBitcoin.JsonConverters;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoWalletBackup
{
    public string Ciphertext { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public string PrfSalt { get; set; } = "";
    [JsonConverter(typeof(DateTimeToUnixTimeConverter))]
    public DateTimeOffset CreatedAt { get; set; }
    [JsonConverter(typeof(DateTimeToUnixTimeConverter))]
    public DateTimeOffset? VerifiedAt { get; set; }
}

public class TandoNodeRegistration
{
    public string NodePubkey { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public long Epoch { get; set; }
    [JsonConverter(typeof(DateTimeToUnixTimeConverter))]
    public DateTimeOffset ClaimedAt { get; set; }
}

public class TandoLightningPaymentProof
{
    public string Bolt11 { get; set; } = "";
    public string PaymentHash { get; set; } = "";
    public string Preimage { get; set; } = "";
    public long AmountSats { get; set; }
    public string NodePubkey { get; set; } = "";
    [JsonConverter(typeof(DateTimeToUnixTimeConverter))]
    public DateTimeOffset RecordedAt { get; set; }
}

public class TandoSaveWalletBackupRequest
{
    [Required] public string Ciphertext { get; set; } = "";
    [Required] public string Nonce { get; set; } = "";
    [Required] public string CredentialId { get; set; } = "";
    [Required] public string PrfSalt { get; set; } = "";
}

public class TandoClaimNodeRequest
{
    [Required] public string NodePubkey { get; set; } = "";
    [Required] public string DeviceId { get; set; } = "";
}

public class TandoRecordLightningPaymentRequest
{
    [Required] public string Bolt11 { get; set; } = "";
    [Required] public string Preimage { get; set; } = "";
    public long Epoch { get; set; }
}