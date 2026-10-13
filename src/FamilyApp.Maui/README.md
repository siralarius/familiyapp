# FamilyApp.Maui

The iPhone host renders `FamilyApp.Shared` with MAUI Blazor Hybrid. Household changes and tombstones are persisted to SQLite before sync acknowledgement. Pairing secrets use MAUI SecureStorage (iOS Keychain); preferences contain only device/family IDs and the nonsecret last-sync timestamp.

## Build on a Mac

Use Xcode 26.5 and .NET SDK 10.0.300 (or a patch in the same 10.0.3xx feature band). The iOS workflow installs the SDK selected by this directory’s `global.json`. Keep the SDK in the 10.0.300 feature band: workload set 10.0.300.3 contains the iOS 26.5 workload, and changing SDK feature bands changes the workload-set package used during installation. Run these commands from this directory. The workload version comes from `global.json`; do not also pass `--version` to `dotnet workload install`:

```sh
sudo dotnet workload install maui-ios
dotnet build FamilyApp.Maui.csproj -c Debug -p:RuntimeIdentifier=iossimulator-x64
```

On an Apple Silicon Mac use `iossimulator-arm64`. The native project is separate from `FamilyApp.slnx` so Linux browser/relay CI does not need Apple workloads. Do not suppress Xcode version checks; use a matching workload instead.

## Install on a personal iPhone

Sign in to your Apple Account in Xcode Settings → Accounts. Connect and trust the iPhone, enable Developer Mode, and create an Apple Development signing certificate plus a provisioning profile for `com.siralarius.familyapp` (or override `ApplicationId` with your own unique bundle ID). A free Personal Team profile expires after seven days.

```sh
dotnet build FamilyApp.Maui.csproj -c Debug -p:RuntimeIdentifier=ios-arm64 \
  -p:CodesignKey='Apple Development: your certificate name' \
  -p:CodesignProvision='your provisioning profile name'
xcrun devicectl list devices
xcrun devicectl device install app --device YOUR_DEVICE_ID \
  bin/Debug/net10.0-ios26.5/ios-arm64/FamilyApp.Maui.app
```

Open Family App on the phone after installation. Certificate/profile setup is personal account configuration; no passwords or private keys belong in the repository.

## Pair and verify two phones

1. On the first phone open More → Family devices and create an offer QR code.
2. On the fresh second phone tap Scan offer QR code, allow Camera permission when prompted, then confirm joining the scanned family and create the response QR code.
3. On the first phone tap Scan response QR code to complete pairing. Offer and response codes expire after five minutes.
4. Validate camera error handling on a physical iPhone: deny permission, cancel scan, scan invalid/non-pairing QR content, scan an expired code, and rescan the same code. Each case must show a clear message and must not establish trust.
5. Validate the manual fallback still works by using Family code plus offer/response text entry when camera scanning is unavailable.
6. Allow Local Network access. Keep both apps in the foreground on the same Wi-Fi. Discovery uses Bonjour, and trusted peers are synchronized every ten seconds with bounded connection timeouts.
7. Add/edit/delete shopping, inventory, meals and calendar entries on either phone. Navigate away and back to refresh the view on the other phone.
8. Verify independent offline edits, reconnect, conflicting edits, duplicate exchange and force-quit/relaunch persistence. Turn Wi-Fi off/on and background/foreground both apps.

Simulator launch and automated tests do not establish physical-camera acceptance or physical two-iPhone pairing acceptance. Record device models, OS versions and observed convergence before closing #1/#12/#13/#14. iOS suspends ordinary apps in the background; this host resumes sync when active rather than promising background connectivity.

## Siri shopping shortcut acceptance

Family App includes an in-app Siri setup guide at More -> Set up Siri. The current native integration uses a Siri-triggered shortcut URL endpoint (`familyapp://shopping/add?item=...`) that writes through the same local SQLite shopping service and sync pipeline as manual edits.

Physical-device acceptance for #48 must validate:

1. Trigger Siri with the configured phrase and add an item (for example, milk), then verify it appears in Shopping.
2. Force-close and reopen Family App; the item must remain.
3. If a second trusted phone is paired and reachable, verify the new item syncs normally.
4. Test from a locked device and while the app is closed. If iOS requests unlock or briefly foregrounds the app, record that as the observed platform behavior rather than promising lock-screen background execution.

Browser checks and automated tests do not replace this physical Siri verification.

## Outstanding external configuration

The native host currently uses local Wi-Fi transport. #2 still requires selection/configuration of a JWT issuer, durable public relay hosting, native token acquisition and two-phone tests on unrelated networks. Google Calendar OAuth in #25 is a browser deployment feature; native Google sign-in is not configured by the web client's Netlify setting.
