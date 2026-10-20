# FamilyApp.Maui

The iPhone host renders `FamilyApp.Shared` with MAUI Blazor Hybrid. Household changes and tombstones are persisted to SQLite before sync acknowledgement. Pairing secrets use MAUI SecureStorage (iOS Keychain); preferences contain only device/family IDs and the nonsecret last-sync timestamp.

## Build on a Mac

Use Xcode 26.5 and a .NET 10 SDK compatible with the minimum version in this directory’s `global.json`. The `latestFeature` policy permits installed newer .NET 10 feature bands, including 10.0.401. The workload version remains pinned to 10.0.300.3 for iOS 26.5. Run workload installation and native build commands from this directory so that pin applies; do not also pass `--version` to `dotnet workload install`. Simulator builds use ad-hoc signing and do not require an Apple Development certificate. Physical iPhone builds still require your personal signing identity and provisioning profile.

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
2. On the fresh second phone tap Scan QR and join, allow Camera permission when prompted, and scan the offer. Scanning authorizes joining that family; no confirmation or response QR scan is required.
3. The second phone returns its authenticated pairing response over Bonjour/TCP and both devices exchange an encrypted snapshot. Both screens return automatically to More → Device sync after success. A connection loss triggers one automatic reconnect with the same response; the host accepts only its exact previously authenticated response while that trust is retained, even if the original offer has since expired. Both sides repeat the encrypted snapshot and confirmation exchange, without another scan. If both attempts fail, pairing does not report success; keep both apps open on the same Wi-Fi and scan a fresh offer. Codes expire after five minutes.
4. Validate camera error handling on a physical iPhone: deny permission, cancel scan, scan invalid/non-pairing QR content, scan an expired code, and scan a family offer on a phone that already contains household data. Each failed case must show a clear message without replacing that family or establishing unintended trust.
5. Validate the manual fallback still works by expanding Manual pairing fallback and using Family code plus offer/response text entry when camera scanning is unavailable.
6. Allow Local Network access. Keep both apps in the foreground on the same Wi-Fi. Discovery uses Bonjour, and trusted peers are synchronized every ten seconds with bounded connection timeouts.
7. Add/edit/delete shopping, inventory, meals and calendar entries on either phone. Navigate away and back to refresh the view on the other phone.
8. Verify independent offline edits, reconnect, conflicting edits, duplicate exchange and force-quit/relaunch persistence. Turn Wi-Fi off/on and background/foreground both apps.

Simulator launch and automated tests do not establish physical-camera acceptance or physical two-iPhone pairing acceptance. Record device models, OS versions and observed convergence before closing #1/#12/#13/#14. iOS suspends ordinary apps in the background; this host resumes sync when active rather than promising background connectivity.

## Outstanding external configuration

The native host currently uses local Wi-Fi transport. #2 still requires selection/configuration of a JWT issuer, durable public relay hosting, native token acquisition and two-phone tests on unrelated networks. Google Calendar OAuth in #25 is a browser deployment feature; native Google sign-in is not configured by the web client's Netlify setting.
