using FamilyApp.Relay.Endpoints;
using FamilyApp.Relay.Middleware;
using FamilyApp.Relay.Services;
using FamilyApp.Relay.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
var authority = builder.Configuration["RelayAuth:Authority"];
var audience = builder.Configuration["RelayAuth:Audience"];
if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("RelayAuth:Authority and RelayAuth:Audience must be configured.");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);
var connectionString = builder.Configuration.GetConnectionString("Relay") ?? "Data Source=relay.db";
builder.Services.AddSingleton<IRelayMessageStore>(_ => new SqliteRelayMessageStore(connectionString));
builder.Services.AddSingleton<IRelayMailboxService, RelayMailboxService>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsEnvironment("Testing"))
    app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<ActiveDeviceMiddleware>();
app.UseAuthorization();

await app.Services.GetRequiredService<IRelayMessageStore>().InitializeAsync();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapHealthChecks("/healthz");
app.MapRelayEndpoints();

await app.RunAsync();

public partial class Program;