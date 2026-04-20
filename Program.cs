using System;
using Microsoft.AspNetCore.HttpOverrides;
using YOUVI.RelayServer.Hubs;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "5002";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
    });
});

// Configure SignalR with detailed errors and shorter keep-alive for better heartbeat detection
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// Configure in-project relay settings (no environment variables). Change values here as needed.
var relaySettings = new YOUVI.RelayServer.Models.RelaySettings
{
    HubToken = "youvi_relay_test_token"
};
builder.Services.AddSingleton(relaySettings);

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseCors("AllowAll");

app.MapGet("/", () => "YOUVI Relay Server");
app.MapHub<CallRoomHub>("/callRoomHub");

Console.WriteLine($">>> Starting on port: {port} (PORT env var: '{Environment.GetEnvironmentVariable("PORT") ?? "not set"}')");
app.Run();
