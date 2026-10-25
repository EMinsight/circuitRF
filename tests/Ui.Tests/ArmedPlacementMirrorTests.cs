using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Input;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// M / Shift+M mirror the ARMED ghost before the click, as R / Shift+R already rotate it, and the part
/// lands the way the ghost faced — so an IProbe cut into a wire points its arrow the chosen way with
/// no edit afterwards (owner request, 2026-10-25). The mirror is the reflection
/// <c>MirrorCommand</c> applies to a placed part: the flag toggles and the rotation is re-derived.
/// </summary>
public class ArmedPlacementMirrorTests
{
    private static (SchematicEditModel Model, SchematicViewModel Vm, PlacementService Svc) ArmedFromPalette(SymbolKind kind)
    {
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model);
        var svc   = new PlacementService();
        vm.SetPlacementService(svc);
        svc.Toggle(kind, 2);
        return (model, vm, svc);
    }

    [Theory]
    [InlineData(KeyModifiers.None,  SymbolRotation.R0)]     // left↔right: the flag alone
    [InlineData(KeyModifiers.Shift, SymbolRotation.R180)]   // top↔bottom: the flag and a half turn
    public void M_MirrorsThePaletteGhost_AndThePartLandsMirrored(KeyModifiers mods, SymbolRotation expected)
    {
        var (model, vm, svc) = ArmedFromPalette(SymbolKind.IProbe);

        Assert.True(vm.OnKeyDown(Key.M, mods));
        Assert.True(svc.Pending!.MirrorX);
        Assert.Equal(expected, svc.Pending.Rotation);

        vm.OnPointerMoved(700, 300, leftDown: false);
        vm.OnPointerPressed(700, 300, KeyModifiers.None);

        var placed = Assert.Single(model.Components);
        Assert.True(placed.MirrorX);
        Assert.Equal(expected, placed.Rotation);
    }

    /// <summary>Rotating after mirroring keeps the mirror: the canvas re-reads the armed state on every
    /// change, and used to reset the flag on each one.</summary>
    [Fact]
    public void R_AfterM_KeepsTheMirror()
    {
        var (model, vm, svc) = ArmedFromPalette(SymbolKind.IProbe);

        vm.OnKeyDown(Key.M, KeyModifiers.None);
        vm.OnKeyDown(Key.R, KeyModifiers.None);
        vm.OnPointerMoved(700, 300, leftDown: false);
        vm.OnPointerPressed(700, 300, KeyModifiers.None);

        var placed = Assert.Single(model.Components);
        Assert.True(placed.MirrorX);
        Assert.Equal(SymbolRotation.R90, placed.Rotation);
    }

    /// <summary>The toolbar's P / T / G placements arm without the service; Shift+M flipped nothing there.</summary>
    [Fact]
    public void ShiftM_MirrorsTheToolbarGhostVertically()
    {
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model);
        vm.BeginPlacement(SymbolKind.Term);

        vm.OnKeyDown(Key.M, KeyModifiers.Shift);
        vm.OnPointerMoved(700, 300, leftDown: false);
        vm.OnPointerPressed(700, 300, KeyModifiers.None);

        var placed = Assert.Single(model.Components);
        Assert.True(placed.MirrorX);
        Assert.Equal(SymbolRotation.R180, placed.Rotation);
    }

    /// <summary>
    /// Clicking a Library tile leaves keyboard focus ON the tile, so a key only the canvas handles never
    /// arrives — M did nothing at first for exactly that reason, while R worked because the window routes
    /// it. The window must route both. A <c>Window</c> cannot be built headlessly here, so the rule is
    /// held by a scan of the handler with comments stripped (as <see cref="DropPlacesUnrotatedTests"/>).
    /// </summary>
    [Fact]
    public void TheWindow_RoutesM_ToTheArmedPlacement_WhereverFocusIs()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var code = File.ReadAllText(Path.Combine(dir!, "src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));
        code = Regex.Replace(code, @"//[^\n]*", " ");
        int at = code.IndexOf("void OnWindowKeyDownTunnel", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string handler = code[at..code.IndexOf("bool TryHandleWirePanelKeys", at, StringComparison.Ordinal)];

        Assert.Contains("Key.M", handler);
        Assert.Contains("PlacementService.Mirror(", handler);
    }
}
