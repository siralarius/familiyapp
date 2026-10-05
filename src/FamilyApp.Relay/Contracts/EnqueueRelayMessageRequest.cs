namespace FamilyApp.Relay.Contracts;

/// <summary>Contains an opaque encrypted message and its intended family device.</summary>
public sealed record EnqueueRelayMessageRequest(
    Guid MessageId,
    Guid RecipientDeviceId,
    DateTimeOffset ExpiresAtUtc,
    byte[] Ciphertext);