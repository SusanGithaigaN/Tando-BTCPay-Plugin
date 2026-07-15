# Local development setup

How to run BTCPay Server locally with the Tando plugin loaded, on regtest, with a working Lightning setup for test payments.

> **Verified on Linux** (Ubuntu 22.04.5 LTS, .NET SDK 10.0.201). macOS should work with the same commands but is untested. On Windows, use WSL2. The docker test relies on bash scripts. Updates verifying other platforms are welcome.

## Prerequisites

The BTCPay Server source is vendored in this repository under `btcpayserver/`, there are no submodules to initialize.

All paths below are relative to the repository root unless a `cd` says otherwise.

## 1. Start the regtest dependencies

BTCPay's test harness provides a docker compose stack with everything the `Bitcoin` launch profile expects: PostgreSQL (port 39372), NBXplorer (port 32838), a regtest bitcoind, and Lightning nodes (c-lightning and LND) for both a "merchant" and a "customer".

```bash
cd btcpayserver/BTCPayServer.Tests
docker compose up -d dev
cd ../..   # return to the repository root for the next steps
```

**Linux gotcha:** the helper scripts in `BTCPayServer.Tests/` may not be executable after checkout. If you get `Permission denied` running any `docker-*.sh` script, fix it once with:

```bash
chmod +x btcpayserver/BTCPayServer.Tests/*.sh
```

## 2. Build the plugin

From the repository root:

```bash
dotnet build Plugins/BTCPayServer.Plugins.Tando
```

This produces `Plugins/BTCPayServer.Plugins.Tando/bin/Debug/net10.0/BTCPayServer.Plugins.Tando.dll`, which is what BTCPay will load in the next step.

## 3. Point BTCPay at the plugin (`appsettings.dev.json`)

BTCPay Server loads development plugins from the paths listed in the `DEBUG_PLUGINS` key of `btcpayserver/BTCPayServer/appsettings.dev.json`. Generate that file either way:

**Option A — run ConfigBuilder** (targets net8.0, so roll it forward to your installed SDK):

```bash
dotnet build ConfigBuilder
cd ConfigBuilder/bin/Debug/net8.0
dotnet --roll-forward LatestMajor ConfigBuilder.dll
cd -
```

ConfigBuilder resolves the plugin's absolute path and writes `btcpayserver/BTCPayServer/appsettings.dev.json`. Note it must be run from its output directory as shown — it locates the `Plugins/` folder and the output file with relative paths.

**Option B — write the file yourself** (from the repository root; `$PWD` expands to your absolute repo path, which is exactly what ConfigBuilder produces):

```bash
cat > btcpayserver/BTCPayServer/appsettings.dev.json <<EOF
{"DEBUG_PLUGINS":"$PWD/Plugins/BTCPayServer.Plugins.Tando/bin/Debug/net10.0/BTCPayServer.Plugins.Tando.dll;"}
EOF
```

Either way, verify the file contains the absolute path to the plugin DLL you built in step 2.

## 4. Run BTCPay Server with the plugin loaded

```bash
cd btcpayserver/BTCPayServer
dotnet run --launch-profile Bitcoin
```

**This must be run from `btcpayserver/BTCPayServer`** (or pass `--project btcpayserver/BTCPayServer`).

`dotnet run` from anywhere else fails with "Couldn't find a project to run."

The server comes up at <http://localhost:14142>. On your first visit, register an account, the `Bitcoin` profile allows admin registration, so the first registered user becomes the server admin.

### Verify the plugin loaded

- The **Tando** item appears in the sidebar navigation once you have a store selected.
- The plugin is listed under **Server Settings → Plugins**.

## 5. Configure Safaricom Daraja credentials (Mobile Number Validation)

The Tando signup endpoint can verify a merchant's phone number against Safaricom's KYC database using the [Mobile Number Validation API](https://developer.safaricom.co.ke/apis/MobileNumberValidation). The API checks whether the phone number is registered under a given ID number (National ID, Military ID, or Passport) and returns true/false. Note this is a commercial API: sandbox is free, but production requires onboarding via <apisupport@safaricom.co.ke>, a signed commercial agreement, and is billed per call (KES 4.50/call at the lowest volume tier, ex-VAT).

### Get API keys

1. Create an account on the [Safaricom Developer Portal](https://developer.safaricom.co.ke) (Daraja).
2. Go to **Dashboard → My Apps → Create App** (a sandbox app).
3. Open the app and copy its **Consumer Key** and **Consumer Secret**.
4. Note a short code to use. In sandbox, use the test short code from the API's simulator page; in production this is your live pay bill / till short code.

### Configure the plugin

1. Log into BTCPay as a server admin.
2. Open <http://localhost:14142/plugins/tando/daraja/settings>.
3. Enter the Consumer Key, Consumer Secret, and short code; leave **Use sandbox environment** checked for development. Save.

Credentials are stored server-wide in BTCPay's settings table. When they are not configured, signup falls back to format-only (regex) validation of the phone number.

### How signup changes when configured

`POST /plugins/api/tando/signup` then requires the merchant's ID details alongside the phone number:

```json
{
  "phoneNumber": "0712345678",
  "idType": "01",
  "idNumber": "12345678"
}
```

`idType` is `01` National ID (the default if omitted), `02` Military ID, or `05` Passport. Responses:

- Number and ID match → store is created, response contains `"phoneNumberVerified": true`.
- They don't match → `400` with `"error": "phone_validation_failed"`.
- Daraja unreachable / misconfigured / unsubscribed → `503` with `"error": "phone_validation_unavailable"` (signup is refused rather than skipping verification).

Test data for the sandbox is listed on the API's simulator section in the developer portal (log in to see it).
