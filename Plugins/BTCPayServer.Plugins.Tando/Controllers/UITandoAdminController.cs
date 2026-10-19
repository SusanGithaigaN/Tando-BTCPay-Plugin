using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Tando;

[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("server/tando")]
public class UITandoAdminController(ApplicationDbContextFactory dbContextFactory, TandoSubscriptionService subscriptionService) : Controller
{
    private const int PageSize = 50;
    private static readonly string[] States = ["pending", "paid", "failed"];

    [HttpGet("stores")]
    public async Task<IActionResult> Stores(string? search = null, int skip = 0)
    {
        ViewData["ActivePage"] = "Tando";
        skip = Math.Max(0, skip);
        var term = search?.Trim() ?? "";

        await using var ctx = dbContextFactory.CreateContext();
        // Tando stores are the ones signup tagged with the merchant's phone number.
        var rows = (await ctx.Database.GetDbConnection().QueryAsync<TandoStoreRow>(
            """
            SELECT s."Id" AS "StoreId",
                   s."StoreBlob"->>'tandoPhoneNumber' AS "Phone",
                   s."StoreBlob"->'tandoSplitConfig'->>'MpesaPercentage' AS "SplitPercent",
                   s."StoreBlob"->'tandoMpesaSettings'->>'DestinationType' AS "DestinationType",
                   s."StoreBlob"->'tandoMpesaSettings'->>'Destination' AS "Destination",
                   (SELECT u."Email" FROM "UserStore" us JOIN "AspNetUsers" u ON u."Id" = us."ApplicationUserId"
                    WHERE us."StoreDataId" = s."Id" AND us."Role" = 'Owner' LIMIT 1) AS "OwnerEmail",
                   (SELECT COUNT(*)::int FROM "UserStore" us
                    WHERE us."StoreDataId" = s."Id" AND us."Role" = 'Employee') AS "Employees"
            FROM "Stores" s
            WHERE s."StoreBlob"->>'tandoPhoneNumber' IS NOT NULL
              AND NOT s."Archived"
              AND (@term = '' OR s."StoreBlob"->>'tandoPhoneNumber' LIKE '%' || @term || '%')
            ORDER BY s."StoreBlob"->>'tandoPhoneNumber'
            OFFSET @skip LIMIT @take
            """,
            new { term, skip, take = PageSize + 1 })).ToList();

        var page = rows.Take(PageSize).ToList();
        foreach (var row in page.Where(r => r.Phone is not null))
        {
            var status = await subscriptionService.GetStatus(row.Phone!);
            row.Phase = status.Phase;
            row.Active = status.Active;
            row.NextPaymentDue = status.NextPaymentDue;
        }

        return View(new TandoStoresViewModel
        {
            Stores = page,
            Search = search,
            Skip = skip,
            Take = PageSize,
            HasMore = rows.Count > PageSize
        });
    }

    [HttpGet("payouts")]
    public async Task<IActionResult> Payouts(string? state = null, int skip = 0)
    {
        ViewData["ActivePage"] = "Tando";
        skip = Math.Max(0, skip);
        var filter = state is not null && States.Contains(state) ? state : "";

        await using var ctx = dbContextFactory.CreateContext();
        var connection = ctx.Database.GetDbConnection();
        // Only sales with an M-Pesa share; State folds the Lightning and M-Pesa legs into one word.
        const string payouts =
            """
            SELECT i."Id" AS "InvoiceId", i."StoreDataId" AS "StoreId", s."StoreName",
                   sp->>'RecordedAt' AS "RecordedAt",
                   sp->>'MpesaPortionAmount' AS "AmountKes",
                   sp->>'MpesaDestinationType' AS "DestinationType",
                   sp->>'MpesaDestination' AS "Destination",
                   sp->>'PullPaymentStatus' AS "LightningStatus",
                   sp->>'MpesaPayoutStatus' AS "MpesaStatus",
                   sp->>'MpesaPayoutReference' AS "Receipt",
                   COALESCE(sp->>'MpesaPayoutError', sp->>'PullPaymentError') AS "Error",
                   CASE
                       WHEN sp->>'MpesaPayoutStatus' = 'Confirmed' OR sp->>'MpesaSettled' = 'true' THEN 'paid'
                       WHEN sp->>'PullPaymentStatus' = 'Failed' OR sp->>'MpesaPayoutStatus' = 'Failed' THEN 'failed'
                       ELSE 'pending'
                   END AS "State"
            FROM "Invoices" i
            JOIN "Stores" s ON s."Id" = i."StoreDataId"
            CROSS JOIN LATERAL (SELECT i."Blob2"->'metadata'->'tandoSplit' AS sp) x
            WHERE sp IS NOT NULL
              AND NOT i."Archived"
              AND (sp->>'MpesaPortionAmount')::numeric > 0
            """;

        var rows = (await connection.QueryAsync<TandoPayoutRow>(
            $"""
            SELECT * FROM ({payouts}) p
            WHERE (@filter = '' OR p."State" = @filter)
            ORDER BY p."RecordedAt" DESC, p."InvoiceId"
            OFFSET @skip LIMIT @take
            """,
            new { filter, skip, take = PageSize + 1 })).ToList();

        var counts = (await connection.QueryAsync<(string State, int Count)>(
            $"""SELECT p."State", COUNT(*)::int FROM ({payouts}) p GROUP BY p."State" """))
            .ToDictionary(c => c.State, c => c.Count);

        return View(new TandoPayoutsViewModel
        {
            Payouts = rows.Take(PageSize).ToList(),
            State = filter == "" ? null : filter,
            Counts = counts,
            Skip = skip,
            Take = PageSize,
            HasMore = rows.Count > PageSize
        });
    }
}