using System.ComponentModel.DataAnnotations;

namespace Industrial.Shared;

/// <summary>
/// OpenTelemetry exporter settings shared by every service, which all export to one collector.
/// </summary>
public class OTelOptions
{
    public const string Section = "OTel";

    // [Required] already rejects null, empty, and whitespace-only strings because
    // AllowEmptyStrings defaults to false. The C# required keyword would not help, because the
    // configuration binder creates objects by reflection and bypasses it.
    [Required]
    public string ServiceName { get; set; } = string.Empty;

    [Required]
    [Url]
    public string Endpoint { get; set; } = string.Empty;
}
