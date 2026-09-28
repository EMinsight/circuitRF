// .c3d groups — objects and instances gathered under a name, and groups inside groups.
//
// A MEMBER NAMES ITS GROUP; THERE IS NO LIST OF GROUPS. Each object and instance carries the path of the group it is in,
// outermost first (`pa/match`), and a group is exactly the set of members whose path runs through it. So a group can
// neither be empty nor dangle, and every edit that already carries an object — a rename, a delete, its undo, a copy —
// carries its membership too, with nothing to keep in step. Grouping, ungrouping and renaming a group rewrite the
// members' paths: ordinary object and instance edits, one undo entry like any other.
//
// A GROUP'S NAME IS UNIQUE AMONG GROUPS, wherever it sits, so "Ungroup match" names one group. It is not an object's
// namespace: nothing attaches to a group by name the way a port attaches to an object.
//
// ORGANISATION ONLY. The elaborator never reads a path, construction order is the object list's as before, and a group
// has no placement of its own: moving a group moves its members. An operation (Boolean, Fillet, Chamfer) carries the path
// for its result, as it carries the name; the object it wraps has none.

using CircuitRF.Design.Cells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

/// <summary>One group of a document: its name, its path from the outermost group, and its parent's path (null at the top).</summary>
public sealed record C3dGroupInfo(string Name, string Path, string? Parent);

/// <summary>A member of a group: an object (by index into <see cref="C3dDocument.Objects"/>) or an instance.</summary>
public readonly record struct C3dMemberRef(bool Instance, int Index);

/// <summary>What a selection is made of, for grouping: a group taken whole (<see cref="GroupPath"/>), or one member.</summary>
public readonly record struct C3dGroupUnit(C3dMemberRef Member, string? GroupPath)
{
    public bool IsGroup => GroupPath is not null;
}

public static class C3dGroups
{
    public const char Separator = '/';

    /// <summary>The name a new group takes when none is given, before its number.</summary>
    public const string DefaultStem = "Group";

    // ── paths ─────────────────────────────────────────────────────────────────────────────────────

    public static string[] Segments(string? path) => string.IsNullOrEmpty(path) ? [] : path.Split(Separator);

    /// <summary>The path of <paramref name="segments"/>, or null for none.</summary>
    public static string? Join(IEnumerable<string> segments)
    {
        string s = string.Join(Separator, segments);
        return s.Length == 0 ? null : s;
    }

    public static string? ParentOf(string path) => path.LastIndexOf(Separator) is >= 0 and var i ? path[..i] : null;

    public static string NameOf(string path) => path[(path.LastIndexOf(Separator) + 1)..];

    /// <summary>Whether a member at <paramref name="memberPath"/> is inside the group at <paramref name="groupPath"/>, at any depth.</summary>
    public static bool IsIn(string? memberPath, string groupPath)
        => memberPath is not null && (memberPath == groupPath || memberPath.StartsWith(groupPath + Separator, StringComparison.Ordinal));

    /// <summary>The top-most group of a path, or null.</summary>
    public static string? TopOf(string? path) => Segments(path) is [var top, ..] ? top : null;

    // ── the members ───────────────────────────────────────────────────────────────────────────────

    public static string? PathOf(C3dDocument doc, C3dMemberRef m)
        => m.Instance ? doc.Instances[m.Index].Group : doc.Objects[m.Index].Group;

    /// <summary>Every object, in construction order, then every instance.</summary>
    public static IEnumerable<C3dMemberRef> Members(C3dDocument doc)
        => Enumerable.Range(0, doc.Objects.Count).Select(i => new C3dMemberRef(false, i))
                     .Concat(Enumerable.Range(0, doc.Instances.Count).Select(i => new C3dMemberRef(true, i)));

    /// <summary>Everything inside the group at <paramref name="groupPath"/>, at any depth.</summary>
    public static IReadOnlyList<C3dMemberRef> MembersOf(C3dDocument doc, string groupPath)
        => [.. Members(doc).Where(m => IsIn(PathOf(doc, m), groupPath))];

    /// <summary>Every group, each parent before its children, in the order its first member appears.</summary>
    public static IReadOnlyList<C3dGroupInfo> All(C3dDocument doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<C3dGroupInfo>();
        foreach (var m in Members(doc))
        {
            var segs = Segments(PathOf(doc, m));
            for (int k = 1; k <= segs.Length; k++)
            {
                string path = string.Join(Separator, segs[..k]);
                if (seen.Add(path)) list.Add(new C3dGroupInfo(segs[k - 1], path, k > 1 ? string.Join(Separator, segs[..(k - 1)]) : null));
            }
        }
        return list;
    }

    /// <summary>The group called <paramref name="name"/>, or null.</summary>
    public static C3dGroupInfo? Find(C3dDocument doc, string name) => All(doc).FirstOrDefault(g => g.Name == name);

    /// <summary>Every group's name.</summary>
    public static HashSet<string> Names(C3dDocument doc) => [.. All(doc).Select(g => g.Name)];

    /// <summary>The next free <c>Group&lt;n&gt;</c>.</summary>
    public static string NextName(C3dDocument doc)
    {
        var used = Names(doc);
        for (int n = 1; ; n++)
            if (!used.Contains(DefaultStem + n)) return DefaultStem + n;
    }

    /// <summary>Why <paramref name="name"/> cannot name a group (renaming the group called <paramref name="except"/>), or null.</summary>
    public static string? NameRefusal(C3dDocument doc, string name, string? except = null)
    {
        if (NameValidator.Validate(name) is { } why) return why;
        if (name != except && Names(doc).Contains(name)) return $"'{name}' is already the name of a group in this 3D view.";
        return null;
    }

    // ── a selection, as a group sees it ───────────────────────────────────────────────────────────

    /// <summary>
    /// What <paramref name="selected"/> is made of: for each member, the top-most group it is in whose every member is
    /// selected — or, when there is none, the member itself. In selection order, each once. A group clicked in the view
    /// selects all of it, so it comes back as one unit; a member picked out of a group in the tree comes back alone.
    /// </summary>
    public static IReadOnlyList<C3dGroupUnit> Units(C3dDocument doc, IEnumerable<C3dMemberRef> selected)
    {
        var order = selected.Distinct().ToList();
        var set = order.ToHashSet();
        var whole = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Whole(string g)
        {
            if (!whole.TryGetValue(g, out bool w)) whole[g] = w = MembersOf(doc, g).All(set.Contains);
            return w;
        }
        var units = new List<C3dGroupUnit>();
        foreach (var m in order)
        {
            var segs = Segments(PathOf(doc, m));
            C3dGroupUnit unit = new(m, null);
            for (int k = 1; k <= segs.Length; k++)
            {
                string g = string.Join(Separator, segs[..k]);
                if (Whole(g)) { unit = new C3dGroupUnit(m, g); break; }
            }
            if (unit.IsGroup ? !units.Any(u => u.GroupPath == unit.GroupPath) : !units.Contains(unit)) units.Add(unit);
        }
        return units;
    }

    // ── the rewrites: each returns every member it moves, with its new path ───────────────────────

    /// <summary>
    /// Group <paramref name="units"/> as a new group <paramref name="name"/>: it sits where they have in common — the deepest
    /// group all of them are in — and each unit goes inside it, a whole group keeping its own groups beneath it.
    /// </summary>
    public static IReadOnlyList<(C3dMemberRef Member, string? Path)> Group(C3dDocument doc, IReadOnlyList<C3dGroupUnit> units, string name)
    {
        string?[] parents = [.. units.Select(u => u.IsGroup ? ParentOf(u.GroupPath!) : PathOf(doc, u.Member))];
        var common = parents.Length == 0 ? [] : Segments(parents[0]).ToList();
        foreach (string? p in parents.Skip(1))
        {
            var s = Segments(p);
            int k = 0;
            while (k < common.Count && k < s.Length && common[k] == s[k]) k++;
            common.RemoveRange(k, common.Count - k);
        }
        string at = Join([.. common, name])!;
        var moves = new List<(C3dMemberRef, string?)>();
        foreach (var u in units)
        {
            if (u.GroupPath is { } g)
                foreach (var m in MembersOf(doc, g)) moves.Add((m, at + Separator + NameOf(g) + PathOf(doc, m)![g.Length..]));
            else moves.Add((u.Member, at));
        }
        return moves;
    }

    /// <summary>Ungroup the group at <paramref name="groupPath"/>: its members, and the groups inside it, move up to where it was.</summary>
    public static IReadOnlyList<(C3dMemberRef Member, string? Path)> Ungroup(C3dDocument doc, string groupPath)
    {
        string? parent = ParentOf(groupPath);
        return [.. MembersOf(doc, groupPath).Select(m =>
        {
            string rest = PathOf(doc, m)![groupPath.Length..].TrimStart(Separator);
            return (m, Join(Segments(parent).Concat(Segments(rest))));
        })];
    }

    /// <summary>Rename the group at <paramref name="groupPath"/> to <paramref name="newName"/>.</summary>
    public static IReadOnlyList<(C3dMemberRef Member, string? Path)> Rename(C3dDocument doc, string groupPath, string newName)
    {
        string renamed = ParentOf(groupPath) is { } p ? p + Separator + newName : newName;
        return [.. MembersOf(doc, groupPath).Select(m => (m, (string?)(renamed + PathOf(doc, m)![groupPath.Length..])))];
    }

    /// <summary>
    /// A copy's path, for one set of copies of <paramref name="units"/> (a Duplicate, or one element of an Array): a group
    /// copied whole is a NEW group beside it, and so is each group inside it, every one under a free name taken from
    /// <paramref name="used"/>; a member copied without the rest of its group stays in that group.
    /// </summary>
    public static Func<string?, string?> CopyPaths(C3dDocument doc, IReadOnlyList<C3dGroupUnit> units, ISet<string> used)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = All(doc);
        foreach (var top in units.Where(u => u.IsGroup).Select(u => u.GroupPath!).Distinct())
            foreach (var g in all.Where(g => IsIn(g.Path, top)))                 // parents first
            {
                string name = C3dOperations.NextFreeName(g.Name, used);
                map[g.Path] = g.Path == top ? (g.Parent is { } p ? p + Separator + name : name) : map[g.Parent!] + Separator + name;
            }
        return path =>
        {
            if (path is null || map.Count == 0) return path;
            string? best = null;
            foreach (var q in map.Keys) if (IsIn(path, q) && (best is null || q.Length > best.Length)) best = q;
            return best is null ? path : map[best] + path[best.Length..];
        };
    }

    // ── findings (C3dValidation) ──────────────────────────────────────────────────────────────────

    /// <summary>Every group name that is not a usable name, and every name that sits in more than one place.</summary>
    public static IEnumerable<Diagnostic> Findings(C3dDocument doc)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Members(doc).Select(m => PathOf(doc, m)).OfType<string>().Distinct(StringComparer.Ordinal))
            foreach (string seg in path.Split(Separator))
                if (NameValidator.Validate(seg) is { } why && bad.Add(seg)) yield return C3dDiagnostics.InvalidName("group", seg, why);
        foreach (var g in All(doc).GroupBy(g => g.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return C3dDiagnostics.GroupInTwoPlaces(g.Key, string.Join(", ", g.Select(x => x.Path)));
    }
}
