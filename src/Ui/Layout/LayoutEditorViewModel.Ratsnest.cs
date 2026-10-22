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

    /// <summary>Every pin with a net, its copper group, and the instance it belongs to.</summary>
    private List<(RatsnestPin Pin, int Instance)> _ratsnestPins = [];

    /// <summary>The placements those pins were read from, by the index a pin carries. While a read is out the model
    /// may have been edited under them, so a dragged part is matched by its object, never by an index that has moved.</summary>
    private LayoutInstance[] _ratsnestPlaced = [];

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
        IReadOnlyList<LayoutShape> Flat, LayoutView Placements, LayoutInstance[] Placed);

    /// <summary>Update Layout from Schematic: the nets the run just used, and the ratsnest switched on.</summary>
    internal void ShowRatsnestFrom(PdnSchematicNets nets)
    {
        _ratsnestNets = nets;
        _ratsnestStale = true;
        if (ShowRatsnest) RebuildOverlay();
        else ShowRatsnest = true;
    }

    /// <summary>The lines to draw now — empty when the ratsnest is off. A part being dragged has its pins drawn
    /// where it is being dragged to.</summary>
    internal IReadOnlyList<RatsnestLine> RatsnestLines()
    {
        if (!ShowRatsnest) return [];
        // One read at a time: an edit made while one runs is picked up when it finishes (PublishWhenRead).
        if (_ratsnestStale && RatsnestRead is not { IsCompleted: false }) StartRatsnestRead();
        if (_ratsnestPins.Count == 0) return [];

        bool dragging = _selectDragKind == SelectDragKind.Move && (_moveLiveDx != 0 || _moveLiveDy != 0)
                        && _selectedInstanceIndices.Count > 0 && !DuplicateDragArmed;
        if (!dragging) return Ratsnest.Build([.. _ratsnestPins.Select(p => p.Pin)]);

        // A dragged pin keeps its copper group only when everything in that group is dragged with it: the copper it
        // was joined to otherwise stays behind, and the line has to show.
        var moving = new HashSet<LayoutInstance>(ReferenceEqualityComparer.Instance);
        foreach (int i in _selectedInstanceIndices)
            if (i >= 0 && i < Model.Instances.Count) moving.Add(Model.Instances[i]);
        bool Moves(int inst) => inst >= 0 && inst < _ratsnestPlaced.Length && moving.Contains(_ratsnestPlaced[inst]);

        var anchored = _ratsnestPins.Where(p => !Moves(p.Instance)).Select(p => p.Pin.Group).ToHashSet();
        int fresh = int.MinValue;
        var pins = new List<RatsnestPin>(_ratsnestPins.Count);
        foreach (var (pin, inst) in _ratsnestPins)
        {
            if (!Moves(inst)) { pins.Add(pin); continue; }
            pins.Add(pin with
            {
                X = pin.X + _moveLiveDx, Y = pin.Y + _moveLiveDy,
                Group = anchored.Contains(pin.Group) ? fresh++ : pin.Group,
            });
        }
        return Ratsnest.Build(pins);
    }

    private void StartRatsnestRead()
    {
        _ratsnestStale = false;
        RatsnestRebuildCount++;
        if (SnapshotRatsnestJob() is not { } job) { _ratsnestPins = []; _ratsnestPlaced = []; return; }

        List<(RatsnestPin Pin, int Instance)>? read = null;
        var task = ReadRatsnestOffThread(() => read = ReadRatsnestPins(job));
        RatsnestRead = task;

        // Inline (the default seam, and every test): the caller is drawing right now and draws this answer.
        if (task.IsCompleted)
        {
            if (task.IsCompletedSuccessfully) (_ratsnestPins, _ratsnestPlaced) = (read ?? [], job.Placed);
            return;
        }
        _ = PublishWhenRead(task, () => read, job.Placed);
    }

    /// <summary>Back on the UI thread: draws the answer — unless the design was edited while it was read, in
    /// which case the answer describes a design that no longer exists and the redraw starts the next read.</summary>
    private async Task PublishWhenRead(Task task, Func<List<(RatsnestPin Pin, int Instance)>?> read, LayoutInstance[] placed)
    {
        bool ok;
        try { await task; ok = true; }
        catch { ok = false; }   // a failed read draws nothing new; the next edit asks again

        if (ok && !_ratsnestStale) (_ratsnestPins, _ratsnestPlaced) = (read() ?? [], placed);
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

    private static List<(RatsnestPin Pin, int Instance)> ReadRatsnestPins(RatsnestJob job)
    {
        var copper = CopperPieces.Build(job.Flat, job.Tech, job.Placements.Shapes);

        var origins = new List<PlacedPinOrigin>();
        var nets = job.Nets;
        var placed = PlacedPins.Of(job.Placements, job.ClayPath, job.Tech, PinNaming.SchematicThenArtwork,
            portNamesOf: id => nets.For(id)?.PortNames ?? [],
            portNetsOf: id => nets.For(id)?.Nets ?? [],
            stamped: copper, origins: origins, scope: PlacementScope.EveryPlacement);

        var pins = new List<(RatsnestPin Pin, int Instance)>();
        int unjoined = -1;
        for (int i = 0; i < placed.Count && i < origins.Count; i++)
        {
            if (placed[i].Net is not { Length: > 0 } net) continue;
            int group = copper.PieceAt(placed[i].X, placed[i].Y, origins[i].Layer);
            pins.Add((new RatsnestPin(net, placed[i].X, placed[i].Y, group >= 0 ? group : unjoined--),
                      origins[i].Instance));
        }
        return pins;
    }
}
