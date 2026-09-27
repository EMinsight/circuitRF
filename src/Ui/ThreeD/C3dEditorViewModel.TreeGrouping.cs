// 3D editor bugs round 2 — the object tree lists its objects BY MATERIAL (the default) or BY PRIMITIVE TYPE, and a
// filter narrows the rows it lists.
//
// A GROUP IS FOUND BY ITS ROLE, NEVER BY ITS HEADER. By material, a group's header is a material's name, and a
// technology may well name one "Ports" or "Wires"; the code that finds the instances' group, the ports' group or the
// air box's home reads C3dTreeGroup.Role, so no material can be mistaken for one of them. The expansion key carries
// the role for the same reason.
//
// THE FILTER IS THE TREE'S, NOT THE VIEW'S. It hides rows only: an object filtered out is still drawn, pickable and
// selectable in the scene (its tree node is simply absent, so the tree shows no selection for it). What is excluded is
// stored as the set of UNCHECKED names, so a material or type that appears later is listed from the start. Ports and
// the air box are a setup's records, not objects, and are never filtered. The material filter applies to what has a
// material to speak of: an instance (a whole cell) and a polyline (construction geometry) pass it and are filtered by
// type alone.
//
// Both choices are editor state for this editor's lifetime: nothing is written to the document or the workspace.
//
// brief-em3d-66 R-em3d66-3 — A BOOLEAN IS A NODE, AND EVERY OBJECT APPEARS ONCE. Its Blank and then its Tools are its
// children, at any depth; by material the boolean is a solid of its BLANK's material and its operands are never listed
// again under their own; by type booleans have their own group, found by its role. The filter hides whole top-level rows
// only, so an operand is never filtered out of a boolean that is shown.

using System.Collections.ObjectModel;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>How the editor's tree groups its objects.</summary>
public enum C3dTreeGrouping { Material, Primitive }

/// <summary>What a group of the editor's tree holds — what code finds a group by (a header may be any material's name).</summary>
public enum C3dTreeGroupRole { Objects, Construction, Instances, Ports, AirBox, Booleans }

public sealed partial class C3dEditorViewModel
{
    /// <summary>The group objects with no material go in, by material.</summary>
    public const string NoMaterialHeader = "No material";

    /// <summary>The tree's grouping choices, as the header's combo lists them (<see cref="C3dTreeGrouping"/> order).</summary>
    public static IReadOnlyList<string> TreeGroupingChoices { get; } = ["By material", "By type"];

    [ObservableProperty] private C3dTreeGrouping _treeGrouping = C3dTreeGrouping.Material;

    partial void OnTreeGroupingChanged(C3dTreeGrouping value)
    {
        OnPropertyChanged(nameof(TreeGroupingText));
        RebuildTree();
    }

    /// <summary>The header combo's side of <see cref="TreeGrouping"/>.</summary>
    public string TreeGroupingText
    {
        get => TreeGroupingChoices[(int)TreeGrouping];
        set { int i = Array.IndexOf([.. TreeGroupingChoices], value); if (i >= 0) TreeGrouping = (C3dTreeGrouping)i; }
    }

    /// <summary>The filter lists a Materials section only when some object names one (or has none).</summary>
    public bool HasMaterialFilters => MaterialFilters.Count > 0;

    /// <summary>The filter's type rows (one per primitive type the document holds, and Instances).</summary>
    public ObservableCollection<C3dTreeFilterEntry> TypeFilters { get; } = [];

    /// <summary>The filter's material rows (one per material the document's objects name, and "No material").</summary>
    public ObservableCollection<C3dTreeFilterEntry> MaterialFilters { get; } = [];

    private readonly HashSet<string> _hiddenTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hiddenMaterials = new(StringComparer.Ordinal);

    /// <summary>True when the filter hides something the document holds: the header's icon says so.</summary>
    public bool IsTreeFilterActive => TypeFilters.Any(f => !f.IsChecked) || MaterialFilters.Any(f => !f.IsChecked);

    /// <summary>Lists every row again.</summary>
    [RelayCommand]
    private void ShowAllTreeRows()
    {
        _hiddenTypes.Clear();
        _hiddenMaterials.Clear();
        RebuildTree();
    }

    internal void TreeFilterChanged(C3dTreeFilterEntry entry)
    {
        var set = entry.IsMaterial ? _hiddenMaterials : _hiddenTypes;
        if (entry.IsChecked) set.Remove(entry.Name); else set.Add(entry.Name);
        RebuildTree();
    }

    /// <summary>The primitive-type group an object belongs to (its header by type, and its type-filter name).</summary>
    private static string TypeHeaderOf(C3dObject o)
    {
        foreach (var (type, header) in Groups) if (o.GetType() == type) return header;
        return C3dObject.KindOf(o);
    }

    /// <summary>The material group an object belongs to (its header by material, and its material-filter name).</summary>
    private static string MaterialHeaderOf(C3dObject o) => C3dValidation.EffectiveMaterial(o) is { Length: > 0 } m ? m : NoMaterialHeader;

    private bool PassesTreeFilter(C3dObject o)
        => !_hiddenTypes.Contains(TypeHeaderOf(o)) && (o is C3dPolyline || !_hiddenMaterials.Contains(MaterialHeaderOf(o)));

    /// <summary>The object groups, by the current grouping, filtered; construction order within each group.</summary>
    private IEnumerable<C3dTreeGroup> ObjectGroups()
    {
        var rows = Document.Objects.Select((o, i) => (o, i)).Where(t => PassesTreeFilter(t.o)).ToList();
        // Round 3: by material the group's header already names each row's material, so the row does not repeat it.
        bool byMaterial = TreeGrouping == C3dTreeGrouping.Material;
        C3dTreeItem Item((C3dObject o, int i) t)
        {
            var item = new C3dTreeItem(this, t.o.Name, C3dObject.KindOf(t.o), byMaterial ? null : C3dValidation.EffectiveMaterial(t.o), t.i, -1, !t.o.Hidden)
            {
                Icon = IconOf(t.o), IconOpacity = t.o is C3dOperation { Enabled: false } ? 0.4 : 1,
            };
            AddOperands(item, t.o, t.i, "", t.o.Name);
            return item;
        }

        if (TreeGrouping == C3dTreeGrouping.Primitive)
        {
            foreach (var (type, header) in Groups)
            {
                var items = rows.Where(t => t.o.GetType() == type).Select(Item).ToList();
                if (items.Count > 0) yield return new C3dTreeGroup(header, items, type == typeof(C3dBoolean) ? C3dTreeGroupRole.Booleans : C3dTreeGroupRole.Objects);
            }
            yield break;
        }

        // By material: what has none first (a solver ignores it, so it is what wants attention), then the materials in
        // name order, then construction geometry, which never has one.
        var solids = rows.Where(t => t.o is not C3dPolyline).ToList();
        var bare = solids.Where(t => MaterialHeaderOf(t.o) == NoMaterialHeader).Select(Item).ToList();
        if (bare.Count > 0) yield return new C3dTreeGroup(NoMaterialHeader, bare);
        foreach (var g in solids.Where(t => MaterialHeaderOf(t.o) != NoMaterialHeader)
                                .GroupBy(t => MaterialHeaderOf(t.o), StringComparer.Ordinal)
                                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            yield return new C3dTreeGroup(g.Key, g.Select(Item));
        var lines = rows.Where(t => t.o is C3dPolyline).Select(Item).ToList();
        if (lines.Count > 0) yield return new C3dTreeGroup("Polylines", lines, C3dTreeGroupRole.Construction);
    }

    /// <summary>brief-em3d-66 R-em3d66-3a — a boolean's operands beneath its row: the Blank first, then the Tools in order, each
    /// labelled in the detail, at any depth. Each row addresses its operand by path under the top-level object.</summary>
    private void AddOperands(C3dTreeItem parent, C3dObject o, int top, string path, string topName)
    {
        if (o is not C3dBoolean) return;
        foreach (var (prefix, operand) in C3dOperands.Of(o))
        {
            bool blank = prefix == "Blank.";
            string? material = C3dValidation.EffectiveMaterial(operand);
            var child = new C3dTreeItem(this, blank ? C3dObject.KindOf(operand) : operand.Name, C3dObject.KindOf(operand),
                                        (blank ? "Blank" : "Tool") + (material is { Length: > 0 } ? " · " + material : ""), top, -1, !operand.Hidden)
            {
                OperandPath = path + prefix, TopName = topName, Icon = IconOf(operand),
                IconOpacity = operand is C3dOperation { Enabled: false } ? 0.4 : 1,
            };
            AddOperands(child, operand, top, path + prefix, topName);
            parent.Children.Add(child);
        }
    }

    /// <summary>An operation's icon — the toolbar's set — or null for anything else.</summary>
    private static Material.Icons.MaterialIconKind? IconOf(C3dObject o) => o switch
    {
        C3dBoolean { Op: C3dBooleanOp.Subtract } => Material.Icons.MaterialIconKind.VectorDifferenceBa,
        C3dBoolean { Op: C3dBooleanOp.Unite } => Material.Icons.MaterialIconKind.VectorUnion,
        C3dBoolean => Material.Icons.MaterialIconKind.VectorIntersection,
        _ => null,
    };

    /// <summary>Instances, unless the type filter hides them.</summary>
    private C3dTreeGroup? InstanceGroup()
    {
        if (_hiddenTypes.Contains(InstancesHeader)) return null;
        var instances = Document.Instances.Select((inst, i) => new C3dTreeItem(this, inst.Name, "Instance", inst.CellRef, -1, i, true)).ToList();
        return instances.Count > 0 ? new C3dTreeGroup(InstancesHeader, instances, C3dTreeGroupRole.Instances) : null;
    }

    private const string InstancesHeader = "Instances";

    /// <summary>The filter's rows, from what the document holds now, each checked unless the user unchecked it.</summary>
    private void RebuildTreeFilters()
    {
        var types = Groups.Select(g => g.Header).Where(h => Document.Objects.Any(o => TypeHeaderOf(o) == h)).ToList();
        if (Document.Instances.Count > 0) types.Add(InstancesHeader);
        var materials = Document.Objects.Where(o => o is not C3dPolyline).Select(MaterialHeaderOf)
                                        .Distinct(StringComparer.Ordinal)
                                        .OrderBy(m => m == NoMaterialHeader ? 0 : 1).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        Refill(TypeFilters, types, _hiddenTypes, material: false);
        Refill(MaterialFilters, materials, _hiddenMaterials, material: true);
        OnPropertyChanged(nameof(IsTreeFilterActive));
        OnPropertyChanged(nameof(HasMaterialFilters));

        void Refill(ObservableCollection<C3dTreeFilterEntry> into, List<string> names, HashSet<string> hidden, bool material)
        {
            if (into.Select(e => e.Name).SequenceEqual(names) && into.All(e => e.IsChecked == !hidden.Contains(e.Name))) return;
            into.Clear();
            foreach (string n in names) into.Add(new C3dTreeFilterEntry(this, n, material, !hidden.Contains(n)));
        }
    }
}

/// <summary>One checkable row of the tree's filter: a primitive type or a material.</summary>
public sealed partial class C3dTreeFilterEntry(C3dEditorViewModel owner, string name, bool isMaterial, bool isChecked) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsMaterial { get; } = isMaterial;

    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.TreeFilterChanged(this);
}
