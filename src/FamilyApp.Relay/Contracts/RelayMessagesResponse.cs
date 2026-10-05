namespace FamilyApp.Relay.Contracts;

/// <summary>Contains pending encrypted messages for the authenticated device.</summary>
public sealed record RelayMessagesResponse(IReadOnlyList<RelayMessageResponse> Messages);