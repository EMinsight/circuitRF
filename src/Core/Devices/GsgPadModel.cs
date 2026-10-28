using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// GSGPAD — a ground-signal-ground probe pad and the microstrip line leaving it, 2-port. Pin 1 is where the
/// probe's signal tip lands, the middle of the signal pad; pin 2 is the far end of the line.
///
/// <para><b>It is MLIN, twice.</b> Half the signal pad — <c>PadL/2</c> of microstrip <c>PadW</c> wide, from the
/// tip to the pad's edge — then the line, <c>L</c> of microstrip <c>W</c> wide, each evaluated by
/// <see cref="MicrostripLineModel"/> itself and cascaded in that order. The ground pads beside it are where the
/// probe's ground tips land and are taken as ideal ground, reached through their vias: on a calibrated probe the
/// reference plane is the tip, and the vias' inductance is the calibration's, not the circuit's. It is a
/// microstrip, never a coplanar line: the ground pads stop at the signal pad and do not run along the line.</para>
/// </summary>
public sealed class GsgPadModel : ComponentModel, IReportsWarnings, IDcShortedTerminals
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>Lossless at DC: a short from the probe tip to the line's end.</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1)];

    /// <summary>The half of the signal pad the signal crosses, or null when the pad has no length.</summary>
    public MicrostripLineModel? PadSection { get; }

    /// <summary>The line from the pad's edge to pin 2.</summary>
    public MicrostripLineModel Line { get; }

    public GsgPadModel(double wMeters, double lMeters, double padWMeters, double padLMeters,
        double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD, string instancePath,
        double roughnessMeters = 0.0)
    {
        if (padLMeters > 0 && padWMeters > 0)
            PadSection = new MicrostripLineModel(padWMeters, padLMeters / 2, hMeters, tMeters, epsR, sigmaSPerM, tanD,
                                                 instancePath, roughnessMeters);
        Line = new MicrostripLineModel(wMeters, lMeters, hMeters, tMeters, epsR, sigmaSPerM, tanD, instancePath, roughnessMeters);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() =>
        [.. PadSection?.DrainWarnings() ?? [], .. Line.DrainWarnings()];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1];
        double freqHz = omega / (2.0 * Math.PI);
        if (omega <= 0.0)
        {
            // Both sections are a short at DC, as MLIN is.
            mna.AddAdmittance(n1, n2, new Complex(1.0e9, 0.0));
            return;
        }

        var total = MicrostripAbcd.Identity;
        foreach (var section in new[] { PadSection, Line })
        {
            if (section is null) continue;
            var (z0, gammaLength) = section.LineAt(freqHz);
            total = total.Cascade(MicrostripAbcd.UniformSection(new Complex(z0, 0.0), gammaLength));
        }
        var (z11, z12, z21, z22) = total.ToZ();

        int b1 = mna.AddBranch();
        int b2 = mna.AddBranch();
        mna.AddBranchCurrent(b1, n1, 0);
        mna.AddBranchCurrent(b2, n2, 0);
        mna.AddConstraint(b1, n1, Complex.One);
        mna.AddConstraint(b2, n2, Complex.One);
        mna.AddBranchConstraint(b1, b1, -z11);
        mna.AddBranchConstraint(b1, b2, -z12);
        mna.AddBranchConstraint(b2, b1, -z21);
        mna.AddBranchConstraint(b2, b2, -z22);
    }
}
