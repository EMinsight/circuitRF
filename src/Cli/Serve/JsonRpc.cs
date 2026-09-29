using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Cli.Serve;

/// <summary>
/// The wire: newline-delimited JSON-RPC 2.0 over stdin and stdout, which is what the Model Context
/// Protocol's stdio transport is.
///
/// <para><b>stdout is the framing and NOTHING else may reach it (R-aut5-2).</b> This class holds the
/// only handle to it that exists once <c>serve</c> has started — <see cref="Console.Out"/> is
/// redirected to a sink at startup and every verb's document is written to a string. A single stray
/// <c>Console.WriteLine</c> on a code path a capability reaches would otherwise land in the middle
/// of a frame, and the client would report a parse error with no indication of the cause.</para>
///
/// <para><b>Writes are serialized.</b> A progress notification is emitted from a run in flight while
/// the reader is still reading, so two threads can want the stream at once; a lock is the whole
/// mechanism, and a half-interleaved frame is the failure it prevents.</para>
///
/// <para>Hand-rolled rather than taken from a package: the protocol layer is the disposable one by
/// design (R-aut-1), and the whole of it is a request loop, six method names and a result envelope.
/// A dependency here would outlive the adapter it serves.</para>
/// </summary>
internal sealed class JsonRpc : IDisposable
{
    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;
    /// <summary>A server-defined code (the -32000..-32099 range JSON-RPC reserves for them): the
    /// installation this server runs out of changed under it, and it is exiting (R-aut13-4).</summary>
    public const int InstallationChanged = -32001;

    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly Lock       _writeLock = new();

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public JsonRpc(Stream input, Stream output)
    {
        _in  = new StreamReader(input, new UTF8Encoding(false));
        // No BOM, and no AutoFlush: a frame is flushed as a whole, once, by Send.
        _out = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = false };
    }

    /// <summary>The next message, or null at end of stream — which is how a client disconnects.</summary>
    public JsonObject? Read(out string? malformed)
    {
        malformed = null;
        while (true)
        {
            string? line = _in.ReadLine();
            if (line is null) return null;
            if (line.Trim().Length == 0) continue;

            try
            {
                if (JsonNode.Parse(line) is JsonObject message) return message;
                malformed = line;
                return null;
            }
            catch (JsonException)
            {
                malformed = line;
                return null;
            }
        }
    }

    public void Result(JsonNode? id, JsonNode result) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = id?.DeepClone(),
        ["result"]  = result,
    });

    public void Error(JsonNode? id, int code, string message) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = id?.DeepClone(),
        ["error"]   = new JsonObject { ["code"] = code, ["message"] = message },
    });

    public void Notify(string method, JsonObject parameters) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = method,
        ["params"]  = parameters,
    });

    /// <summary>True once the far end has gone. Nothing further is written: a client that closed
    /// its read end is not coming back, and every subsequent write would throw on a worker thread
    /// for a frame nobody will read.</summary>
    // ── Requests FROM the server (brief-generated-cells-2 follow-up: elicitation) ─────────────────
    //
    // Until the kit-trust question, this server only ever answered. A request of its own is sent from
    // the WORKER thread and its answer arrives on the READER thread (cli.md §11.6), so the two meet in
    // this table. The ids are strings with a prefix no client id carries, so an answer can never be
    // mistaken for the client's own numbering.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private int _nextRequest;

    /// <summary>
    /// Sends <paramref name="method"/> to the client and waits for its answer — the whole response
    /// frame — or null when the call was cancelled or the client went away first.
    /// </summary>
    public JsonObject? Request(string method, JsonObject parameters, CancellationToken ct)
    {
        string id = "crf-" + Interlocked.Increment(ref _nextRequest);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            if (_broken) return null;
            try { answer.Task.Wait(ct); }
            catch (OperationCanceledException) { return null; }
            catch (AggregateException) { return null; }
            return answer.Task.IsCompletedSuccessfully ? answer.Task.Result : null;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Hands a response frame to the request waiting for it. False when nothing is waiting —
    /// an answer to a request this server did not send, which the protocol says to ignore.</summary>
    public bool Deliver(JsonObject response)
    {
        if (response["id"] is not JsonValue v || !v.TryGetValue<string>(out string? id)) return false;
        return _pending.TryRemove(id, out var waiting) && waiting.TrySetResult(response);
    }

    /// <summary>Releases every request still waiting — the client disconnected.</summary>
    public void AbandonPending()
    {
        foreach (var (_, waiting) in _pending) waiting.TrySetCanceled();
    }

    private bool _broken;

    private void Send(JsonObject message)
    {
        string text = message.ToJsonString(Compact);
        lock (_writeLock)
        {
            if (_broken) return;
            try
            {
                _out.Write(text);
                _out.Write('\n');
                _out.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _broken = true;
            }
        }
    }

    public void Dispose() { try { _out.Flush(); } catch { /* the client is gone */ } }
}
