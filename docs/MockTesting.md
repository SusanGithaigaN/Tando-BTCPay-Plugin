# Development signup verification without Daraja

This provider simulates phone/ID verification only. BTCPay still authenticates the
request and performs its real subscription/store work. Successful fixtures can
create records in your local database.

Merge this property into the existing `Tando` object in
`btcpayserver/BTCPayServer/appsettings.dev.json`, preserving `DEBUG_PLUGINS`:

```json
"Tando": {
  "PhoneVerification": { "Mode": "Mock" }
}
```

Build from the repository root and restart BTCPay from its project directory:

```bash
dotnet build Plugins/BTCPayServer.Plugins.Tando
cd btcpayserver/BTCPayServer
dotnet run --launch-profile Bitcoin
```

In Postman, set the base URL to `http://localhost:14142`. Create a BTCPay API
key with unscoped Modify store settings permission and send it as
`Authorization: token YOUR_API_KEY`.

Check:

```http
GET /plugins/api/tando/verification/status
```

It should return `{"mode":"Mock","mock":true}`. Use the same phone
`0701234567` and ID type `01` for both signup requests:

```json
{
  "phoneNumber": "0701234567",
  "idNumber": "mock-verified",
  "idType": "01"
}
```

Change only `idNumber` to run negative cases:

| ID number | Result |
|---|---|
| `mock-verified` | Synthetic match; continues to subscription flow |
| `mock-mismatch` | 400 `phone_id_mismatch` |
| `mock-unavailable` | 503 `phone_validation_unavailable` |
| `mock-unconfigured` | 503 `kyc_not_configured` |

Test both `POST /plugins/api/tando/signup` and
`POST /plugins/api/tando/signup/subscribe`. The negative cases stop before
subscription or store creation. For a successful store, configure a local active
subscription offering and designated plan with a positive trial period in Tando
settings. A 503 `subscription_not_configured` means verification passed but
subscription setup is incomplete.

Unknown IDs and all unlisted phone/ID combinations mismatch. The successful ID and
phone are synthetic test fixtures. The mock provider is rejected outside the
Development environment, and production never falls back to it.

