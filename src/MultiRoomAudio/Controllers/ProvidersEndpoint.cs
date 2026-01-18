using MultiRoomAudio.Models;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for provider information.
/// </summary>
public static class ProvidersEndpoint
{
    /// <summary>
    /// Registers provider information API endpoints with the application.
    /// </summary>
    /// <remarks>
    /// Endpoints:
    /// <list type="bullet">
    /// <item>GET /api/providers - List available audio player providers</item>
    /// </list>
    /// </remarks>
    /// <param name="app">The WebApplication to register endpoints on.</param>
    public static void MapProvidersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/providers")
            .WithTags("Providers")
            .WithOpenApi();

        // GET /api/providers - List available providers
        group.MapGet("/", () =>
        {
            // Sendspin-only implementation
            var providers = new[]
            {
                new ProviderInfo
                {
                    Type = "sendspin",
                    DisplayName = "Sendspin",
                    Available = true,
                    Description = "Native SendSpin.SDK audio streaming"
                }
            };

            return Results.Ok(providers);
        })
        .WithName("ListProviders")
        .WithDescription("Get available audio player providers");
    }
}
