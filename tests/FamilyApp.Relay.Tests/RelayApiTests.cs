using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyApp.Relay.Contracts;
using FamilyApp.Relay.Security;
using FamilyApp.Sync.Transport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyApp.Relay.Tests;

public class RelayApiTests
{
    [Fact]
    public async Task Mailbox_is_family_scoped_idempotent_acknowledged_and_revocable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"familyapp-relay-{Guid.NewGuid():N}.db");
        using (var factory = new RelayApiFactory(databasePath))
        using (var sender = factory.CreateClient())
        using (var recipient = factory.CreateClient())
        using (var otherFamily = factory.CreateClient())
        using (var familyAdmin = factory.CreateClient())
        {
            var familyId = Guid.NewGuid();
            var otherFamilyId = Guid.NewGuid();
            var senderDeviceId = Guid.NewGuid();
            var recipientDeviceId = Guid.NewGuid();
            SetIdentity(sender, familyId, senderDeviceId);
            SetIdentity(recipient, familyId, recipientDeviceId);
            SetIdentity(otherFamily, otherFamilyId, Guid.NewGuid());
            SetIdentity(familyAdmin, familyId, Guid.NewGuid(), "admin");

            var request = new EnqueueRelayMessageRequest(
                Guid.NewGuid(), recipientDeviceId, DateTimeOffset.UtcNow.AddDays(1), [9, 8, 7, 6]);
            var created = await sender.PostAsJsonAsync("/api/v1/relay/messages", request, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.NotNull(created.Headers.Location);

            var retry = await sender.PostAsJsonAsync("/api/v1/relay/messages", request, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
            var conflict = await sender.PostAsJsonAsync(
                "/api/v1/relay/messages", request with { Ciphertext = [1, 2, 3] }, cancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

            var pending = await recipient.GetFromJsonAsync<RelayMessagesResponse>(
                "/api/v1/relay/messages", cancellationToken);
            Assert.NotNull(pending);
            var message = Assert.Single(pending.Messages);
            Assert.Equal(senderDeviceId, message.SenderDeviceId);
            Assert.Equal(request.Ciphertext, message.Ciphertext);

            var otherInbox = await otherFamily.GetFromJsonAsync<RelayMessagesResponse>(
                "/api/v1/relay/messages", cancellationToken);
            Assert.Empty(Assert.IsType<RelayMessagesResponse>(otherInbox).Messages);

            var lookup = await recipient.GetAsync($"/api/v1/relay/messages/{request.MessageId:D}", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            var acknowledged = await recipient.DeleteAsync($"/api/v1/relay/messages/{request.MessageId:D}", cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, acknowledged.StatusCode);

            var revoke = await familyAdmin.PostAsync(
                $"/api/v1/relay/devices/{recipientDeviceId:D}/revoke", content: null, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
            var revokedAccess = await recipient.GetAsync("/api/v1/relay/messages", cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, revokedAccess.StatusCode);
        }

        File.Delete(databasePath);
    }

    [Fact]
    public async Task Mailbox_requires_authenticated_family_and_device_claims()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"familyapp-relay-{Guid.NewGuid():N}.db");
        using (var factory = new RelayApiFactory(databasePath))
        using (var client = factory.CreateClient())
        {
            var response = await client.GetAsync("/api/v1/relay/messages", cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        File.Delete(databasePath);
    }

    [Fact]
    public async Task Recipient_mailbox_has_a_fixed_capacity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"familyapp-relay-{Guid.NewGuid():N}.db");
        using (var factory = new RelayApiFactory(databasePath))
        using (var sender = factory.CreateClient())
        {
            var familyId = Guid.NewGuid();
            SetIdentity(sender, familyId, Guid.NewGuid());
            var recipientDeviceId = Guid.NewGuid();

            for (var index = 0; index < 101; index++)
            {
                var request = new EnqueueRelayMessageRequest(
                    Guid.NewGuid(), recipientDeviceId, DateTimeOffset.UtcNow.AddDays(1), [(byte)index]);
                var response = await sender.PostAsJsonAsync("/api/v1/relay/messages", request, cancellationToken);
                Assert.Equal(index < 100 ? HttpStatusCode.Created : HttpStatusCode.Conflict, response.StatusCode);
            }
        }

        File.Delete(databasePath);
    }

    [Fact]
    public async Task Shared_http_client_can_enqueue_poll_and_ack_through_the_relay_api()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"familyapp-relay-{Guid.NewGuid():N}.db");
        using (var factory = new RelayApiFactory(databasePath))
        using (var senderHttp = factory.CreateClient())
        using (var recipientHttp = factory.CreateClient())
        {
            var familyId = Guid.NewGuid();
            var senderDeviceId = Guid.NewGuid();
            var recipientDeviceId = Guid.NewGuid();
            SetIdentity(senderHttp, familyId, senderDeviceId);
            SetIdentity(recipientHttp, familyId, recipientDeviceId);
            var sender = new HttpRemoteRelayClient(senderHttp, new TestAccessTokenProvider());
            var recipient = new HttpRemoteRelayClient(recipientHttp, new TestAccessTokenProvider());
            var timestamp = DateTimeOffset.UtcNow;
            var message = new EncryptedRelayMessage(
                Guid.NewGuid(), familyId, senderDeviceId, recipientDeviceId,
                timestamp, timestamp.AddDays(1), [4, 3, 2, 1]);

            await sender.EnqueueAsync(message, cancellationToken);
            var pending = await recipient.GetPendingAsync(familyId, recipientDeviceId, cancellationToken);
            var received = Assert.Single(pending);
            Assert.Equal(message.MessageId, received.MessageId);
            Assert.Equal(message.Ciphertext, received.Ciphertext);
            await recipient.AcknowledgeAsync(familyId, recipientDeviceId, message.MessageId, cancellationToken);
            Assert.Empty(await recipient.GetPendingAsync(familyId, recipientDeviceId, cancellationToken));
        }

        File.Delete(databasePath);
    }

    private static void SetIdentity(HttpClient client, Guid familyId, Guid deviceId, string? role = null)
    {
        client.DefaultRequestHeaders.Add("X-Test-Family", familyId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Test-Device", deviceId.ToString("D"));
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
    }

    private sealed class RelayApiFactory(string databasePath) : WebApplicationFactory<Program>
    {
        static RelayApiFactory()
        {
            Environment.SetEnvironmentVariable("RelayAuth__Authority", "https://issuer.example.test/");
            Environment.SetEnvironmentVariable("RelayAuth__Audience", "familyapp-relay");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RelayAuth:Authority"] = "https://issuer.example.test/",
                    ["RelayAuth:Audience"] = "familyapp-relay",
                    ["ConnectionStrings:Relay"] = $"Data Source={databasePath}"
                }));
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName, _ => { }));
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "RelayTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Family", out var familyId) ||
                !Request.Headers.TryGetValue("X-Test-Device", out var deviceId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(RelayCaller.FamilyIdClaim, familyId.ToString()),
                new(RelayCaller.DeviceIdClaim, deviceId.ToString())
            };
            if (Request.Headers.TryGetValue("X-Test-Role", out var role))
                claims.Add(new Claim(RelayCaller.FamilyRoleClaim, role.ToString()));

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class TestAccessTokenProvider : IRelayAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("test-only-access-token");
        }
    }
}