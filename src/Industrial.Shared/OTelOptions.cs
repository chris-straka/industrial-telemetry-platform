using System.ComponentModel.DataAnnotations;

namespace Industrial.Shared;

/// <summary>
/// Every service in the solution exports to the same collector, so this section is
/// identical everywhere and lives here rather than being copy-pasted five times.
/// </summary>
public class OTelOptions
{
    public const string Section = "OTel";

    // [Required] already rejects null, empty AND whitespace-only strings --
    // RequiredAttribute.IsValid ends with stringValue.Trim().Length != 0, and
    // AllowEmptyStrings defaults to false. No need to spell that out.
    //
    // Note: the C# `required` keyword would NOT help here. It is enforced by the
    // compiler at object-initialiser call sites, and the configuration binder creates
    // objects by reflection, which skips it entirely.
    [Required]
    public string ServiceName { get; set; } = string.Empty;

    [Required]
    [Url]
    public string Endpoint { get; set; } = string.Empty;
}
