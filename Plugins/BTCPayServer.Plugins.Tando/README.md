# BTCPayServer.Plugins.Tando

Plugin project for the **Kenyan Merchant BTCPay PoS plugin** — mobile Point of Sale accepting both M-Pesa (via PSP) and Bitcoin (Lightning), two independent rails with no forced conversion.

**The spec lives in the repository root [README](../../README.md)** — design principles, two-rail settlement model, LSP/PSP architecture, and the open pre-implementation items. Read it before touching this project.

**Current status (2026-07-22):** early implementation. The onboarding/store-creation slice is merged (PR #1, `ft/onboarding`); the remaining phases and the spec's open design decisions (node stack, LSP protocol, recovery, refunds, pricing, …) are still ahead.

### Provider boundaries and testing

The root README remains the spec: BTC and KES are independent rails. BTCPay owns
stores, invoices, roles, persistence, and events; Splice is the reference M-Pesa
PSP/Daraja adapter. This plugin does not convert a BTC payment to M-Pesa.

Legacy split creation, split configuration writes, and manual split settlement
return HTTP 410 (`cross_rail_conversion_not_supported`). Historical split records
and configuration remain readable. The legacy treasury setting is retained in
storage but is no longer exposed or used to create Lightning payouts.

Signup depends on the abstract `PhoneVerificationProvider`. Daraja administration
is separate from signup. The real validation adapter remains registered, with
400 for an identity mismatch and 503 for missing configuration or unavailable
verification. These are hard blocks in both signup steps; mocks never replace
verification in production.

M-Pesa initiation and normalized callbacks now share a durable KES ledger with
store/order idempotency and explicit reconciliation. The Development-only payment
adapter persists simulated PSP outcomes separately, enabling lost-acknowledgement
and lost-callback testing. See [Payment testing](../../docs/PaymentTesting.md).
Real Splice processing remains blocked until its authenticated callback, lookup,
and idempotency contracts are approved; unconfigured webhooks return 503.

Use the .NET SDK and BTCPay's existing Docker regtest stack as documented in
[Setup](../../docs/Setup.md):

```bash
dotnet build Plugins/BTCPayServer.Plugins.Tando
dotnet test tests/Tando.Tests/Tando.Tests.csproj
```

The plugin tests use xUnit and reference the actual plugin/BTCPay projects.
Development payment simulation is explicitly selected and uses the shared ledger.
Tests exercise retries, idempotency, duplicate callbacks and reconciliation;
controller tests check signup blocks and rejection of legacy cross-rail writes.
These supplement the documented BTCPay regtest invoice/payment check; they do
not establish real PSP callback authentication or end-to-end B2C settlement.


### Local signup without credentials

An explicit Development-only phone verification provider supports Postman testing
of successful verification, mismatch, outage, and missing configuration.
See [Mock testing](../../docs/MockTesting.md) for setup, fixed synthetic fixtures,
and an importable Postman collection. Production continues to use Daraja and
never falls back to a mock. BTCPay authentication and persistence remain active.
