using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The per-user vendor-library list (brief vendorlib-1 R-vl1-4c, overview D3) — what this user has
/// added, so New Workspace can reference it from the start and a missing library can be repaired from
/// where the user last had it. The WORKSPACE is the truth; this is only the convenience, which is why
/// it is never read by anything in <c>src/Design</c> — a preference reaches there as an argument.
/// </summary>
public static class UserVendorLibraries
{
    public static IReadOnlyList<UserVendorLibrary> All()
        => AppPreferencesIo.Load().VendorLibraries ?? [];

    /// <summary>Adds or updates <paramref name="name"/>, keeping its Use-in-new-workspaces choice.</summary>
    public static void Remember(string name, string path)
        => AppPreferencesIo.Update(p =>
        {
            var list = p.VendorLibraries ?? [];
            var hit  = list.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null) list.Add(new UserVendorLibrary { Name = name, Path = path });
            else             { hit.Name = name; hit.Path = path; }
            p.VendorLibraries = list;
        });

    public static void SetUseInNewWorkspaces(string name, bool use)
        => AppPreferencesIo.Update(p =>
        {
            if (p.VendorLibraries?.FirstOrDefault(
                    l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } hit)
                hit.UseInNewWorkspaces = use;
        });

    /// <summary>What File ▸ New Workspace references: every ticked entry.</summary>
    public static IReadOnlyList<(string Name, string Path)> ForNewWorkspace()
        => [.. All().Where(l => l.UseInNewWorkspaces && l.Name.Length > 0 && l.Path.Length > 0)
                    .Select(l => (l.Name, l.Path))];

    /// <summary>Where this user last had a library of that name, when that folder still exists.</summary>
    public static string? ExistingPathFor(string name)
        => All().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)
                                  && Directory.Exists(l.Path))?.Path;

    public static bool? UseInNewWorkspaces(string name)
        => All().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))?.UseInNewWorkspaces;
}
