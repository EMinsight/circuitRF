using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// One row of the Archive Workspace dialog's tree: a heading, or one file/kit the user can tick.
///
/// <para><b>Children are built on first expand, not up front.</b> A workspace can hold hundreds of
/// results files and a kit can hold tens of thousands, and a dialog that is only ever opened to tick
/// two boxes must not pay for rows nobody looks at. A collapsed heading therefore holds one
/// placeholder row, which is what makes the expander arrow appear at all, and swaps it for the real
/// rows the first time it opens.</para>
///
/// <para>Framework-free apart from the MVVM toolkit — no Avalonia — so the tri-state roll-up below
/// can be tested without a window, which is where this kind of logic actually goes wrong.</para>
/// </summary>
public sealed partial class ArchiveTreeNode : ObservableObject
{
    private static readonly ArchiveTreeNode Placeholder = new() { Title = "…" };

    private Func<IEnumerable<ArchiveTreeNode>>? _childFactory;
    private bool _populated;
    private bool _updatingFromParent;

    public string Title { get; init; } = "";

    /// <summary>Full path, shown as the row's tooltip. Empty for a heading.</summary>
    public string Detail { get; init; } = "";

    /// <summary>A second line under the title, or empty (<see cref="ArchiveOption.Note"/>).</summary>
    public string Note { get; init; } = "";

    public bool HasNote => Note.Length > 0;

    /// <summary>The option this row ticks, or null for a heading.</summary>
    public ArchiveOption? Option { get; init; }

    public ArchiveTreeNode? Parent { get; private set; }

    public ObservableCollection<ArchiveTreeNode> Children { get; } = [];

    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Null = some children ticked and some not. Bound to a three-state CheckBox.</summary>
    [ObservableProperty] private bool? _isChecked;

    /// <summary>A heading whose children are not built yet still needs the expander arrow.</summary>
    public static ArchiveTreeNode Group(string title, Func<IEnumerable<ArchiveTreeNode>> children)
    {
        var node = new ArchiveTreeNode { Title = title, _childFactory = children };
        node.Children.Add(Placeholder);
        return node;
    }

    public static ArchiveTreeNode Leaf(ArchiveOption option) => new()
    {
        Title    = option.DisplayName,
        Detail   = option.Detail.Length > 0 ? option.Detail : option.SourcePath,
        Note     = option.Note,
        Option   = option,
        SizeText = WorkspaceArchivePlan.FormatSize(option.SizeBytes),
        IsChecked = option.Selected,
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _populated || _childFactory is null) return;

        _populated = true;
        Children.Clear();
        foreach (var child in _childFactory())
        {
            child.Parent = this;
            Children.Add(child);
        }

        // A heading the user ticked before opening it has to push that choice down to the rows it
        // was standing in for — otherwise expanding a ticked group shows unticked children.
        if (IsChecked is { } state) ApplyToChildren(state);
        else RefreshFromChildren();
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (Option is not null && value is { } v) Option.Selected = v;

        if (!_updatingFromParent && value is { } state) ApplyToChildren(state);

        Parent?.RefreshFromChildren();
    }

    private void ApplyToChildren(bool state)
    {
        // A group standing in for rows it has not built yet still has to decide for them, so the
        // choice is remembered on the group and applied by OnIsExpandedChanged above; the unbuilt
        // options are updated here directly. HERE and not only in OnIsCheckedChanged: a parent pushes
        // its state down through this method, and a collapsed sub-group reached that way (Results ▸
        // Other, unticked from Results) once kept every file it stood for, archiving 1.3 GB of field
        // files with every box on screen empty.
        if (!_populated && _childFactory is not null) SelectUnbuilt(_childFactory, state);

        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Placeholder)) continue;
            child._updatingFromParent = true;
            try { child.IsChecked = state; }
            finally { child._updatingFromParent = false; }
            child.ApplyToChildren(state);
        }
    }

    /// <summary>Every option under a group's unbuilt rows, through any nested group's unbuilt rows too.</summary>
    private static void SelectUnbuilt(Func<IEnumerable<ArchiveTreeNode>> factory, bool state)
    {
        foreach (var node in factory())
        {
            if (node.Option is not null) node.Option.Selected = state;
            if (node._childFactory is not null) SelectUnbuilt(node._childFactory, state);
        }
    }

    /// <summary>Rolls the children's state up: all on, all off, or the indeterminate middle.</summary>
    private void RefreshFromChildren()
    {
        var real = Children.Where(c => !ReferenceEquals(c, Placeholder)).ToList();
        if (real.Count == 0) return;

        bool? state = real.All(c => c.IsChecked == true) ? true
                    : real.All(c => c.IsChecked == false) ? false
                    : null;

        _updatingFromParent = true;
        try { IsChecked = state; }
        finally { _updatingFromParent = false; }

        Parent?.RefreshFromChildren();
    }

    /// <summary>Every node under this one, this one included — for tests and for size roll-ups.</summary>
    public IEnumerable<ArchiveTreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Placeholder)) continue;
            foreach (var d in child.SelfAndDescendants()) yield return d;
        }
    }
}
