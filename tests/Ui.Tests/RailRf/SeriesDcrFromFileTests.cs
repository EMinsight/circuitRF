// ================================================================
//  SeriesDcrFromFileTests.cs — a series part's DC resistance read off its own Touchstone file
//
//  Where neither the rail's part row nor the part library states a DCR, the part's measured file
//  is read at its lowest point at or below 1 kHz (2026-09-30). Above that the real part of a
//  choke's impedance carries skin effect and core loss, so a file that starts higher gives none.
// ================================================================

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.RailRf;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class SeriesDcrFromFileTests
{
    private static RailPart FileRow(double? dcrOhms = null) =>
        SeriesElementTests.Ferrite(dcrOhms: dcrOhms) with
        {
            TouchstoneRef = "FB1.s2p", SeriesResistanceOhms = null, SeriesInductanceHenries = null,
        };

    /// <summary>The rule's edges: the 1 kHz ceiling from both sides, a vendor's placeholder 0 Ω DC
    /// point, a point that blocks DC, and a stated DCR that the file never overrides.</summary>
    [Theory]
    [InlineData(1e3,  0.12, 0.0,   null, 0.12, RailSeriesValueSource.File)]
    [InlineData(5e7,  0.12, 0.0,   null, 0.0,  RailSeriesValueSource.Unstated)]
    [InlineData(0.0,  0.0,  0.0,   null, 0.0,  RailSeriesValueSource.Unstated)]
    [InlineData(100,  0.12, -50.0, null, 0.0,  RailSeriesValueSource.Unstated)]
    [InlineData(100,  0.12, 0.0,   0.2,  0.2,  RailSeriesValueSource.Row)]
    public void TheDcrIsReadBelow1kHzOnlyWhereNothingStatesOne(
        double firstHz, double re, double im, double? stated, double expected, RailSeriesValueSource from)
    {
        var file = new RailMeasuredPart("FB1.s2p", [firstHz, 1e9], [new Complex(re, im), new Complex(5, 500)], null)
        {
            Fixture = PassiveExtraction.SeriesThrough,
        };

        var model = RailSeriesModel.Resolve(FileRow(stated), null, _ => file)!;

        Assert.Equal(from, model.DcResistanceFrom);
        Assert.Equal(expected, model.DcResistanceOhms!.Value, 1e-12);
        Assert.Equal(from == RailSeriesValueSource.Unstated, model.AssumedDcResistanceLine is not null);
    }

    /// <summary>
    /// End to end: the DC run opens the row's file RELATIVE TO THE .crail, reads it the way the
    /// parts table does, and the element's DCR becomes its row of the breakdown with a note that
    /// says where it came from.
    /// </summary>
    [Fact]
    public void TheDcRunReadsTheFileBesideTheDocument()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-seriesdcr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            WriteSeriesThru(Path.Combine(dir, "FB1.s2p"), (100, new Complex(0.12, 0)), (1e8, new Complex(2, 60)));

            var board = SeriesElementTests.DcRequest(dcrOhms: null);
            board.Document.Rails[0].Parts[0] = FileRow();

            var run = RailDcRun.Run(new RailDcRequest
            {
                Document     = board.Document,
                Technology   = board.Technology,
                Shapes       = board.Shapes,
                Pads         = board.Pads,
                NetPoints    = board.NetPoints,
                DocumentPath = Path.Combine(dir, "board.crail"),
            });

            Assert.Null(run.Refusal);
            var row = run.Rails[0].Breakdown.Single(r => r.Label.Contains("FB1", StringComparison.Ordinal));
            Assert.Equal(0.12, row.ResistanceOhms, 1e-6);
            Assert.Contains(run.Rails[0].Notes, n => n.Contains("read from FB1.s2p at 100 Hz", StringComparison.Ordinal));
            Assert.DoesNotContain(run.Rails[0].Notes, n => n.Contains("taken as 0 Ω", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A series-thru two-port of impedance Z in 50 Ω: S11 = Z/(Z+100), S21 = 100/(Z+100).</summary>
    private static void WriteSeriesThru(string path, params (double Hz, Complex Z)[] points)
    {
        static string C(Complex c) => string.Create(CultureInfo.InvariantCulture, $"{c.Real:R} {c.Imaginary:R}");
        var lines = new[] { "# Hz S RI R 50" }.Concat(points.Select(p =>
        {
            var s11 = p.Z / (p.Z + 100);
            var s21 = 100 / (p.Z + 100);
            return string.Create(CultureInfo.InvariantCulture, $"{p.Hz:R} {C(s11)} {C(s21)} {C(s21)} {C(s11)}");
        }));
        File.WriteAllLines(path, lines);
    }
}
