using System.Globalization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Look;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// brief-em3d-110 R-em3d110-1g — <c>explain x.c3d --look</c>: what <c>render --look realistic</c> would draw with, as a walk — every key
/// of the Look with its value and whether the file stated it, the environment and where it came from (a preset, or the <c>.hdr</c>'s
/// resolved path and whether it reads), and the appearance table: each slot's values with the statement that decided each field
/// (brief 105 §3a, through <see cref="Scene3DBuilder.AppearanceOf"/> — the 3D view's own answer) and the objects that use it.
///
/// <para>It writes no rule of its own: the values are <see cref="C3dLook"/>'s and <see cref="RealisticLook"/>'s readers, the
/// environment <see cref="EnvironmentPrefilter.For"/>'s, the table <see cref="Scene3DBuilder"/>'s, from the scene `render` builds.</para>
/// </summary>
internal static class ExplainLook
{
    public static int Walk(string path, string? setupName, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }
        var stated = doc.Look ?? new C3dLook();
        var look = RealisticLook.From(doc.Look);
        const string Keys = "the .c3d's Look, as the realistic view and `render --look realistic` read it; an omitted key is its default";

        void Key(string key, object? state, string value)
            => walks.Add(new ResolutionStepJson($"look {key}", state is null ? "the default" : "the file", value, Keys));

        string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        stated.EnvironmentOf(out bool known);
        Key(nameof(C3dLook.Environment), stated.Environment, known ? stated.EnvironmentLabel : $"\"{stated.Environment}\" names nothing: Studio");
        Key(nameof(C3dLook.Rotation), stated.Rotation, $"{Num(stated.RotationDegrees)}°");
        Key(nameof(C3dLook.Intensity), stated.Intensity, Num(stated.IntensityValue));
        Key(nameof(C3dLook.Exposure), stated.Exposure, $"{C3dLook.FormatEv(stated.ExposureValue)} EV");
        Key(nameof(C3dLook.Background), stated.Background,
            stated.TryBackground(out var kind, out _, out _) ? $"{kind}{(stated.Background is { } b && kind is C3dBackgroundKind.Solid or C3dBackgroundKind.Gradient ? $" ({b})" : "")}"
                                                            : $"\"{stated.Background}\" is no background: Theme");
        foreach (var (row, key) in RealisticLook.Chrome)
            Key(key, typeof(C3dLook).GetProperty(key)!.GetValue(stated), look.Shows(row) ? "shown" : "hidden");
        Key(nameof(C3dLook.Shadows), stated.Shadows, look.Shadows ? "on" : "off");
        Key(nameof(C3dLook.AmbientOcclusion), stated.AmbientOcclusion, look.AmbientOcclusion ? "on" : "off");
        Key(nameof(C3dLook.Ground), stated.Ground, look.Ground ? "on" : "off");
        Key(nameof(C3dLook.Camera), stated.Camera, stated.Camera is not { } cam ? "none: `render` needs --iso or --view-dir"
            : cam.Faults() is { Count: > 0 } faults ? $"unusable: {string.Join("; ", faults)}"
            : $"{(cam.IsOrthographic ? "orthographic" : "perspective")}, toward ({string.Join(", ", cam.Direction!.Select(Num))}), " +
              $"target ({string.Join(", ", cam.Target!.Select(Num))}) DBU, distance {Num(cam.Distance!.Value)} DBU" +
              (cam.FovY is { } fov ? $", {Num(fov)}° field of view" : ""));
        Key(nameof(C3dLook.FieldStyle), stated.FieldStyle, look.FieldStyle.ToString());
        Key(nameof(C3dLook.FieldOpacity), stated.FieldOpacity, $"{Num(stated.FieldOpacityValue)} %");

        // the environment: a preset, or a file — its resolved path, and whether it reads (the picture falls back to Studio, and says so)
        string? hdr = look.HdrPath is { } p ? C3dLook.ResolvePath(p, full) : null;
        string environment;
        if (hdr is null) environment = $"{C3dLook.StudioLabel(look.Studio)}: a procedural studio, a preset";
        else
        {
            var env = EnvironmentPrefilter.For(look.Studio, hdr);
            environment = env.Fallback is { } why ? $"{hdr} — {why}" : $"{hdr} — found, and it reads";
        }
        walks.Add(new ResolutionStepJson("look environment", hdr is null ? "a preset" : look.HdrPath, environment,
            "a studio by name, or a Radiance .hdr relative to the .c3d; one that cannot be read lights the scene with Studio"));

        // the appearance table: the scene `render` builds, each slot's values and the statement behind each field
        Em3dSetupSource src;
        try { src = Em3dSetupSource.ForThreeDView(full, setupName); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }
        if (src.Generated?.Problem is not { } problem || src.Elaboration is not { } e) return src.Refusal is null ? 0 : 1;
        var scene = RenderEm3dRealistic.Scene(src, ColorTheme.BuiltIn, ColorVariant.Light, airBox: false);
        var looks = AppearanceOverride.Of(e.Provenance);
        const string Rule = "the scene's appearance table (equal appearances share a slot): object, instances innermost first, material " +
                            "(each with its Like), the material's Color, the role's default — read with the default theme";
        for (int slot = 0; slot < scene.Appearances.Length; slot++)
        {
            var users = scene.Objects.Take(scene.OwnedObjects).Where(o => o.AppearanceSlot == slot).Select(o => o.Name).ToList();
            var a = users.Select(n => Scene3DBuilder.AppearanceOf(problem, n, e.Origins, e.Technology, looks(n))).FirstOrDefault(x => x is not null);
            var v = scene.Appearances[slot];
            string Of(string field) => a?.Provenance.GetValueOrDefault(field) is { } from ? $" ({from})" : "";
            string values = string.Join("; ", new (string Field, string Value)[]
            {
                ("BaseColor", Linear(v.BaseColor)), ("Metallic", Num(v.Metallic)), ("Roughness", Num(v.Roughness)),
                ("Transmission", Num(v.Transmission)), ("Ior", Num(v.Ior)), ("Clearcoat", Num(v.Clearcoat)),
                ("ClearcoatRoughness", Num(v.ClearcoatRoughness)), ("AttenuationColor", Linear(v.AttenuationColor)),
            }.Select(f => $"{f.Field} {f.Value}{Of(f.Field)}"));
            walks.Add(new ResolutionStepJson($"appearance slot {slot}", users.Count == 0 ? null : string.Join(", ", users),
                                             $"{values} — used by {(users.Count == 0 ? "nothing drawn" : string.Join(", ", users))}", Rule));
        }
        return src.Refusal is null ? 0 : 1;

        string Linear(AppearanceColour c) => $"({Num(c.R)}, {Num(c.G)}, {Num(c.B)}) linear";
    }
}
