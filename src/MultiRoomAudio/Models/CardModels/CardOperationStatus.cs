namespace MultiRoomAudio.Models.CardModels;

/// <summary>
/// Status codes for card operations enabling structured error handling.
/// </summary>
public enum CardOperationStatus
{
    /// <summary>Operation completed successfully.</summary>
    Success,

    /// <summary>The specified card was not found.</summary>
    CardNotFound,

    /// <summary>The specified profile was not found or is not available.</summary>
    ProfileNotFound,

    /// <summary>The profile is not available for use.</summary>
    ProfileNotAvailable,

    /// <summary>No sinks were found for the card.</summary>
    NoSinksFound,

    /// <summary>One or more sinks failed during the operation.</summary>
    PartialFailure,

    /// <summary>A general/unexpected error occurred.</summary>
    Error
}
