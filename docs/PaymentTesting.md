# Test the shared M-Pesa workflow

This is a KES ledger, independent of Bitcoin invoices. BTCPay supplies authentication,
store ownership, and PostgreSQL persistence. The provider supplies collection and
disbursement observations. A customer collection is not merchant settlement.

## Enable locally

Merge `"Payments": { "Mode": "Mock" }` into the existing `Tando` object in
`btcpayserver/BTCPayServer/appsettings.dev.json`:

```json
"Tando": {
  "PhoneVerification": { "Mode": "Mock" },
  "Payments": { "Mode": "Mock" }
}
```

Preserve `DEBUG_PLUGINS`. Build the plugin and restart using the Bitcoin launch
profile as described in [Setup](Setup.md). Payment mock mode is refused outside
Development. Missing mode selects Splice; unknown mode values are rejected.

Import [the payment collection](postman/Tando-payments.postman_collection.json).
Set its `apiKey` and `storeId` to a local BTCPay store you control. It inherits
the same Greenfield token authentication as the signup collection. The payment
routes require Modify store settings permission for that store.

## Run the collection in order

1. Read provider status: `provider: "Mock"`, `ready: true`.
2. Save a synthetic merchant destination using the settings request.
3. Initiate an order. HTTP 200 is an accepted workflow record, **not proof of payment**.
   `collection` becomes `Pending`; `disbursement` remains `Unknown`.
4. Repeat initiation with the same order, phone, amount and destination. The same
   reference is returned, with no second provider payment. Reusing the order with
   a different amount or destination returns 409.
5. Deliver collection success plus disbursement pending via the authenticated
   mock callback route. Inspect the two states separately.
6. Deliver disbursement success. Repeat it: a duplicate is harmless. A conflicting
   terminal result returns 409; a stale pending result cannot undo success.
7. Initiate the lost-acknowledgement fixture. Its order starts with
   `mock-lost-ack-`; the provider accepts it, while the merchant ledger remains
   `Unknown`. Reconcile to discover `Pending` without creating another payment.
8. Simulate success with `deliver: false`. Only simulated PSP state is updated.
   Reconcile to recover the missing callback into the merchant ledger.
9. The collection also tests permanent outage (`mock-outage-` order prefix),
   failed collection and failed disbursement. These never report success.

Use a new `orderId` for a new experiment; completed/failed orders are not reset by
retrying. To test persistence, restart BTCPay and GET an existing order, or reconcile
the lost callback after restart. Both the mock PSP state and the merchant ledger
are persisted, with separate keys.

## Endpoints

All paths are under `/plugins/api/tando/stores/{storeId}/mpesa`:

| Method | Path | Purpose |
|---|---|---|
| GET | `/status` | Active provider and readiness |
| POST | `/pay` | Persist intent and initiate; order ID is the idempotency key |
| GET | `/payments/{orderId}` | Read current ledger state |
| POST | `/payments/{orderId}/reconcile` | Query PSP; retry only after authoritative absence |
| POST | `/payments/{orderId}/mock-callback` | Authenticated Development-only simulation |

Mock callbacks include the exact amount and destination; mismatches return 409.
An unknown order cannot be created by a callback. Payout state can only advance
after confirmed collection. Reconciliation is explicitly invoked via the endpoint;
there is no background polling job yet.

## Persistence and real provider boundary

The ledger uses plugin-owned JSON rows in BTCPay's PostgreSQL Settings table.
It deliberately bypasses the settings cache. Transaction-scoped advisory locks
serialize mutations across processes. Intent commits before calling the provider,
so a crash leaves an uncertain record to reconcile. No network call holds a DB lock.
Production adapters must guarantee stable-reference idempotency and distinguish
authoritative absence from service failure; failed/uncertain responses never trigger
an unconditional resend.

Splice remains the reference PSP, but real payment initiation now returns
503 `mpesa_provider_not_ready` until approved callback authentication, lookup,
and idempotency contracts are implemented. Existing Splice callback/identity
routes return 503 instead of falsely acknowledging unprocessed events. There is
no invented signature scheme or assertion that any supplied phone is known.

The workflow never marks a BTC invoice paid and never creates Lightning payouts.
`orderId` is a merchant order reference, not an instruction to settle a Bitcoin
invoice. The KES payment is read from this ledger.

## Tests

```bash
dotnet test tests/Tando.Tests/Tando.Tests.csproj -p:StaticWebAssetsEnabled=false
```

For the database test, point `TANDO_TEST_POSTGRES` at an **initialized local
BTCPay regtest database** (the stack in Setup.md uses port 39372). The test inserts
and deletes only a uniquely named test row, checks concurrent creation, reload,
and rollback. Without that variable the PostgreSQL test is explicitly skipped.
