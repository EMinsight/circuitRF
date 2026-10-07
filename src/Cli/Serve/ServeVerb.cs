using System.Text;

namespace CircuitRF.Cli.Serve;

/// <summary>
/// <c>circuitrf serve --root &lt;dir&gt;</c> — the protocol adapter, as a VERB on this binary.
///
/// <para><b>R-aut5-1: a verb, not a second executable.</b> Packaging is the constraint, not style.
/// What ships is named after the APPLICATION rather than the assembly, and that name is a literal in
/// five packaging files because .NET names the published host after the assembly and
/// <c>CrfRenameApphost</c> renames it after publish only. A second executable means a second rename,
/// a second set of those literals, and a second thing a platform script can silently omit — and
/// 1.0.0-beta.2 already shipped 7 of its 15 artifacts that way, with no error, because a missing
/// payload stops an update without saying so.</para>
///
/// <para><b>R-aut5-2: this verb is exempt from <c>cli.md</c> §3.1's channel split, and only this
/// verb.</b> stdout carries the protocol framing, so nothing else may be written to it — ever. The
/// guarantee is structural rather than a rule every printer has to remember: the real stdout is
/// taken here, <see cref="Console.Out"/> is replaced with a sink before a single capability runs,
/// and each verb's document is written to a string. Every engine progress line, <c>[circuitRF]</c>
/// note and worker log that the run verbs deliberately send to stderr keeps going there, where a
/// client's own logging picks it up.</para>
///
/// <para><b>What the server will not do</b> (R-aut5-8): it resolves every path under
/// <see cref="PathRoot"/>; it launches no shell and no process a client named — the device-worker
/// and PCell paths still launch their own, and nothing new becomes launchable because a client asked;
/// and there is no tool that deletes or overwrites, because there is no user at the other end to
/// confirm with.</para>
///
/// <para><b><c>--kits</c> is the operator's, not the client's.</b> It is one of the flags every verb
/// takes and is stripped before dispatch, so <c>circuitrf serve --root &lt;dir&gt; --kits
/// &lt;dir&gt;</c> registers the resolver for the whole server and every <c>run</c> resolves an
/// externally-supplied device model with it. It is deliberately not a tool argument: a kit folder is
/// installed software rather than design data, it lives outside the root by nature, and letting a
/// client name one would be the server pointing at an arbitrary directory on its say-so.</para>
///
/// <para><b><c>--print-config</c> starts nothing</b>: it prints the configuration an MCP client needs
/// to start THIS server, and exits. The command is the executable actually running — the installed
/// app, or <c>dotnet</c> plus the entry assembly for a source build — because the path is the part
/// people get wrong by hand, and a client's <c>PATH</c> is often not their shell's. The root and every
/// <c>--kits</c> folder are written absolute, since the client starts the server from a directory of
/// its own choosing. The root is validated first, so a config naming a missing root is never printed.
/// stdout is the client JSON and nothing else, so it can be redirected into a file; the Claude Code
/// one-liner for the same command goes to stderr.</para>
/// </summary>
internal static class ServeVerb
{
    public static int Run(string[] args)
    {
        string? root = null;
        bool printConfig = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length:
                    root = args[++i];
                    break;
                case "--print-config":
                    printConfig = true;
                    break;
                default:
                    return JsonRun.Fail(CliDiagnostics.ServeUnknownOption(args[i]));
            }
        }

        if (root is null)
        {
            int code = JsonRun.Fail(CliDiagnostics.ServeRootRequired());
            Console.Error.WriteLine("Usage: circuitrf serve --root <dir> [--kits <dir>] [--print-config]");
            return code;
        }

        var confinement = PathRoot.Open(root, out var refusal);
        if (confinement is null) return JsonRun.Fail(refusal!);

        if (printConfig)
        {
            // --json has already taken stdout, and what this prints IS a JSON document — a second
            // envelope around it would be one more thing to unwrap before pasting it anywhere.
            if (JsonRun.Enabled) return JsonRun.Fail(CliDiagnostics.ServePrintConfigJson());
            return PrintConfig(confinement.Root);
        }

        // --json is one of the flags taken before dispatch, so `serve` never sees it in its own
        // arguments — but it has already captured the real stdout, and this verb needs that stream
        // for the framing. Two writers on one stream is exactly R-aut5-2's failure, so it is refused.
        //
        // Refused HERE and not earlier, deliberately: every argument and root refusal above still
        // emits a document under --json, the way every other verb's does (R-aut-7). What cannot be
        // answered with a document is the case where the server would actually start, because from
        // that point stdout belongs to the protocol.
        if (JsonRun.Enabled) return JsonRun.Fail(CliDiagnostics.ServeJsonNotApplicable());

        // Taken BEFORE anything is redirected, and held for the protocol alone. Opened as a stream
        // rather than kept as Console.Out, so replacing Console.Out cannot take it away.
        var stdout = Console.OpenStandardOutput();
        var stdin  = Console.OpenStandardInput();

        // From here on nothing this process prints to Console.Out reaches a caller. Every verb also
        // redirects it for itself under --json; this is the belt for the paths that run before or
        // outside one.
        Console.SetOut(TextWriter.Null);

        Console.Error.WriteLine($"[circuitRF] serve: root {confinement.Root}");

        using var rpc = new JsonRpc(stdin, stdout);
        return new McpServer(rpc, confinement).Serve();
    }

    // ── --print-config ───────────────────────────────────────────────────────

    private static int PrintConfig(string root)
    {
        if (Environment.ProcessPath is not { Length: > 0 } host)
            return JsonRun.Fail(CliDiagnostics.ServePrintConfigNoProcessPath());

        var launch = new List<string>();

        // `dotnet CircuitRF.Cli.dll serve …` — a source build run through the shared host. The host
        // alone would start nothing, so the entry assembly goes first in the arguments.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase)
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
            launch.Add(entry);

        launch.Add("serve");
        launch.Add("--root");
        launch.Add(root);
        foreach (string kits in CliEntry.KitFolders)
        {
            launch.Add("--kits");
            launch.Add(Path.GetFullPath(kits));
        }

        var args = new System.Text.Json.Nodes.JsonArray();
        foreach (string a in launch) args.Add(a);

        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["circuitrf"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = host,
                    ["args"]    = args,
                },
            },
        };

        Console.Out.WriteLine(config.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            // A Windows path is backslashes, and the default encoder writes non-ASCII as \uXXXX —
            // legal JSON, but not what anyone pasting it expects to see.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        Console.Error.WriteLine("[circuitRF] Claude Code, the same server in one line:");
        Console.Error.WriteLine("  claude mcp add circuitrf -- "
                              + string.Join(' ', new[] { host }.Concat(launch).Select(ShellWord)));
        return 0;
    }

    /// <summary>One argument as the user's shell would need it typed: bare when it is plainly safe,
    /// otherwise quoted — double quotes on Windows, where a backslash is a path separator and not an
    /// escape, and single quotes elsewhere.</summary>
    internal static string ShellWord(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "/._-:+=@,%".Contains(c)
                                       || (c == '\\' && OperatingSystem.IsWindows())))
            return s;
        return OperatingSystem.IsWindows()
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : "'" + s.Replace("'", "'\\''") + "'";
    }
}
