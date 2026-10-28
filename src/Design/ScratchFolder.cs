using System.Text.Json;

namespace CircuitRF.Design;

/// <summary>
/// Where circuitRF puts bytes that exist only while something runs — Gmsh's staging folder (a mesh can
/// be gigabytes) and the workspace an unsaved 3D view runs in. One setting for every such consumer, so
/// a machine with a small startup disk can send all of it to another drive (Settings ▸ Solvers ▸
/// Scratch folder).
///
/// <para><b>Unset, it is the system temp folder</b> — what every one of these consumers used before
/// there was a setting — so a headless process that sets nothing behaves exactly as it did, and still
/// follows <c>TMPDIR</c> / <c>TMP</c>.</para>
///
/// <para><b>Why process-wide and not an argument</b>, against this project's rule that a preference
/// is one: the consumers sit at the bottom of call chains that carry no run options at all (the DC row's
/// mesh, a thermal run's mesh), and the value is not a property of any run — it says which disk the
/// machine's temporary bytes go to. The GUI sets it from its preference at start-up and when the
/// setting changes; nothing here reads a preference file.</para>
///
/// <para><b>The command line reads the same setting</b> (<see cref="FromPreferences"/>), from the per-user preferences
/// file the Settings dialog writes, as RC-3's identity reader does — so <c>circuitrf em</c>, and an agent driving it
/// through the MCP server, stage where the application stages, and the run says where (<c>em</c>'s <c>Scratch:</c> line
/// and its JSON <c>scratch</c> block).</para>
///
/// <para><b>A chosen folder that is not there is a refusal, never a fallback.</b> The usual reason is
/// an external drive that is not connected, and quietly writing to the startup disk instead is the one
/// thing the setting exists to prevent.</para>
/// </summary>
public static class ScratchFolder
{
    private static volatile string? _chosen;

    /// <summary>The preferences key the Settings dialog writes the folder under. Shared with <c>AppPreferences</c>.</summary>
    public const string PreferenceKey = "scratch_folder";

    /// <summary>
    /// The folder Settings ▸ Solvers names, or null when it names none. <b>Never throws</b>: a missing, truncated or
    /// wrong-shaped preferences file is an absent one, so a headless run on a machine nobody configured uses the
    /// system temp folder.
    /// </summary>
    /// <param name="path">The preferences file to read; null for this user's (<see cref="UserStateDirectory.PreferencesPath"/>).</param>
    public static string? FromPreferences(string? path = null)
    {
        path ??= UserStateDirectory.PreferencesPath;
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(PreferenceKey, out var v)
                && v.ValueKind == JsonValueKind.String
                && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s)
                ? s.Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The folder the host chose, or null for the system temp folder. Blank clears it.</summary>
    public static string? Chosen
    {
        get => _chosen;
        set => _chosen = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The folder scratch goes in now.</summary>
    public static string Root => _chosen ?? Path.GetTempPath();

    /// <summary>Null when <see cref="Root"/> can be used; otherwise the sentence saying why not.</summary>
    public static string? Refusal() => RefusalFor(_chosen);

    /// <summary><see cref="Refusal"/> for <paramref name="chosen"/> rather than the folder in use — what the settings
    /// row and a test ask without changing the process's folder.</summary>
    public static string? RefusalFor(string? chosen)
        => !string.IsNullOrWhiteSpace(chosen) && !Directory.Exists(chosen.Trim())
            ? $"The scratch folder {chosen.Trim()} (Settings ▸ Solvers) is not there. Connect its drive, or choose another folder."
            : null;

    /// <summary>A new, uniquely named path under <see cref="Root"/> starting with <paramref name="prefix"/>.
    /// Creates nothing.</summary>
    public static string NewPath(string prefix)
        => Path.Combine(Root, prefix + Guid.NewGuid().ToString("N")[..12]);
}
