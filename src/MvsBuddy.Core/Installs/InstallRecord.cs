using System.Text.Json;
using System.Text.Json.Serialization;

namespace MvsBuddy.Installs;

/// <summary>
/// What MVS Buddy wrote down about the OpenVersus it installed: <c>plugins/OpenVersus/mvs-buddy-install.json</c>. It sits
/// with the mod's own files, so turning the mod off and on, a clean start and undoing an install carry it along with them.
/// </summary>
public sealed class InstallRecord
{
    /// <summary>The record's file name, inside <c>plugins/OpenVersus/</c>.</summary>
    public const string FileName = "mvs-buddy-install.json";

    /// <summary><c>Prod</c> or <c>Testing</c>.</summary>
    public string Channel { get; set; } = "";

    /// <summary>Who said which channel this is: <c>install</c> (MVS Buddy installed it) or <c>person</c> (they corrected it).</summary>
    public string ChannelFrom { get; set; } = "install";

    /// <summary>The release's git tag.</summary>
    public string? Tag { get; set; }

    /// <summary>The release's version, such as <c>2026.10.12.01</c>.</summary>
    public string? Version { get; set; }

    /// <summary>When it was installed, or when the person corrected the channel.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>MVS Buddy's version that wrote this.</summary>
    public string? Buddy { get; set; }

    /// <summary>The release's install zip, as downloaded.</summary>
    public RecordedFile? Zip { get; set; }

    /// <summary>The plugin it put in place.</summary>
    public RecordedFile? Plugin { get; set; }

    /// <summary>The paks it installed or found already in place.</summary>
    public List<RecordedFile> Paks { get; set; } = [];

    /// <summary>Ultimate ASI Loader as it was installed.</summary>
    public RecordedFile? Loader { get; set; }

    /// <summary>The channel, when the record names one MVS Buddy knows.</summary>
    [JsonIgnore]
    public Catalog.Channel? ChannelValue => Enum.TryParse<Catalog.Channel>(Channel, ignoreCase: false, out var c) ? c : null;

    internal byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, InstallRecordJsonContext.Default.InstallRecord);

    internal InstallRecord Copy() => JsonSerializer.Deserialize(ToJson(), InstallRecordJsonContext.Default.InstallRecord)!;

    /// <summary>The record in <paramref name="json"/>; null when it cannot be read.</summary>
    internal static InstallRecord? FromJson(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, InstallRecordJsonContext.Default.InstallRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The record at <paramref name="path"/>; null when there is none or it cannot be read.</summary>
    internal static InstallRecord? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), InstallRecordJsonContext.Default.InstallRecord) : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
        {
            return null;
        }
    }
}

/// <summary>A file as the record keeps it.</summary>
public sealed class RecordedFile
{
    /// <summary>Its file name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Where it was downloaded from; null for a file that was not downloaded.</summary>
    public string? Url { get; set; }

    /// <summary>Its SHA-256, lowercase hex.</summary>
    public string? Sha256 { get; set; }

    /// <summary>A version, where the file has one apart from the release's (the loader).</summary>
    public string? Version { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstallRecord))]
internal sealed partial class InstallRecordJsonContext : JsonSerializerContext
{
}
