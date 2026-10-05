using System.Security.Claims;
using FamilyApp.Relay.Contracts;
using FamilyApp.Relay.Security;
using FamilyApp.Relay.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FamilyApp.Relay.Endpoints;

public static class RelayEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/relay/messages")
            .RequireAuthorization()
            .WithTags("Encrypted relay mailbox");

        group.MapPost("", async Task<Results<Created<RelayMessageResponse>, Conflict<ProblemDetails>, BadRequest<ProblemDetails>, ForbidHttpResult>> (
                EnqueueRelayMessageRequest request,
                HttpContext context,
                IRelayMailboxService mailbox,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!RelayCaller.TryCreate(context.User, out var caller))
                    return TypedResults.Forbid();

                var now = timeProvider.GetUtcNow();
                var message = new FamilyApp.Sync.Transport.EncryptedRelayMessage(
                    request.MessageId,
                    caller.FamilyId,
                    caller.DeviceId,
                    request.RecipientDeviceId,
                    now,
                    request.ExpiresAtUtc,
                    request.Ciphertext);
                var result = await mailbox.EnqueueAsync(message, cancellationToken);
                var response = RelayMessageResponse.From(message);

                return result switch
                {
                    RelayMailboxResult.Accepted or RelayMailboxResult.Duplicate =>
                        TypedResults.Created($"/api/v1/relay/messages/{message.MessageId:D}", response),
                    RelayMailboxResult.Conflict => TypedResults.Conflict(new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "Message ID conflict",
                        Detail = "The message ID conflicts with an existing relay message or revoked device."
                    }),
                    _ => TypedResults.BadRequest(new ProblemDetails
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Title = "Invalid encrypted message",
                        Detail = "The relay message metadata or ciphertext is invalid."
                    })
                };
            })
            .WithName("EnqueueEncryptedRelayMessage")
            .WithSummary("Queue an opaque encrypted message")
            .WithDescription("Stores ciphertext and routing metadata only. The relay does not decrypt household changes.")
            .Produces<RelayMessageResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("", async Task<Results<Ok<RelayMessagesResponse>, ForbidHttpResult>> (
                HttpContext context,
                IRelayMailboxService mailbox,
                CancellationToken cancellationToken) =>
            {
                if (!RelayCaller.TryCreate(context.User, out var caller))
                    return TypedResults.Forbid();

                var messages = await mailbox.GetPendingAsync(caller.FamilyId, caller.DeviceId, cancellationToken);
                return TypedResults.Ok(new RelayMessagesResponse(messages.Select(RelayMessageResponse.From).ToArray()));
            })
            .WithName("GetPendingEncryptedRelayMessages")
            .WithSummary("Read pending messages for the authenticated device")
            .WithDescription("Returns opaque encrypted messages addressed to this device in its authenticated family.")
            .Produces<RelayMessagesResponse>();

        group.MapGet("/{messageId:guid}", async Task<Results<Ok<RelayMessageResponse>, NotFound, ForbidHttpResult>> (
                Guid messageId,
                HttpContext context,
                IRelayMailboxService mailbox,
                CancellationToken cancellationToken) =>
            {
                if (!RelayCaller.TryCreate(context.User, out var caller))
                    return TypedResults.Forbid();
                var message = await mailbox.GetByIdAsync(caller.FamilyId, caller.DeviceId, messageId, cancellationToken);
                return message is null ? TypedResults.NotFound() : TypedResults.Ok(RelayMessageResponse.From(message));
            })
            .WithName("GetEncryptedRelayMessage")
            .WithSummary("Read one message addressed to the authenticated device")
            .Produces<RelayMessageResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{messageId:guid}", async Task<Results<NoContent, ForbidHttpResult>> (
                Guid messageId,
                HttpContext context,
                IRelayMailboxService mailbox,
                CancellationToken cancellationToken) =>
            {
                if (!RelayCaller.TryCreate(context.User, out var caller))
                    return TypedResults.Forbid();
                await mailbox.AcknowledgeAsync(caller.FamilyId, caller.DeviceId, messageId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("AcknowledgeEncryptedRelayMessage")
            .WithSummary("Acknowledge and delete a delivered message")
            .Produces(StatusCodes.Status204NoContent);

        endpoints.MapPost("/api/v1/relay/devices/{deviceId:guid}/revoke", async Task<Results<NoContent, ForbidHttpResult>> (
                Guid deviceId,
                HttpContext context,
                IRelayMailboxService mailbox,
                CancellationToken cancellationToken) =>
            {
                if (!RelayCaller.TryCreate(context.User, out var caller) ||
                    !string.Equals(context.User.FindFirstValue(RelayCaller.FamilyRoleClaim), "admin", StringComparison.Ordinal))
                    return TypedResults.Forbid();
                await mailbox.RevokeDeviceAsync(caller.FamilyId, deviceId, cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireAuthorization()
            .WithName("RevokeFamilyDevice")
            .WithSummary("Revoke a device and remove its queued messages")
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }
}