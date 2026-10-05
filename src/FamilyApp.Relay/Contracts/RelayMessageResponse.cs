using FamilyApp.Sync.Transport;

namespace FamilyApp.Relay.Contracts;

/// <summary>Describes an opaque encrypted message queued for a trusted device.</summary>
public sealed record RelayMessageResponse(
    Guid MessageId,
    Guid FamilyId,
    Guid SenderDeviceId,
    Guid RecipientDeviceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    byte[] Ciphertext)
{
    public static RelayMessageResponse From(EncryptedRelayMessage message)
        => new(message.MessageId, message.FamilyId, message.SenderDeviceId, message.RecipientDeviceId,
            message.CreatedAtUtc, message.ExpiresAtUtc, message.Ciphertext);
}