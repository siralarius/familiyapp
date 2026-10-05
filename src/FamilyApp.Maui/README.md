# FamilyApp.Maui

The iPhone host renders `FamilyApp.Shared` with MAUI Blazor Hybrid. Household changes and tombstones are persisted to SQLite before sync acknowledgement. Pairing secrets use MAUI SecureStorage (iOS Keychain); preferences contain only device/family IDs and the nonsecret last-sync timestamp.

## Build on a Mac

Use Xcode 26.5 and .NET SDK 10.0.401. Run these commands from this directory so its `global.json` selects the Xcode-compatible workload:

```sh
sudo dotnet workload install maui-ios --version 10.0.300.3
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

1. On the first phone open More → Family devices and copy its family code.
2. On the fresh second phone join that family before adding household data. Joining a different family is refused once local data exists, preventing accidental cross-family data sharing.
3. Create an offer on the first phone, copy it to the second and accept it. Copy the response to the first and complete pairing. Both codes expire after five minutes. The current host supports copying pairing codes; camera/QR rendering remains an onboarding improvement.
4. Allow Local Network access. Keep both apps in the foreground on the same Wi-Fi. Discovery uses Bonjour, and trusted peers are synchronized every ten seconds with bounded connection timeouts.
5. Add/edit/delete shopping, inventory, meals and calendar entries on either phone. Navigate away and back to refresh the view on the other phone.
6. Verify independent offline edits, reconnect, conflicting edits, duplicate exchange and force-quit/relaunch persistence. Turn Wi-Fi off/on and background/foreground both apps.

Simulator launch and automated tests do not establish physical two-iPhone acceptance. Record device models, OS versions and observed convergence before closing #1/#12/#13/#14. iOS suspends ordinary apps in the background; this host resumes sync when active rather than promising background connectivity.

## Outstanding external configuration

The native host currently uses local Wi-Fi transport. #2 still requires selection/configuration of a JWT issuer, durable public relay hosting, native token acquisition and two-phone tests on unrelated networks. Google Calendar OAuth in #25 is a browser deployment feature; native Google sign-in is not configured by the web client's Netlify setting.
