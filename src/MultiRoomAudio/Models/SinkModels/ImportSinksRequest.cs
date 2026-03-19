using System.ComponentModel.DataAnnotations;

namespace MultiRoomAudio.Models.SinkModels;

/// <summary>
/// Request to import sinks from default.pa.
/// </summary>
public class ImportSinksRequest
{
    /// <summary>
    /// Line numbers of sinks to import from default.pa.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one line number is required.")]
    public required List<int> LineNumbers { get; set; }
}
