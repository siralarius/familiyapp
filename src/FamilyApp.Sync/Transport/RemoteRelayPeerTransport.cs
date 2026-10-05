using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FamilyApp.Sync.Discovery;

namespace FamilyApp.Sync.Transport;

public sealed record EncryptedRelayMessage(
    Guid MessageId,
    Guid FamilyId,
    Guid SenderDeviceId,
    Guid RecipientDeviceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    byte[] Ciphertext);

public sealed record EncryptedRelayMessageBatch(IReadOnlyList<EncryptedRelayMessage> Messages);

public interface IRelayAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteRelayClient
{
    Task EnqueueAsync(EncryptedRelayMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(
        Guid familyId, Guid recipientDeviceId, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default);
}

public sealed class HttpRemoteRelayClient(HttpClient httpClient, IRelayAccessTokenProvider tokenProvider) : IRemoteRelayClient
{
    public async Task EnqueueAsync(EncryptedRelayMessage message, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "api/v1/relay/messages", cancellationToken);
        request.Content = JsonContent.Create(message);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("The relay rejected a conflicting message ID.");
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(
        Guid familyId, Guid recipientDeviceId, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/relay/messages?familyId={familyId:D}&recipientDeviceId={recipientDeviceId:D}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var batch = await response.Content.ReadFromJsonAsync<EncryptedRelayMessageBatch>(cancellationToken: cancellationToken);
        return batch?.Messages ?? [];
    }

    public async Task AcknowledgeAsync(
        Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/relay/messages/{messageId:D}?familyId={familyId:D}&recipientDeviceId={recipientDeviceId:D}";
        using var request = await CreateRequestAsync(HttpMethod.Delete, path, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("A relay access token is required.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}

public sealed class RemoteRelayPeerTransport(
    Guid familyId,
    Guid localDeviceId,
    IRemoteRelayClient relayClient,
    TimeProvider? timeProvider = null) : IPeerTransport, IPeerListener
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        cancellationToken.ThrowIfCancellationRequested();
        var peerDeviceId = peer.DeviceId ?? throw new ArgumentException("The peer has no valid device ID.", nameof(peer));
        if (familyId == Guid.Empty || localDeviceId == Guid.Empty || peerDeviceId == localDeviceId)
            throw new ArgumentException("Relay sync requires a valid family and distinct device IDs.", nameof(peer));

        return Task.FromResult<IPeerConnection>(new RelayPeerConnection(
            familyId, localDeviceId, peerDeviceId, relayClient, _timeProvider, null, null));
    }

    public async IAsyncEnumerable<IPeerConnection> AcceptAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var messages = await relayClient.GetPendingAsync(familyId, localDeviceId, cancellationToken);
            foreach (var message in messages.OrderBy(message => message.CreatedAtUtc))
            {
                if (message.FamilyId != familyId || message.RecipientDeviceId != localDeviceId ||
                    message.SenderDeviceId == Guid.Empty || message.MessageId == Guid.Empty ||
                    !_inFlight.TryAdd(message.MessageId, 0))
                    continue;

                yield return new RelayPeerConnection(
                    familyId,
                    localDeviceId,
                    message.SenderDeviceId,
                    relayClient,
                    _timeProvider,
                    message,
                    messageId => _inFlight.TryRemove(messageId, out _));
            }

            await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, cancellationToken);
        }
    }
}

public sealed class FallbackPeerTransport(params IPeerTransport[] transports) : IPeerTransport
{
    private readonly IPeerTransport[] _transports = transports.Length > 0
        ? transports
        : throw new ArgumentException("At least one peer transport is required.", nameof(transports));

    public async Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
    {
        Exception? lastFailure = null;
        foreach (var transport in _transports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await transport.ConnectAsync(peer, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        throw new IOException("No peer transport could connect.", lastFailure);
    }
}

internal sealed class RelayPeerConnection(
    Guid familyId,
    Guid localDeviceId,
    Guid peerDeviceId,
    IRemoteRelayClient relayClient,
    TimeProvider timeProvider,
    EncryptedRelayMessage? firstIncomingMessage,
    Action<Guid>? onMessageCompleted) : IPeerConnection
{
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private EncryptedRelayMessage? _incoming = firstIncomingMessage;

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var messageId = CreateMessageId(familyId, localDeviceId, peerDeviceId, message.Span);
        var now = timeProvider.GetUtcNow();
        await relayClient.EnqueueAsync(new EncryptedRelayMessage(
            messageId,
            familyId,
            localDeviceId,
            peerDeviceId,
            now,
            now.Add(MessageLifetime),
            message.ToArray()), cancellationToken);
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_incoming is not null)
                return _incoming.Ciphertext;

            var messages = await relayClient.GetPendingAsync(familyId, localDeviceId, cancellationToken);
            _incoming = messages.FirstOrDefault(message =>
                message.FamilyId == familyId &&
                message.SenderDeviceId == peerDeviceId &&
                message.RecipientDeviceId == localDeviceId);
            if (_incoming is not null)
                return _incoming.Ciphertext;

            await Task.Delay(PollInterval, timeProvider, cancellationToken);
        }
    }

    public async Task AcknowledgeAsync(CancellationToken cancellationToken = default)
    {
        if (_incoming is null) return;
        await relayClient.AcknowledgeAsync(familyId, localDeviceId, _incoming.MessageId, cancellationToken);
        onMessageCompleted?.Invoke(_incoming.MessageId);
        _incoming = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_incoming is not null)
        {
            onMessageCompleted?.Invoke(_incoming.MessageId);
            _incoming = null;
        }

        return ValueTask.CompletedTask;
    }

    private static Guid CreateMessageId(Guid familyId, Guid senderDeviceId, Guid recipientDeviceId, ReadOnlySpan<byte> payload)
    {
        var prefix = Encoding.UTF8.GetBytes($"{familyId:N}|{senderDeviceId:N}|{recipientDeviceId:N}|");
        var input = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(input, 0);
        payload.CopyTo(input.AsSpan(prefix.Length));
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}