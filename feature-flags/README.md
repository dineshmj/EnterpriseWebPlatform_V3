# Feature flags

Switches that change what the platform does **without a redeploy**: a kill switch that pauses a product, a release toggle that hides a new API surface. The services ask for a flag through [OpenFeature](https://openfeature.dev) (vendor-neutral), so where the flags live is a setting, not code:

| Where the flags come from | When | How fast a change arrives |
|---|---|---|
| **LaunchDarkly** | `EWP_LAUNCHDARKLY_SDK_KEY` is set (environment variable; in the cloud demo the GitHub secret `LAUNCHDARKLY_SDK_KEY`) | Seconds (a stream) |
| **This folder's flag file** `ewp-flags.json` (flagd's format, read in-process - no flagd server) | No SDK key; `FeatureFlags:File` names the file (each API's `appsettings.Development.json`; in the cloud demo the ConfigMap `ewp-feature-flags`) | About 5 s locally; about a minute in the cluster (the kubelet updates the file) |
| **Nothing** | Neither | Every flag keeps its default |

Code: [Common/WebUtilities/FeatureFlags](../src/Common/WebUtilities/FeatureFlags/EwpFeatureFlags.cs).

## The flags

| Flag | Kind | Default (= today's behaviour) | On | Off | Read by |
|---|---|---|---|---|---|
| `bian-v2-endpoints` | Release toggle | **on** | The BIAN `/v2` endpoints answer | Every `/v2` request answers **404** with no body, before authentication - as if the route did not exist; `/v1` is never touched | Payments, Accounts, Customer Onboarding and Documents Management APIs |
| `digital-gold-sales-paused` | Kill switch | **off** | Sophie cannot record a new transfer slip, and "Send & pay" of a slip not yet sent is refused: *"Digital Gold sales are paused by the bank for now: nothing was sent to the Partner Bank and nothing was charged."* A slip already SENT is still sent again, and ORDERED / PAID purchases carry on - nothing is stranded | Sales as usual | Partner Products API |

## Safety rules

- **The default is today's behaviour.** A missing or broken file, or LaunchDarkly being unreachable, never switches anything by itself: the flag keeps its default and the service logs a warning (`Feature flag ... could not be read`).
- **A flag only switches something OFF, for everyone.** Who may do what stays with the authorization policies ([Authorization-Model](../doc/Authorization-Model.md)); no flag grants access.
- **The SDK key is a secret.** It reads every flag of its LaunchDarkly environment: environment variable or secret store only, never a settings file or a commit.
- **Start-up never waits** for the flag source: until it is ready, every flag has its default.

## Switching a flag

**On this PC (the file):** edit `ewp-flags.json` - set the flag's `"defaultVariant"` to `"on"` or `"off"` - and save. The APIs pick it up within about 5 seconds; no restart. Put it back before you commit (the committed file holds the defaults).

**In the cloud demo (the file):** `kubectl -n ewp edit configmap ewp-feature-flags`, same change; about a minute. The chart's values (`global.featureFlags.flags`) put it back at the next Demo up.

**LaunchDarkly:** toggle the flag in its environment; seconds.

## LaunchDarkly set-up (one time)

1. In your LaunchDarkly project, create the two flags **with exactly these keys**: `bian-v2-endpoints` and `digital-gold-sales-paused`, both **Boolean**. Serve `true` / `false` so that the result matches the table above (`bian-v2-endpoints` → `true`, `digital-gold-sales-paused` → `false`), and turn targeting **on** in each environment you use.
2. The services identify themselves as a context of kind `service`, key = the service name (`payments-api`, `partner-products-api`, ...): target a single service by that key if you want to.
3. **This PC:** copy the SDK key of the environment you use for development (Project settings → Environments → *Test* → SDK key; it starts with `sdk-`) and set it once for your user, then restart the APIs (and Visual Studio / the terminal you start them from):
   ```
   setx EWP_LAUNCHDARKLY_SDK_KEY "sdk-..."
   ```
   To go back to the file: `reg delete HKCU\Environment /v EWP_LAUNCHDARKLY_SDK_KEY /f`, then restart them.
4. **Cloud demo:** add the GitHub repository secret `LAUNCHDARKLY_SDK_KEY` (the SDK key of another environment, e.g. *Production*). Demo up then stores it in Key Vault, and the chart switches the five APIs to LaunchDarkly. Their NetworkPolicy then allows HTTPS (443) to public addresses - LaunchDarkly publishes no fixed addresses a NetworkPolicy could name ([doc/Kubernetes.md](../doc/Kubernetes.md), K13). Without the secret nothing changes.

## Tests

- Unit: `tst/dotnet/Payments.Api.Tests/FeatureFlags` - the gate on / off / file missing / file broken, and this folder's file as committed.
- Playwright (switch the file, then put it back byte for byte): `tests/20-functional/bian/bian-release-toggle.spec.ts`, `tests/35-resilience/partner-products/digital-gold-sales-paused.spec.ts`. Both are skipped while `EWP_LAUNCHDARKLY_SDK_KEY` is set (the APIs would not read the file).