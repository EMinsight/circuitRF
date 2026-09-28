using CircuitRF.Design.Layout;
using CircuitRF.Design.Thermal;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ThermalKTensor — a material's anisotropic k, as the volume solve receives it. The solver's own tensor path is gated by
// Thermal.Tests' InterfaceGateTests.Gate3_Anisotropy; these hold the material → conductivity step and the check rules.
public sealed class ThermalKTensorTests
{
    [Fact]
    public void Tensor_WithoutATable_IsTheStatedConstantDiagonal()
    {
        var m = new TechMaterial { Name = "Laminate", ThermalK = 0.8, ThermalKTensor = [0.8, 0.8, 0.3] };
        var k = ThermalRunService.Tensor(m, m.ThermalKTensor!, 0.8);
        Assert.True(k.IsConstant);
        Assert.Equal(0.8, k.Nominal * k.Axes.X, 12);
        Assert.Equal(0.8, k.Nominal * k.Axes.Y, 12);
        Assert.Equal(0.3, k.Nominal * k.Axes.Z, 12);
    }

    [Fact]
    public void Tensor_WithATable_EachComponentFollowsTheTablesChangeFrom25C()
    {
        // k(T) halves from 25 °C to 125 °C; the tensor states 2 / 2 / 1 at 25 °C, so at 125 °C it is 1 / 1 / 0.5.
        var m = new TechMaterial
        {
            Name = "Laminate", ThermalKTensor = [2, 2, 1],
            ThermalKVsTemp = [new() { TempC = 25, Value = 10 }, new() { TempC = 125, Value = 5 }],
        };
        var k = ThermalRunService.Tensor(m, m.ThermalKTensor!, 10);
        Assert.False(k.IsConstant);
        var (at125, slope) = k.OfT!(125);
        Assert.Equal(1.0, at125 * k.Axes.X, 12);
        Assert.Equal(0.5, at125 * k.Axes.Z, 12);
        Assert.Equal(-0.005, slope * k.Axes.Z, 12);                  // 0.5 W/(m·K) lost over 100 K along z
        Assert.Equal(2.0, k.OfT(25).K * k.Axes.X, 12);
    }

    [Theory]
    [InlineData(new[] { 0.8, 0.8, 0.3 }, true, false)]              // with ThermalK: fine
    [InlineData(new[] { 0.8, 0.8, 0.3 }, false, true)]              // no scalar for a wire or block to read
    [InlineData(new[] { 0.8, 0.3 }, true, true)]                    // not three
    [InlineData(new[] { 0.8, 0.8, 0.0 }, true, true)]               // not positive
    public void Check_RefusesAMalformedTensor_OrOneWithNoScalar(double[] tensor, bool statesK, bool refused)
    {
        var m = new TechMaterial { Name = "Laminate", Epsr = 4.4, ThermalK = statesK ? 0.8 : null, ThermalKTensor = tensor };
        var problems = MaterialValidation.Validate([m]).Where(p => p.Message.Contains("ThermalKTensor")).ToList();
        Assert.Equal(refused, problems.Count > 0);
    }
}
