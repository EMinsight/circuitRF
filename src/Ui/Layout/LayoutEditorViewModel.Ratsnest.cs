// The ratsnest overlay (brief-dfr22-3-layout-round-trip.md R-dfr22-3-5; layout-view.md §9 step 4): thin straight
// lines between pins that share a net and that copper has not yet joined. An OVERLAY — never a shape, never saved,
// never exported — which is the lesson of R-L5h-3, where the first ratsnest was written as artwork.
//
// The pins and their copper groups are read once per edit (Model.Changed marks them stale) because reading them
// flattens the design and partitions its copper; a pointer move only offsets the dragged parts' pins.
//
// The read leaves the UI thread (designer report, round 25). Partitioning a production ten-layer board's copper
// is ~20 s in Release, and the overlay used to do it inside RebuildOverlay on every edit, so each part placed or
// moved on that board froze the window for the whole read. The lines already drawn stay up while it runs.
//
// …and they follow the parts while it runs (designer report, round 26). The last answer's pins were drawn where
// they were READ, so for the whole read a moved part's rats stayed where it had been, a rotated part's ended on
// its unrotated pins, and a deleted part's were still drawn — on a board edited faster than it is read, for good.
// A pin is a point in its placement's frame, which is cheap to carry: each draw puts it through the placement's
// transform as it is NOW, drops the pins of a placement that is gone, and only the copper groups wait for the read.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Pdn;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>View ▸ Show Ratsnest. On after Update Layout from Schematic (<see cref="ShowRatsnestFrom"/>).</summary>
    [ObservableProperty] private bool _showRatsnest;

    partial void OnShowRatsnestChanged(bool value)
    {
        _ratsnestStale = true;
        RebuildOverlay();
    }

    /// <summary>The schematic's nets by placement, once read — from memory after Update Layout, else from the
    /// schematic beside this layout on disk the first time the ratsnest is drawn.</summary>
    private PdnSchematicNets? _ratsnestNets;

    private bool _ratsnestStale = true;

    /// <summary>One pin as read: its net, where it was and its copper group, the placement it belongs to (an index
    /// into <see cref="_ratsnestPlaced"/>), and which array cell of that placement.</summary>
    private readonly record struct RatsnestReadPin(RatsnestPin Pin, int Instance, int Row, int Col);

    /// <summary>Every pin with a net, its copper group, and the instance it belongs to.</summary>
    private List<RatsnestReadPin> _ratsnestPins = [];

    /// <summary>The placements those pins were read from, by the index a pin carries. While a read is out the model
    /// may have been edited under them, so a placement is matched by its object, never by an index that has moved.</summary>
    private LayoutInstance[] _ratsnestPlaced = [];

    /// <summary>Copies of those placements as they stood when read — what a pin's position was computed through.</summary>
    private LayoutInstance[] _ratsnestAsRead = [];

    /// <summary>How many times the pins were re-read — once per edit, never per pointer move.</summary>
    internal int RatsnestRebuildCount { get; private set; }

    /// <summary>
    /// How the ratsnest's copper read leaves the UI thread. <c>Task.Run</c> in the application, set by the
    /// canvas; inline by default, so a test reads the answer straight back — railRF's
    /// <c>ReadCopperOffThread</c> seam, for its reason.
    /// </summary>
    internal Func<Action, Task> ReadRatsnestOffThread { get; set; } =
        static work => { work(); return Task.CompletedTask; };

    /// <summary>The read in flight, or the last one. <b>Awaitable</b>, so a test that installs real threading
    /// can wait for the deferred answer rather than sleeping for it.</summary>
    internal Task? RatsnestRead { get; private set; }

    /// <summary>What one read needs, taken on the UI thread: the model is the editor's and not the worker's.</summary>
    private sealed record RatsnestJob(
        Technology Tech, string ClayPath, PdnSchematicNets Nets,
        IReadOnlyList<LayoutShape> Flat, LayoutView Placements, LayoutInstance[] Placed)
    {
        public LayoutInstance[] AsRead => [.. Placements.Instances];
    }

    /// <summary>Update Layout from Schematic: the nets the run just used, and the ratsnest switched on.</summary>
    internal void ShowRatsnestFrom(PdnSchematicNets nets)
    {
        _ratsnestNets = nets;
        _ratsnestStale = true;
        if (ShowRatsnest) RebuildOverlay();
        else ShowRatsnest = true;
    }

    /// <summary>The lines to draw now — empty when the ratsnest is off. Every pin is drawn where its placement puts
    /// it now, a part being dragged where it is being dragged to, and a placement that is gone draws nothing.</summary>
    internal IReadOnlyList<RatsnestLine> RatsnestLines()
    {
        if (!ShowRatsnest) return [];
        // One read at a time: an edit made while one runs is picked up when it finishes (PublishWhenRead).
        if (_ratsnestStale && RatsnestRead is not { IsCompleted: false }) StartRatsnestRead();
        if (_ratsnestPins.Count == 0) return [];

        bool dragging = _selectDragKind == SelectDragKind.Move && (_moveLiveDx != 0 || _moveLiveDy != 0)
                        && _selectedInstanceIndices.Count > 0 && !DuplicateDragArmed;
        var dragged = new HashSet<LayoutInstance>(ReferenceEqualityComparer.Instance);
        if (dragging)
            foreach (int i in _selectedInstanceIndices)
                if (i >= 0 && i < Model.Instances.Count) dragged.Add(Model.Instances[i]);

        // Where each placement read is now: null when it is gone, and whether it has moved since it was read.
        var now = PlacementsNow();
        var moved = new bool[now.Length];
        bool anyChange = false;
        for (int k = 0; k < now.Length; k++)
        {
            moved[k] = now[k] is { } cur && (dragged.Contains(cur) || !SameTransform(cur, _ratsnestAsRead[k]));
            anyChange |= now[k] is null || moved[k];
        }
        if (!anyChange) return Ratsnest.Build([.. _ratsnestPins.Select(p => p.Pin)]);

        // A moved pin keeps its copper group only when everything in that group moved with it: the copper it was
        // joined to otherwise stays behind, and the line has to show. Pins of one placement that shared a group
        // still share one — the placement's own artwork moved with them.
        bool Kept(int inst) => inst >= 0 && inst < now.Length && now[inst] is not null;
        var anchored = _ratsnestPins.Where(p => Kept(p.Instance) && !moved[p.Instance]).Select(p => p.Pin.Group).ToHashSet();
        var fresh = new Dictionary<(int Instance, int Group), int>();
        var pins = new List<RatsnestPin>(_ratsnestPins.Count);
        foreach (var (pin, inst, row, col) in _ratsnestPins)
        {
            if (!Kept(inst)) continue;
            if (!moved[inst]) { pins.Add(pin); continue; }

            var cur = now[inst]!;
            var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(pin.X, pin.Y, _ratsnestAsRead[inst], row, col);
            var (x, y) = LayoutInstanceTransform.TransformPoint((long)Math.Round(lx), (long)Math.Round(ly), cur, row, col);
            if (dragged.Contains(cur)) { x += _moveLiveDx; y += _moveLiveDy; }

            int group = pin.Group;
            if (anchored.Contains(group))
            {
                if (!fresh.TryGetValue((inst, group), out int g)) fresh[(inst, group)] = g = int.MinValue + fresh.Count;
                group = g;
            }
            pins.Add(pin with { X = x, Y = y, Group = group });
        }
        return Ratsnest.Build(pins);
    }

    /// <summary>
    /// The placement each read pin's instance is now, or null when it is gone. By object first — a move edits the
    /// placement in place. Then by designator, for an edit that REPLACES the placement with a copy (a rotation, a
    /// mirror, a properties change) and for Cut then Paste, which is the same part placed again: one current
    /// placement of that name and cell that no read placement holds by object. A placement flipped to the other
    /// side reads its pins from another view, so it waits for the read.
    /// </summary>
    private LayoutInstance?[] PlacementsNow()
    {
        var now = new LayoutInstance?[_ratsnestPlaced.Length];
        var live = new HashSet<LayoutInstance>(Model.Instances, ReferenceEqualityComparer.Instance);
        Dictionary<string, LayoutInstance?>? byName = null;
        for (int k = 0; k < now.Length; k++)
        {
            if (live.Contains(_ratsnestPlaced[k])) { now[k] = _ratsnestPlaced[k]; continue; }
            if (_ratsnestAsRead[k].DisplayRefDes is not { Length: > 0 } name) continue;
            byName ??= UnreadPlacementsByName();
            if (byName.TryGetValue(name, out var cur) && cur is not null
                && string.Equals(cur.CellRef, _ratsnestAsRead[k].CellRef, StringComparison.Ordinal)
                && cur.Side == _ratsnestAsRead[k].Side)
            {
                now[k] = cur;
                byName[name] = null;   // claimed once
            }
        }
        return now;

        Dictionary<string, LayoutInstance?> UnreadPlacementsByName()
        {
            var read = new HashSet<LayoutInstance>(_ratsnestPlaced, ReferenceEqualityComparer.Instance);
            var map = new Dictionary<string, LayoutInstance?>(StringComparer.Ordinal);
            foreach (var inst in Model.Instances)
                if (!read.Contains(inst) && inst.DisplayRefDes is { Length: > 0 } n)
                    map[n] = map.ContainsKey(n) ? null : inst;   // two of one name: neither is the part
            return map;
        }
    }

    private static bool SameTransform(LayoutInstance a, LayoutInstance b) =>
        a.X == b.X && a.Y == b.Y && a.RotationDegrees == b.RotationDegrees && a.MirrorX == b.MirrorX && a.Mag == b.Mag
        && a.Rows == b.Rows && a.Cols == b.Cols && a.PitchX == b.PitchX && a.PitchY == b.PitchY;

    private void StartRatsnestRead()
    {
        _ratsnestStale = false;
        RatsnestRebuildCount++;
        if (SnapshotRatsnestJob() is not { } job) { _ratsnestPins = []; _ratsnestPlaced = []; _ratsnestAsRead = []; return; }

        List<RatsnestReadPin>? read = null;
        var task = ReadRatsnestOffThread(() => read = ReadRatsnestPins(job));
        RatsnestRead = task;

        // Inline (the default seam, and every test): the caller is drawing right now and draws this answer.
        if (task.IsCompleted)
        {
            if (task.IsCompletedSuccessfully) (_ratsnestPins, _ratsnestPlaced, _ratsnestAsRead) = (read ?? [], job.Placed, job.AsRead);
            return;
        }
        _ = PublishWhenRead(task, () => read, job);
    }

    /// <summary>Back on the UI thread: draws the answer — unless the design was edited while it was read, in
    /// which case the answer describes a design that no longer exists and the redraw starts the next read.</summary>
    private async Task PublishWhenRead(Task task, Func<List<RatsnestReadPin>?> read, RatsnestJob job)
    {
        bool ok;
        try { await task; ok = true; }
        catch { ok = false; }   // a failed read draws nothing new; the next edit asks again

        if (ok && !_ratsnestStale) (_ratsnestPins, _ratsnestPlaced, _ratsnestAsRead) = (read() ?? [], job.Placed, job.AsRead);
        RebuildOverlay();
    }

    private RatsnestJob? SnapshotRatsnestJob()
    {
        if (Technology is not { } tech || Model.Instances.Count == 0 || InstanceBaseDir is not { Length: > 0 } layoutDir) return null;

        // PlacedPins and the schematic walk read the DIRECTORY of this path; the name is only what a note would say.
        string clayPath = Path.Combine(layoutDir, "layout.clay");
        _ratsnestNets ??= PdnSchematicNets.Resolve(clayPath);

        var flat = LayoutDesignFlatten.Flatten(Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        if (flat.ExceedsCeiling) return null;

        // The placements as they are now, in the same order, so a pin's instance index is still this model's.
        var placements = new LayoutView();
        placements.Shapes.AddRange(Model.Shapes);
        foreach (var inst in Model.Instances) placements.Instances.Add(LayoutGeometry.Clone(inst));
        return new RatsnestJob(tech, clayPath, _ratsnestNets, flat.Shapes, placements, [.. Model.Instances]);
    }

    private static List<RatsnestReadPin> ReadRatsnestPins(RatsnestJob job)
    {
        var copper = CopperPieces.Build(job.Flat, job.Tech, job.Placements.Shapes);

        var origins = new List<PlacedPinOrigin>();
        var nets = job.Nets;
        var placed = PlacedPins.Of(job.Placements, job.ClayPath, job.Tech, PinNaming.SchematicThenArtwork,
            portNamesOf: id => nets.For(id)?.PortNames ?? [],
            portNetsOf: id => nets.For(id)?.Nets ?? [],
            stamped: copper, origins: origins, scope: PlacementScope.EveryPlacement);

        var pins = new List<RatsnestReadPin>();
        int unjoined = -1;
        for (int i = 0; i < placed.Count && i < origins.Count; i++)
        {
            if (placed[i].Net is not { Length: > 0 } net) continue;
            int group = copper.PieceAt(placed[i].X, placed[i].Y, origins[i].Layer);
            pins.Add(new RatsnestReadPin(new RatsnestPin(net, placed[i].X, placed[i].Y, group >= 0 ? group : unjoined--),
                                         origins[i].Instance, origins[i].Row, origins[i].Col));
        }
        return pins;
    }
}
