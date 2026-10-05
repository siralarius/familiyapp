# Family App

A local-first family organizer built with .NET MAUI + Blazor Hybrid.

## Product goals

Family App is intended to give a household one simple place for:

- weekly meal planning
- a shared family calendar
- shared shopping lists
- household inventory / "running low" items such as milk, eggs, pasta, bread and bananas
- offline-first use on each family member's phone
- privacy-first synchronization without making a cloud database the source of truth

Each phone owns a complete local copy of the household data. SQLite is the local source of truth. Synchronization exchanges changes between trusted family devices.

## Development approach

Development is iterative. The web preview exists for fast UI and business-logic testing; the native MAUI app remains the production target.

### Iteration 1 — Local-first family app + Wi-Fi sync

**Application foundation**
- .NET MAUI Blazor Hybrid native app
- shared Razor UI usable by MAUI and a browser preview host
- minimalistic responsive UI
- Home, Meals, Shopping, Calendar and More/Inventory areas
- clean separation of Core, Data, Sync, Shared UI, MAUI and Web projects

**Data and features**
- local SQLite persistence on each phone
- shopping list
- household inventory and low-stock thresholds
- optional "add to shopping list when low" behavior
- weekly meal planner
- family calendar
- domain/service abstractions so UI is independent from storage and transport

**Local synchronization**
- append/change log rather than copying SQLite database files
- stable entity/change/device IDs
- conflict resolution rules appropriate to each entity
- iOS peer discovery on the same local network using Apple Network framework / Bonjour
- trusted-device pairing, designed around QR-based onboarding
- authenticated/encrypted device-to-device transport
- automatic sync when trusted devices are reachable on the same Wi-Fi
- clear last-sync / sync-status UI
- app remains fully usable while peers are offline

**Developer experience**
- FamilyApp.Web browser preview
- browser-friendly persistence adapter for preview/testing
- unit tests for Core and Sync logic
- GitHub Actions build/test CI
- Netlify deploy previews for pull requests
- native-device testing reserved for iOS-specific functionality such as Bonjour, permissions, pairing and SQLite integration

**Iteration 1 exit criteria:** two paired iPhones can independently modify household data, discover each other when reachable on the same Wi-Fi, exchange missing changes, resolve supported conflicts and converge to the same state. Most non-native functionality can be reviewed through a Netlify PR preview.

### Iteration 2 — Encrypted remote transport

Add internet/mobile-data synchronization without replacing local SQLite as the source of truth.

- implement a remote `ISyncTransport`
- allow synchronization when family members are on different Wi-Fi networks or cellular data
- end-to-end encrypt change payloads using family/device keys
- use the remote service as a relay, not as the authoritative household database
- server should not need plaintext access to household data
- support temporary/acknowledged messages and cleanup after successful delivery
- reconnect/retry/idempotency handling
- secure device authentication and key rotation/revocation strategy
- preserve the same change log and conflict resolver used by local Wi-Fi sync
- prefer local transport when appropriate while allowing remote transport when peers are not locally reachable
- add remote-sync observability and tests without exposing household contents

**Iteration 2 exit criteria:** paired devices can converge while on unrelated networks/mobile data, with household content encrypted before it leaves the device and without moving the authoritative data model into a cloud database.

## Proposed solution structure

```text
src/
  FamilyApp.Core/
  FamilyApp.Data/
  FamilyApp.Sync/
  FamilyApp.Shared/
  FamilyApp.Maui/
  FamilyApp.Web/

tests/
  FamilyApp.Core.Tests/
  FamilyApp.Sync.Tests/

.github/
  workflows/
    ci.yml
    deploy-preview.yml
```

## Architecture rule

All synchronization must sit behind transport abstractions. Domain features (meals, shopping, inventory and calendar) must not know whether a change arrived over local Wi-Fi or a future remote relay.

## Current roadmap

- **Iteration 1:** local-first product, browser preview/CI and trusted Wi-Fi peer-to-peer synchronization.
- **Iteration 2:** encrypted remote transport for internet/mobile-data synchronization.

Implementation work should be delivered in small reviewable pull requests so each capability can be tested before the next one is added.

## Issue delivery notes

- Deliver each actionable issue on its own `feature/issue-N-*` branch and merge branches into `main` sequentially after CI and the Netlify deployment succeed.
- Issue #1 is the Iteration 1 parent for issues #10–#14. Issue #2 is the separate Iteration 2 milestone and follows Iteration 1 stabilization.
- Issue #10 adds an in-process two-device simulator. Automated tests cover independent replicas, bidirectional missing-change exchange, retry idempotency and deterministic convergence without iOS networking.
- Issue #11 adds a QR-based ECDH pairing flow. QR payloads contain public keys and nonces; private keys and derived peer secrets use the secure pairing-store abstraction.
- Issue #12 adds a Bonjour peer-discovery contract and iOS `NWBrowser` adapter, with local-network permission metadata for `_familyapp-sync._tcp`.
- Issue #13 adds a trusted-peer sync coordinator and AES-GCM change exchange bound to family and device identities, with retryable discovery/connection handling.
- Issue #14 adds sync-state and last-successful-sync UX with persisted status restoration and tests for reconnect, duplicate delivery and restart behavior.
- The browser preview persists only the nonsecret last-sync timestamp in local storage; live iPhone sync status requires the future MAUI host to register its native pairing, discovery and transport services.
- The MAUI host is still a placeholder excluded from the solution, so iOS adapter compilation and two-iPhone acceptance must be verified when that host is introduced.
- Browser and automated checks do not replace the two-iPhone acceptance checks required for native pairing, discovery and local transport.
