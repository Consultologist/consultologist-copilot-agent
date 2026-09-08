using Consultologist.CopilotAgent;
using Consultologist.CopilotAgent.Core.Engine;

using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;

var builder = WebApplication.CreateBuilder(args);

// The typed satellite client for the Consultologist engine. Base address comes
// from config (Engine:ApiHost); the per-turn delegated bearer is supplied by the
// agent on each call, never baked into the client.
builder.Services.AddHttpClient<EngineApiClient>(client =>
{
    var apiHost = builder.Configuration["Engine:ApiHost"]
        ?? throw new InvalidOperationException("Engine:ApiHost is not configured (see appsettings.json).");
    client.BaseAddress = new Uri(apiHost.TrimEnd('/') + "/");
});

// IStorage: MemoryStorage is fine for local dev. Production must use a persisted
// IStorage (Blob/Cosmos) so conversation state survives restarts and works
// across instances.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// Agent hosting defaults (AgentApplicationOptions, the channel adapter, turn
// state) plus the attachment downloaders, so an attached referral arrives
// already downloaded in turnState.Temp.InputFiles: AddAgentAttachmentDownloader
// for Teams personal chat, and AddAgentM365AttachmentDownloader for the M365
// Copilot surface's authenticated file URLs.
builder.AddAgentDefaults()
    .AddAgentAttachmentDownloader()
    .AddAgentM365AttachmentDownloader()
    .AddAgent<IntakeAgent>()
    .AddAgentAuthorization(b => b.AddAgentAspNetAuthentication());

var app = builder.Build();

// Authentication + authorization middleware for the agent pipeline.
app.UseAgents();

// GET "/" and the POST /api/messages agent endpoint the Azure Bot posts to.
app.MapDefaultAgentEndpoints();

app.Run();
