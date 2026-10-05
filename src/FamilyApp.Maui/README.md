# FamilyApp.Maui

This directory is reserved for the .NET MAUI Blazor Hybrid native host. The project will reference FamilyApp.Shared and FamilyApp.Sync and provide native implementations for persistence, secure storage and networking abstractions. The pairing host must implement `ISecurePairingStore` with platform secure storage (such as MAUI `SecureStorage.Default`); it must not fall back to preferences, files, browser storage or ordinary app repositories. The iOS Bonjour adapter requires the `Platforms/iOS/Info.plist` entries in this directory to be included by the eventual MAUI project.

It is intentionally not part of the Linux CI solution until the MAUI workload/native project is introduced and platform-specific CI is configured.
