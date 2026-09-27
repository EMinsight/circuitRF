using System;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// 3D editor round 3 — a cell's VIEW FILE (<c>.c3d</c> / <c>.clay</c>) dragged out of the Project Tree, so it can be
/// dropped into a 3D view as an instance of exactly that view.
///
/// <para><b>Why a payload of its own rather than <see cref="WorkspaceFileDragPayload"/>.</b> The tree's own drop
/// handler reads that one as a loose file and MOVES it (TM1) or copies it into another workspace (R-mw3-11) — and a
/// view file lifted out of its cell's sub-folder is a broken cell. No tree reads this prefix, so it can be dragged
/// anywhere and only a target that understands a view acts on it. Same mechanism as <see cref="CellDragPayload"/>:
/// a prefixed text string, so it travels on the native pasteboard.</para>
/// </summary>
public sealed record CellViewDragPayload(string ViewFileAbsPath)
{
    private const string Prefix = "circuitrf-cellview:";

    /// <summary>Compact wire representation: <c>circuitrf-cellview:&lt;absolute-view-file-path&gt;</c>.</summary>
    public string Serialize() => $"{Prefix}{ViewFileAbsPath}";

    /// <summary>Parses a string produced by <see cref="Serialize"/>; false for anything else — the foreign-text guard
    /// every one of these payloads carries.</summary>
    public static bool TryParse(string? s, out CellViewDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var path = s[Prefix.Length..];
        if (string.IsNullOrEmpty(path)) return false;
        result = new CellViewDragPayload(path);
        return true;
    }
}
