using MultiRoomAudio.Models;
using MultiRoomAudio.Services;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for PulseAudio sound card profile management.
/// Allows listing cards, viewing available profiles, and changing the active profile.
/// </summary>
public static class CardsEndpoint
{
    public static void MapCardsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cards")
            .WithTags("Cards")
            .WithOpenApi();

        // GET /api/cards - List all sound cards with their profiles
        group.MapGet("/", (CardProfileService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: GET /api/cards");

            try
            {
                var cards = service.GetCards().ToList();
                logger.LogDebug("API: Found {CardCount} sound cards", cards.Count);

                return Results.Ok(new CardsListResponse(cards, cards.Count));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to enumerate sound cards");
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to enumerate sound cards");
            }
        })
        .WithName("ListCards")
        .WithDescription("List all PulseAudio sound cards with their available profiles");

        // GET /api/cards/saved - Get saved profile configurations
        group.MapGet("/saved", (CardProfileService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: GET /api/cards/saved");

            var saved = service.GetSavedProfiles();
            return Results.Ok(new
            {
                profiles = saved,
                count = saved.Count
            });
        })
        .WithName("GetSavedProfiles")
        .WithDescription("Get all saved card profile configurations that will be restored on startup");

        // GET /api/cards/{nameOrIndex} - Get specific card
        group.MapGet("/{nameOrIndex}", (string nameOrIndex, CardProfileService service, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: GET /api/cards/{CardId}", nameOrIndex);

            try
            {
                var card = service.GetCard(nameOrIndex);
                if (card == null)
                {
                    logger.LogDebug("API: Card {CardId} not found", nameOrIndex);
                    return Results.NotFound(new ErrorResponse(false, $"Card '{nameOrIndex}' not found"));
                }

                return Results.Ok(card);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to get card {CardId}", nameOrIndex);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to get card");
            }
        })
        .WithName("GetCard")
        .WithDescription("Get details of a specific sound card including available profiles");

        // PUT /api/cards/{nameOrIndex}/profile - Set active profile
        group.MapPut("/{nameOrIndex}/profile", async (
            string nameOrIndex,
            SetCardProfileRequest request,
            CardProfileService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: PUT /api/cards/{CardId}/profile - Setting to {Profile}",
                nameOrIndex, request.Profile);

            try
            {
                var result = await service.SetCardProfileAsync(nameOrIndex, request.Profile);

                if (!result.Success)
                {
                    // Determine appropriate status code
                    if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.NotFound(new ErrorResponse(false, result.Message));
                    }
                    if (result.Message.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                        result.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.BadRequest(new ErrorResponse(false, result.Message));
                    }
                    return Results.Problem(
                        detail: result.Message,
                        statusCode: 500,
                        title: "Failed to set profile");
                }

                logger.LogInformation("API: Card {CardName} profile changed to {Profile}",
                    result.CardName, result.ActiveProfile);

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "API: Failed to set profile for card {CardId}", nameOrIndex);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to set profile");
            }
        })
        .WithName("SetCardProfile")
        .WithDescription("Change the active profile for a sound card (e.g., switch from stereo to 7.1 surround)");

        // PUT /api/cards/{nameOrIndex}/boot-mute - Set boot mute preference
        group.MapPut("/{nameOrIndex}/boot-mute", (
            string nameOrIndex,
            SetCardBootMuteRequest request,
            CardProfileService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: PUT /api/cards/{CardId}/boot-mute to {Muted}", nameOrIndex, request.Muted);

            var result = service.SetCardBootMute(nameOrIndex, request.Muted);
            if (!result.Success)
            {
                if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(new ErrorResponse(false, result.Message));
                }

                return Results.Problem(
                    detail: result.Message,
                    statusCode: 500,
                    title: "Failed to set boot mute");
            }

            return Results.Ok(result);
        })
        .WithName("SetCardBootMute")
        .WithDescription("Set the boot mute preference for a sound card (muted or unmuted on startup)");

        // PUT /api/cards/{nameOrIndex}/mute - Set mute state in real-time
        group.MapPut("/{nameOrIndex}/mute", async (
            string nameOrIndex,
            SetCardMuteRequest request,
            CardProfileService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: PUT /api/cards/{CardId}/mute to {Muted}", nameOrIndex, request.Muted);

            var result = await service.SetCardMuteAsync(nameOrIndex, request.Muted);
            if (!result.Success)
            {
                if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(new ErrorResponse(false, result.Message));
                }

                return Results.Problem(
                    detail: result.Message,
                    statusCode: 500,
                    title: "Failed to set mute");
            }

            return Results.Ok(result);
        })
        .WithName("SetCardMute")
        .WithDescription("Mute or unmute a sound card in real-time without changing boot preference");

        // DELETE /api/cards/{nameOrIndex}/saved - Remove saved profile for a card
        group.MapDelete("/{nameOrIndex}/saved", (
            string nameOrIndex,
            CardProfileService service,
            ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("CardsEndpoint");
            logger.LogDebug("API: DELETE /api/cards/{CardId}/saved", nameOrIndex);

            var removed = service.RemoveSavedProfile(nameOrIndex);
            if (!removed)
            {
                logger.LogDebug("API: No saved profile found for card {CardId}", nameOrIndex);
                return Results.NotFound(new ErrorResponse(false,
                    $"No saved profile found for card '{nameOrIndex}'"));
            }

            logger.LogInformation("API: Removed saved profile for card {CardId}", nameOrIndex);
            return Results.Ok(new SuccessResponse(true,
                $"Saved profile for card '{nameOrIndex}' removed"));
        })
        .WithName("RemoveSavedProfile")
        .WithDescription("Remove a saved profile configuration so it won't be restored on startup");
    }
}
