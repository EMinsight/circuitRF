using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Commands;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// <b>The editable-reference mark on a document tab</b> (RC-2 R-rc2-5) — and the defect that made it
/// appear on tabs it can never legitimately describe.
///
/// <para>Owner-reported, 2026-09-07: every <c>.cdd</c> Data Display tab in an ordinary workspace
/// carried the pencil, which says <i>saving this writes into somebody else's project</i>. It is the
/// worst direction for this particular mark to be wrong in: the mark's whole job is to be rare, and a
/// mark that is on by default is one a designer learns to ignore before they ever meet the real
/// thing.</para>
///
/// <para><b>The cause is a general trap, which is why the gates below are about the trap and not
/// about Data Displays.</b> The tab header template declares <c>IDockable</c> as its data type and
/// binds <c>IsEditableReference</c> with compiled bindings off, because only the document types a
/// referenced workspace's sub-tree can open declare that property. The template's comment used to say
/// every other type "binds null here and renders unmarked" — and that is not what happens. A property
/// the runtime object does not have is not null, it is <c>UnsetValue</c>, and a property left unset
/// takes its DEFAULT. <c>IsVisible</c>'s default is <c>true</c>.</para>
///
/// <para><b><c>IsForeign</c>, two lines above it, is not the precedent it appears to be</b>: it runs
/// through a converter into a <c>Background</c>, where unset renders transparent either way. Only a
/// direct binding onto a boolean whose default is the wrong answer can fail this way.</para>
/// </summary>
public class EditableReferenceTabMarkTests
{
    /// <summary>
    /// <b>Document types that do not declare the property exist, and always will.</b> This is what
    /// makes the fallback necessary rather than defensive — a Data Display, a harmonicaRF instrument,
    /// a technology and an EM setup are not cell views and cannot be reached through a referenced
    /// workspace's sub-tree at all, so giving each of them the property would be four copies of the
    /// constant <c>false</c>.
    /// </summary>
    [Fact]
    public void SomeDocumentTypesDoNotDeclareTheMark_WhichIsWhyTheBindingNeedsAFallback()
    {
        var documents = typeof(CircuitRF.Ui.DataDisplay.DataDisplayDocument).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(Document).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(documents);

        var without = documents.Where(t => !typeof(IReferenceMarkedDocument).IsAssignableFrom(t)).ToList();

        Assert.True(without.Count > 0,
            "Every document type now declares IsEditableReference. If that is deliberate the tab "
          + "template's FallbackValue is no longer load-bearing — but do not remove it without saying "
          + "so here, because the next document type added will not have the property either.");

        // Named, so the list is a fact a reader can check rather than a count.
        Assert.Contains(without, t => t.Name == "DataDisplayDocument");
    }

    /// <summary>
    /// <b>The mark's binding carries <c>FallbackValue=False</c>.</b>
    ///
    /// <para>Source-scanned because the failure is a rendering one on a template this project's tests
    /// cannot instantiate — there is no headless Avalonia harness here — and because what can go wrong
    /// is a single attribute being dropped, which is exactly what a source scan sees.</para>
    /// </summary>
    [Fact]
    public void TheTabMarkBindsWithAFalseFallback()
    {
        string xaml = File.ReadAllText(SourcePath("Styles/CircuitRfStyles.axaml"));

        System.Text.RegularExpressions.Match binding = Regex.Match(xaml, @"IsVisible=""\{Binding IsEditableReference[^}]*\}""");
        Assert.True(binding.Success,
            "The document tab's editable-reference mark no longer binds IsEditableReference; this gate "
          + "must follow it.");

        Assert.Contains("FallbackValue=False", binding.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same trap, stated as a rule over the whole template: <b>a direct binding in the tab header
    /// onto a property <c>IDockable</c> does not declare needs a fallback.</b> The template is the one
    /// place in this application that binds against a heterogeneous set of runtime types with compiled
    /// bindings deliberately off, so it is the one place this can happen.
    /// </summary>
    [Fact]
    public void EveryDirectBindingInTheTabHeaderCarriesAFallback()
    {
        string xaml = File.ReadAllText(SourcePath("Styles/CircuitRfStyles.axaml"));

        int start = xaml.IndexOf("<Setter Property=\"HeaderTemplate\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "The document tab HeaderTemplate has moved; this gate must follow it.");

        int end = xaml.IndexOf("</Setter>", start, StringComparison.Ordinal);
        string template = xaml[start..end];

        // One level of nested braces in the rest, so FallbackValue={x:Null} (brief-em3d-98's SolveBadges) is seen too.
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(template, @"(\w+)=""\{Binding ([A-Za-z]+)((?:[^{}]|\{[^{}]*\})*)\}"""))
        {
            string property = m.Groups[1].Value;
            string source   = m.Groups[2].Value;
            string rest     = m.Groups[3].Value;

            // Title is IDockable's own, so every runtime type has it.
            if (source == "Title") continue;

            // A converter absorbs UnsetValue on the way through, which is why IsForeign never showed
            // this. Anything else must say what unset means.
            if (rest.Contains("Converter=", StringComparison.Ordinal)) continue;

            Assert.True(
                rest.Contains("FallbackValue=", StringComparison.Ordinal),
                $"The tab header binds {property} to '{source}', which not every document type declares, "
              + "with no FallbackValue. A missing property is UnsetValue, not null, and the target "
              + $"property then takes its DEFAULT — which for {property} is the wrong answer on every "
              + "document that lacks it.");
        }
    }

    private static string SourcePath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Ui")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Ui",
                            relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
