using System.IO;
using System.Text.Json;

namespace HandPilot;

/// <summary>Persists independent reach profiles for each camera and desktop arrangement.</summary>
public sealed class CalibrationProfileStore(string path)
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool TryLoad(string cameraId, string displayKey, out CameraBounds bounds)
    {
        bounds = CameraBounds.Default;
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(displayKey)) return false;
        var document = ReadDocument();
        var profile = document?.Profiles?.LastOrDefault(profile =>
            profile is not null && profile.CameraId == cameraId && profile.DisplayKey == displayKey &&
            profile.Bounds.IsValid);
        if (profile is null) return false;
        bounds = profile.Bounds;
        return true;
    }

    public void Save(string cameraId, string displayKey, CameraBounds bounds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayKey);
        if (!bounds.IsValid) throw new ArgumentException("Calibration bounds must be finite, ordered, and inside the camera image.", nameof(bounds));
        var document = ReadDocument() ?? new ProfileDocument { Version = FormatVersion };
        document.Profiles ??= [];
        document.Profiles.RemoveAll(profile => profile is null ||
            (profile.CameraId == cameraId && profile.DisplayKey == displayKey));
        document.Profiles.Add(new CalibrationProfile { CameraId = cameraId, DisplayKey = displayKey, Bounds = bounds });
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private ProfileDocument? ReadDocument()
    {
        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(path));
            return document?.Version == FormatVersion ? document : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A missing or invalid profile is an uncalibrated setup, never an app startup failure.
            return null;
        }
    }

    private sealed class ProfileDocument
    {
        public int Version { get; set; }
        public List<CalibrationProfile?>? Profiles { get; set; } = [];
    }

    private sealed class CalibrationProfile
    {
        public string? CameraId { get; set; }
        public string? DisplayKey { get; set; }
        public CameraBounds Bounds { get; set; }
    }
}
