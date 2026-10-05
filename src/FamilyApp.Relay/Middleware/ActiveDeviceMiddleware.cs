using FamilyApp.Relay.Security;
using FamilyApp.Relay.Services;
using Microsoft.AspNetCore.Mvc;

namespace FamilyApp.Relay.Middleware;

public sealed class ActiveDeviceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRelayMailboxService mailbox)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!RelayCaller.TryCreate(context.User, out var caller) ||
                await mailbox.IsDeviceRevokedAsync(caller.FamilyId, caller.DeviceId, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Device access revoked",
                    Detail = "This device is not authorized to use the relay."
                }, context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}