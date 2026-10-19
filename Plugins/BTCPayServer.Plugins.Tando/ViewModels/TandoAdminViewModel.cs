using System;
using System.Collections.Generic;
using System.Text;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoStoreRow
{
    public string StoreId { get; set; } = "";
    public string? Phone { get; set; }
    public string? OwnerEmail { get; set; }
    public int Employees { get; set; }
    public string? SplitPercent { get; set; }
    public string? DestinationType { get; set; }
    public string? Destination { get; set; }
    public string? Phase { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset? NextPaymentDue { get; set; }
}

public class TandoStoresViewModel
{
    public List<TandoStoreRow> Stores { get; set; } = new();
    public string? Search { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public bool HasMore { get; set; }
}

public class TandoPayoutRow
{
    public string InvoiceId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string? StoreName { get; set; }
    public string? RecordedAt { get; set; }
    public string? AmountKes { get; set; }
    public string? DestinationType { get; set; }
    public string? Destination { get; set; }
    public string? LightningStatus { get; set; }
    public string? MpesaStatus { get; set; }
    public string? Receipt { get; set; }
    public string? Error { get; set; }
    public string State { get; set; } = "";
}

public class TandoPayoutsViewModel
{
    public List<TandoPayoutRow> Payouts { get; set; } = new();
    public string? State { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
    public int Skip { get; set; }
    public int Take { get; set; }
    public bool HasMore { get; set; }
}

