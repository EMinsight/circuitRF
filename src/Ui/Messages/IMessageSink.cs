using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// Posts messages to the Messages region. All app-layer code (WorkspaceViewModel, run
/// controller, net-extraction validation) uses this interface; the engine itself never
/// calls it directly (it returns a DataSet; the UI layer reads the result and posts).
/// The implementation is MessagesTool (a Dock Tool VM), injected at startup.
/// </summary>
public interface IMessageSink
{
    void Post(MessageLevel level, string text, string? filePath = null);

    /// <summary>
    /// Posts a message that stays LIVE — its text and progress bar are rewritten in place while a long
    /// operation runs, and it settles into an ordinary message on
    /// <see cref="IProgressMessage.Complete"/>. The default posts the opening line and degrades to
    /// an ordinary start/finish pair, so a sink with no live-message support needs no changes.
    /// </summary>
    IProgressMessage BeginProgress(string text)
    {
        Post(MessageLevel.Info, text);
        return new PostOnlyProgressMessage(this);
    }

    /// <summary>
    /// Posts a message carrying a single ACTION the user can invoke from the row — a button after
    /// the text, hosted inline the same way the progress bar is.
    ///
    /// <para><b>The default implementation drops the action and posts the text.</b> Every message
    /// worded for this must therefore still read correctly with no button on the end: the sink
    /// behind a console tool or a headless run has no way to offer one, and a sentence that only
    /// makes sense next to a button would be a sentence those users cannot act on. The one caller —
    /// the update announcement — says "Relaunch circuitRF to start using it" and then offers the
    /// button as a shortcut for exactly that, so nothing is lost when it degrades.</para>
    ///
    /// <para><paramref name="filePath"/> is the row's reveal link, as <see cref="Post"/> takes it — kept when the
    /// action is dropped (brief-em3d-97: a memory warning links <c>.wslconfig</c> AND offers to raise it).</para>
    /// </summary>
    void PostAction(MessageLevel level, string text, string actionLabel, Func<Task> action, string? filePath = null)
        => Post(level, text, filePath);

    /// <summary>
    /// Posts a message that EXPANDS to <paramref name="items"/> — the places it is about — each of which a
    /// double-click acts on (brief-artsch-8 R-as8-4: the report goes to Messages one line per class, expandable to
    /// its anchors). <b>The default drops the items and posts the text</b>, so the line must read correctly alone,
    /// as <see cref="PostAction"/>'s must.
    /// </summary>
    void PostItems(MessageLevel level, string text, IReadOnlyList<MessageItem> items, string? filePath = null)
        => Post(level, text, filePath);

    void Info(string text, string? filePath = null)    => Post(MessageLevel.Info, text, filePath);
    void Success(string text, string? filePath = null) => Post(MessageLevel.Success, text, filePath);
    void Warning(string text, string? filePath = null) => Post(MessageLevel.Warning, text, filePath);
    void Error(string text, string? filePath = null)   => Post(MessageLevel.Error, text, filePath);

    void Clear();
}
