using FamilyApp.Sync.Transport;

namespace FamilyApp.Relay.Services;

public enum RelayEnqueueResult
{
    Created,
    Duplicate,
    Conflict
}

public interface IRelayMessageStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<RelayEnqueueResult> EnqueueAsync(EncryptedRelayMessage message, CancellationToken cancellationToken = default);
    Task<EncryptedRelayMessage?> GetByIdAsync(Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(Guid familyId, Guid recipientDeviceId, int limit, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default);
    Task RevokeDeviceAsync(Guid familyId, Guid deviceId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default);
    Task<bool> IsDeviceRevokedAsync(Guid familyId, Guid deviceId, CancellationToken cancellationToken = default);
}