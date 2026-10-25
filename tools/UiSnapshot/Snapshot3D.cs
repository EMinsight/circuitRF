using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;

// UiSnapshot --c3d <file.c3d> -o <out.png> [--size WxH] [--realistic] [--fit] [--ortho|--perspective]
//            [--yaw deg] [--pitch deg] [--distance mm] [--target x,y,z] [--only a+b] [--hide a+b] [--list]
//
// Opens a .c3d in the 3D editor's own view model, on the platform's own GPU backend (Metal, D3D11 or Vulkan, as
// Viewer3DBackends.Create picks for the application), and writes what Export Picture writes: Viewer3DViewModel's
// CapturePicture, the same plan and read-back. No Avalonia window is involved — the editor's view model needs only a
// dispatcher to post to, which is the queue this pumps.
//
// The camera starts where the owner left it: the workspace's .cwsuser, as OpenC3dEditor restores it. --fit frames the
// scene instead; --yaw/--pitch/--distance/--target then move it, in the camera's own terms (degrees, millimetres, the
// scene-local frame --list prints). --only/--hide name objects by substring, '+' between several.
internal static class Snapshot3D
{
    public static int Run(string[] args)
    {
        string? path = null, output = null, only = null, hide = null;
        int w = 1200, h = 800;
        bool realistic = false, fit = false, list = false;
        bool? ortho = null;
        float? yaw = null, pitch = null, distance = null;
        Vector3? target = null;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--c3d" when i + 1 < args.Length: path = Path.GetFullPath(args[++i]); break;
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--size" when i + 1 < args.Length:
                    var wh = args[++i].Split('x');
                    (w, h) = (int.Parse(wh[0]), int.Parse(wh[1]));
                    break;
                case "--realistic": realistic = true; break;
                case "--fit": fit = true; break;
                case "--ortho": ortho = true; break;
                case "--perspective": ortho = false; break;
                case "--yaw" when i + 1 < args.Length: yaw = F(args[++i]) * MathF.PI / 180; break;
                case "--pitch" when i + 1 < args.Length: pitch = F(args[++i]) * MathF.PI / 180; break;
                case "--distance" when i + 1 < args.Length: distance = F(args[++i]) * 1e-3f; break;
                case "--target" when i + 1 < args.Length:
                    var t = args[++i].Split(',').Select(v => F(v) * 1e-3f).ToArray();
                    target = new Vector3(t[0], t[1], t[2]);
                    break;
                case "--only" when i + 1 < args.Length: only = args[++i]; break;
                case "--hide" when i + 1 < args.Length: hide = args[++i]; break;
                case "--list": list = true; break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }
        if (path is null || (output is null && !list))
        {
            Console.Error.WriteLine("usage: UiSnapshot --c3d <file.c3d> -o <out.png> [--size WxH] [--realistic] [--fit] [--ortho|--perspective]\n"
                                  + "                  [--yaw deg] [--pitch deg] [--distance mm] [--target x,y,z] [--only a+b] [--hide a+b] [--list]");
            return 2;
        }

        var posted = new ConcurrentQueue<Action>();
        void Pump(Func<bool> until, string what)
        {
            if (!SpinWait.SpinUntil(() => { while (posted.TryDequeue(out var a)) a(); return until(); }, TimeSpan.FromMinutes(2)))
                throw new TimeoutException($"the 3D editor never {what}");
        }

        string? cws = FindWorkspace(path);
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), Viewer3DBackends.Create, () => cws, posted.Enqueue);
        try
        {
            // OpenC3dEditor's own order: orthographic, then the stored camera and setup, which win.
            vm.Viewer.IsPerspective = false;
            var user = cws is null ? null : WorkspacePersistence.LoadFromFile(cws);
            string? key = cws is null ? null : Path.GetRelativePath(Path.GetDirectoryName(cws)!, path).Replace('\\', '/');
            if (!fit && key is not null && user?.Viewer3DCameras?.GetValueOrDefault(key) is { } camera) vm.Viewer.RestoreCamera(camera);
            vm.RestoreActiveSetup(key is not null ? user?.C3dActiveSetups?.GetValueOrDefault(key) : null);
            vm.Viewer.Resized(w, h);
            vm.Viewer.Session.EnsureBackend();
            vm.Start();
            Pump(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "built a scene");
            var scene = vm.Viewer.Scene;
            if (list)
            {
                foreach (var o in scene.Objects)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{o.Name}\t{o.Kind}\t{o.Min.X * 1e3:F4},{o.Min.Y * 1e3:F4},{o.Min.Z * 1e3:F4} .. {o.Max.X * 1e3:F4},{o.Max.Y * 1e3:F4},{o.Max.Z * 1e3:F4} mm"));
                if (output is null) return 0;
            }

            var view = vm.Viewer.View;
            if (only is not null || hide is not null)
                foreach (var o in scene.Objects)
                {
                    bool named(string? keys) => keys?.Split('+').Any(k => o.Name.Contains(k, StringComparison.Ordinal)) == true;
                    if ((only is not null && !named(only)) || named(hide)) view.Visible[o.Id - 1] = false;
                }
            if (ortho is { } o2)
            {
                vm.Viewer.IsPerspective = !o2;
                view.Camera.Projection = o2 ? Projection3D.Orthographic : Projection3D.Perspective;
            }
            if (yaw is { } y) view.Camera.Yaw = y;
            if (pitch is { } p) view.Camera.Pitch = p;
            if (distance is { } d) view.Camera.Distance = d;
            if (target is { } tg) view.Camera.Target = tg;
            if (realistic)
            {
                vm.Viewer.IsRealistic = true;
                Pump(() => view.Environment is not null, "made the realistic view's environment");
            }

            var shot = vm.Viewer.CapturePicture(w, h, 1, out string? error, supersample: 1);
            if (shot is null) { Console.Error.WriteLine($"no picture: {error}"); return 1; }
            File.WriteAllBytes(output!, shot.Png());
            var c = view.Camera;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Wrote {output} ({w}x{h}, {(realistic ? "realistic" : "plain")}, {c.Projection}) — yaw {c.Yaw * 180 / MathF.PI:F1}°, " +
                $"pitch {c.Pitch * 180 / MathF.PI:F1}°, distance {c.Distance * 1e3:F4} mm, target {c.Target.X * 1e3:F4},{c.Target.Y * 1e3:F4},{c.Target.Z * 1e3:F4} mm"));
            return 0;
        }
        finally { vm.Dispose(); }
    }

    /// <summary>The nearest .cws above the document — the workspace the GUI would have open around it.</summary>
    private static string? FindWorkspace(string path)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, ".cws"))) return Path.Combine(dir, ".cws");
        return null;
    }
}
