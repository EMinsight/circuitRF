using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>
/// <b>Every sentence RC-5 puts in front of a designer</b>
/// (<c>docs/design/revision-control.md</c> §5.5, §5.7a, §8.2, §8.2b; R-rc5-2, R-rc5-4b, R-rc5-10,
/// R-rc5-11, R-rc5-15a).
///
/// <para><b>Diagnostics, not strings</b> (R-rc3-6). The Messages panel and the CLI render the same
/// wording from one source, the ids are what dedup and once-per-session suppression key on, and a
/// sentence is never assembled twice.</para>
///
/// <para><b>No git vocabulary in any of them</b> (R-rc0-6, gate 2) — no branch, checkout, HEAD,
/// commit, detached, reference or stash. What is kept is a "restore point"; where it is kept is "this
/// workspace's history"; what a designer does with one is "go back to it".</para>
/// </summary>
public static class RestorePointMessages
{
    /// <summary>
    /// R-rc5-4b. <b>The first time a workspace gains a history, that is announced — once.</b>
    ///
    /// <para>§1.4's rule is that a state must be VISIBLE, not that it must be absent, and this is the
    /// cheap half of that rule. It is the exception to R-rc5-11's silence, and it is an exception
    /// because it is not a restore point: it is the creation of the thing restore points go into. It
    /// says what was made, where the setting is, and — per §5.7's closing paragraph — that removing it
    /// later cannot harm the design, because that is the sentence that stops the announcement being
    /// alarming.</para>
    /// </summary>
    public static Diagnostic HistoryStarted(string workspaceName) => Diagnostic.Create(
        "revision.history.started",
        DiagnosticSeverity.Info,
        "circuitRF has started keeping a history of '{workspace}', so you can go back to how it was "
      + "earlier. You can turn this off in Settings ▸ Revision Control. Removing the history later "
      + "means deleting one plainly-named folder inside the workspace, and it cannot harm the design.",
        ("workspace", workspaceName));

    /// <summary>
    /// R-rc5-10. <b>A restore point that could not be taken is always reported</b>, and the entry says
    /// WHAT WAS NOT SAVED and why. §1.4 forbids a designer believing they are protected when they are
    /// not, and one failing quietly is the purest form of that failure.
    /// </summary>
    public static Diagnostic CouldNotTake(string what, string why) => Diagnostic.Create(
        "revision.restore-point.not-taken",
        DiagnosticSeverity.Error,
        "circuitRF could not keep a restore point for {what}, so that state is not in this workspace's "
      + "history. {why}",
        ("what", (object?)what), ("why", why));

    /// <summary>
    /// R-rc5-11. <b>Once per session</b>, however many were skipped: enough to be unmissable, not so
    /// often that the panel becomes noise a designer learns to scroll past.
    /// </summary>
    public static Diagnostic BatchCheckpointSkipped(string why) => Diagnostic.Create(
        "revision.batch.no-restore-point",
        DiagnosticSeverity.Warning,
        "An assistant asked to change this workspace and circuitRF could not keep a restore point "
      + "first, so nothing was changed. {why}",
        ("why", why));

    /// <summary>R-rc5-6f, first member: recording is switched off for this workspace.</summary>
    public static Diagnostic RecordingOff() => new(
        "revision.batch.refused.off",
        DiagnosticSeverity.Error,
        "This workspace does not keep a history, so there is nothing to fall back to and nothing was "
      + "changed. Turn it on in Settings ▸ Revision Control and ask again.");

    /// <summary>R-rc5-6f, second member: RC-6's held state — a repository circuitRF does not manage.</summary>
    public static Diagnostic RecordingHeld() => new(
        "revision.batch.refused.held",
        DiagnosticSeverity.Error,
        "This workspace's history is not circuitRF's to write to, so no restore point can be kept "
      + "before a change and nothing was changed. Ask the designer to let circuitRF look after this "
      + "workspace's history, then ask again.");

    /// <summary>R-rc5-20. Scratch: there is no folder, so there is nothing to record into — and the
    /// remedy is OFFERED rather than named.</summary>
    public static Diagnostic ScratchHasNowhereToRecord() => new(
        "revision.batch.refused.scratch",
        DiagnosticSeverity.Error,
        "This workspace has not been saved anywhere yet, so there is nowhere to keep a restore point "
      + "and nothing was changed. Save the workspace and ask again.");

    /// <summary>
    /// R-rc5-7a, the fourth member of the family. <b>A batch is refused while the window holds unsaved
    /// changes, before anything is modified.</b> §5.8 resolves this for a restore by asking the
    /// designer; a batch is headless and out of process and there is nobody to answer a prompt, so a
    /// refusal is the only honest answer.
    /// </summary>
    public static Diagnostic WindowHasUnsavedChanges() => new(
        "revision.batch.refused.unsaved",
        DiagnosticSeverity.Error,
        "This workspace has unsaved changes open in circuitRF, so a change made underneath them would "
      + "be lost when they are saved. Nothing was changed. Save them and ask again.");

    /// <summary>
    /// R-rc5-6c's other half: <b>a batch is already open on a DIFFERENT workspace.</b>
    ///
    /// <para>A second open on the SAME workspace is the same batch and takes no second entry — the
    /// designer's question, <i>what did this look like before the agent touched it</i>, has exactly one
    /// answer. On a different workspace it is not the same batch and cannot be treated as one, and the
    /// alternative to refusing is worse than untidy: replacing the open batch abandons it, so the
    /// window holding the first workspace is never told which documents changed and never reloads
    /// them. It goes on showing the old content over an undo stack describing edits its files no
    /// longer contain, and its next save discards everything the first batch did — the
    /// two-editors-one-file failure §7A.3 already calls worse than the divergence §7A.2 exists to
    /// prevent, reached through the mechanism §1.2 is the motive for.</para>
    ///
    /// <para><b>It names the open batch</b>, because an agent given a vague refusal will improvise,
    /// which §5.3b rule 9 forbids — and because the remedy is one call the agent already has.</para>
    /// </summary>
    /// <param name="openWorkspace">The workspace the running batch belongs to.</param>
    /// <param name="openIntent">What that batch said it was doing, when it said anything.</param>
    public static Diagnostic AnotherBatchIsOpen(string openWorkspace, string? openIntent)
        => openIntent?.Trim() is { Length: > 0 } intent
            ? Diagnostic.Create(
                  "revision.batch.refused.another-open",
                  DiagnosticSeverity.Error,
                  "A change is already under way in '{workspace}' ({intent}), and circuitRF looks after "
                + "one at a time. Nothing was changed here. Close that one first, then ask again.",
                  ("workspace", (object?)openWorkspace), ("intent", intent))
            : Diagnostic.Create(
                  "revision.batch.refused.another-open",
                  DiagnosticSeverity.Error,
                  "A change is already under way in '{workspace}', and circuitRF looks after one at a "
                + "time. Nothing was changed here. Close that one first, then ask again.",
                  ("workspace", (object?)openWorkspace));

    /// <summary>There is no git on this machine, so no history can be kept. Reported only where the
    /// caller ASKED — R-rc3-3's silence still governs every automatic path.</summary>
    public static Diagnostic NoGitHere() => new(
        "revision.batch.refused.no-git",
        DiagnosticSeverity.Error,
        "circuitRF cannot keep a history on this machine, so there is nothing to fall back to and "
      + "nothing was changed.");

    // ── The large-file guard (§8.2, §8.2b) ────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-15a. <b>At a boundary nobody is at, the restore point proceeds and the file is left
    /// out</b>, and the entry says so. §9A.1's rule decides the direction: including is irreversible,
    /// leaving out is not. What must not happen is the silent version of either.
    /// </summary>
    public static Diagnostic LeftOutUnattended(string names, int count) => Diagnostic.Create(
        "revision.large-file.left-out",
        DiagnosticSeverity.Warning,
        "{count} unusually large file(s) or folder(s) were left out of this workspace's history because nobody was "
      + "there to be asked: {names}. They are still on disk and unchanged. circuitRF will ask about "
      + "them the next time you keep a restore point yourself.",
        ("count", (object?)count), ("names", names));

    /// <summary>
    /// R-rc5-16. <b>The consequence of "never include files like this", at the point of choosing.</b>
    /// §10B requires this sentence to appear where the choice is made and not only in a manual.
    /// </summary>
    public const string NeverIncludeConsequence =
        "Files matching this pattern are not kept in the history, so they are not in a restore and "
      + "not in a copy anyone else takes of this workspace. They stay on disk exactly as they are.";

    /// <summary>What "include it" costs, in the terms §2 measured it in.</summary>
    public const string IncludeConsequence =
        "The file is kept in the history: one copy now, and one more only when it changes.";

    /// <summary>What "leave it out this time" does, which is nothing.</summary>
    public const string LeaveOutConsequence =
        "Nothing is written and the file stays on disk. circuitRF will ask again next time.";

    // ── Restore (§5.8) ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-14. <b>A restore restores this workspace's files and nothing else, and says so where it
    /// could mislead.</b> Content in a referenced workspace is not in this repository, so
    /// "restore my workspace" sounds more total than it is.
    /// </summary>
    public const string RestoreCoversThisWorkspaceOnly =
        "This brings back the files in this workspace. Anything in a workspace it refers to is not "
      + "part of this workspace's history and is left exactly as it is.";

    /// <summary>
    /// RC-9 R-rc9-14: <b>the half of R-rc5-14's caveat the pin retires</b>, and only that half. Where a
    /// reference names a version, the restore brings that back too — which is what makes a restore
    /// complete rather than "your files, and today's library".
    /// </summary>
    public const string RestoreBringsBackTheLibraryVersionsToo =
        "It also brings back which version of each referenced workspace this design uses, so the "
      + "restored design resolves against what it did then. The referenced workspaces' own files are "
      + "still theirs and are left exactly as they are.";

    /// <summary>
    /// The right caveat for this workspace. <b>Both sentences are true and neither is true in the
    /// other's case</b>, so the choice is made from the workspace rather than by saying the weaker
    /// thing always — a designer whose references are all pinned would otherwise be told the restore
    /// was less complete than it is, and one with none would be told it was more.
    /// </summary>
    public static string RestoreReferenceCaveat(bool anyReferenceIsPinned)
        => anyReferenceIsPinned
            ? RestoreCoversThisWorkspaceOnly + " " + RestoreBringsBackTheLibraryVersionsToo
            : RestoreCoversThisWorkspaceOnly;

    /// <summary>Results are not in a restore point, so a restore leaves them alone (§5.8).</summary>
    public const string RestoreLeavesResultsAlone =
        "Simulation results are not kept in the history, so they are left untouched — re-run the "
      + "analysis if you need them to match.";

    /// <summary>What a restore did, once it has. Info, because the designer asked for it.</summary>
    public static Diagnostic Restored(string label, int filesWritten, int filesRemoved) => Diagnostic.Create(
        "revision.restored",
        DiagnosticSeverity.Info,
        "This workspace is back to '{label}'. {written} file(s) were brought back and {removed} added "
      + "since then were taken away. The state you were in a moment ago is in the list too, so you can "
      + "go back to it.",
        ("label", (object?)label), ("written", filesWritten), ("removed", filesRemoved));

    /// <summary>
    /// R-rc5-12c, last rule. <b>An interrupted restore is detected on the next open</b> — thousands of
    /// files over a share can be cut off by a crash or a dropped connection, leaving §1.3's failure
    /// exactly: well-formed, and half of two states. The entry names BOTH ends and the two ways out.
    /// </summary>
    public static Diagnostic RestoreWasInterrupted(string target, string fallback) => Diagnostic.Create(
        "revision.restore.interrupted",
        DiagnosticSeverity.Error,
        "Going back to '{target}' did not finish, so this workspace is part one state and part "
      + "another. Finish it to reach '{target}', or go back to '{fallback}' — the state it was in "
      + "before it started.",
        ("target", (object?)target), ("fallback", fallback));

    /// <summary>
    /// The same report when the marker itself was truncated — <b>which is the crash case, not an
    /// exotic one</b>: the write that was cut off is the write the crash cut off.
    ///
    /// <para>It names no states because there are none to name, and it must not pretend otherwise: the
    /// ordinary message's two quoted labels would render as two pairs of empty quotes, which reads as
    /// a defect rather than as the one thing that is certainly true — that this workspace may be part
    /// one state and part another, and that the list is where to settle it.</para>
    /// </summary>
    public static Diagnostic RestoreWasInterruptedUnnamed() => new(
        "revision.restore.interrupted-unnamed",
        DiagnosticSeverity.Error,
        "Going back to an earlier state did not finish, so this workspace may be part one state and "
      + "part another. circuitRF could not read which states were involved. Check the restore points "
      + "and go back to the one you want.");

    /// <summary>
    /// R-rc5-19. <b>The one sentence a Save Workspace As copy adds</b>, because that is the only one
    /// of the three journeys with no dialog to read: without it, "I saved a copy and my history is
    /// gone" is a discovery rather than a decision.
    /// </summary>
    public const string CopyStartsItsOwnHistory =
        "The copy starts a history of its own. The original keeps every restore point and every "
      + "version it had.";
}
