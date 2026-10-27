using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoSharp.Sim;

/// <summary>
/// A terminal disguise profile. Carries the model/User-Agent string, the IMEI TAC,
/// the generated IMEI/IMEISV, and the UK location + timezone used to spoof a UK iPhone.
/// </summary>
public sealed class DeviceProfile
{
    /// <summary>Marketing model name, e.g. "iPhone 18 Pro".</summary>
    public string Model { get; set; } = "iPhone 18 Pro";

    /// <summary>SIP User-Agent value sent to the P-CSCF.</summary>
    public string UserAgent { get; set; } = "iPhone Pro/18";

    /// <summary>8-digit Type Allocation Code prefix for generated IMEI.</summary>
    public string Tac { get; set; } = ImeiGenerator.DefaultIphoneProTac;

    /// <summary>Persisted generated 15-digit IMEI (stable across registrations).</summary>
    public string? Imei { get; set; }

    /// <summary>Derived 16-digit IMEISV.</summary>
    [JsonIgnore]
    public string? Imeisv => string.IsNullOrWhiteSpace(Imei) ? null : ImeiGenerator.ImeisvFromImei(Imei);

    /// <summary>Location latitude (UK, e.g. London 51.5074).</summary>
    public double Latitude { get; set; } = 51.5074;

    /// <summary>Location longitude (UK, e.g. London -0.1278).</summary>
    public double Longitude { get; set; } = -0.1278;

    /// <summary>IANA timezone id, e.g. "Europe/London".</summary>
    public string Timezone { get; set; } = "Europe/London";

    public string ToJson() => JsonSerializer.Serialize(this);

    public static DeviceProfile FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DeviceProfile();
        try { return JsonSerializer.Deserialize<DeviceProfile>(json) ?? new DeviceProfile(); }
        catch (JsonException) { return new DeviceProfile(); }
    }
}

/// <summary>Well-known default device profiles.</summary>
public static class DeviceProfiles
{
    /// <summary>Default UK iPhone 18 Pro profile (London, Europe/London).</summary>
    public static DeviceProfile IPhone18ProUk() => new()
    {
        Model = "iPhone 18 Pro",
        UserAgent = "iPhone Pro/18",
        Tac = ImeiGenerator.DefaultIphoneProTac,
        Latitude = 51.5074,
        Longitude = -0.1278,
        Timezone = "Europe/London"
    };
}
