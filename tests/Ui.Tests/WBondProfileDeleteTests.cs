using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Delete in the Wire Profile view: a vertex or segment comes off every wire of its group with the same point count, in
/// one undo entry, and a sibling with a different count is left alone — there the same index is a different point.
/// </summary>
public sealed class WBondProfileDeleteTests
{
    /// <summary>One group: wires 0 and 1 have seven points, wire 2 has five.</summary>
    private static WBondViewModel Group()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var array = new WireArray { Name = "G1" };
        foreach (var (y, points) in new[] { (0.0, 7), (6.0, 7), (12.0, 5) })
            array.Wires.Add(LoopShape.CreateSeedWire(Point3.Mils(0, y, 4), Point3.Mils(30, y, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm, points));
        var design = new WBondDesign();
        design.Arrays.Add(array);
        return new WBondViewModel(design);
    }

    private static int[] Counts(WBondViewModel vm) => [.. vm.Design.AllWires().Select(w => w.Points.Count)];

    [Fact]
    public void DeletingAVertex_TakesItOffEverySiblingWithTheSameCount_AndLeavesTheOthersAlone()
    {
        var vm = Group();
        var refusals = new List<string>();
        vm.EditRefused += refusals.Add;
        var footOfTheShortWire = vm.Design.AllWires().ElementAt(2).Points[4];

        vm.Selection = new WireSelection { Points = [new PointRef(0, 4)] };
        Assert.Equal(2, vm.DeleteProfileSelection());

        Assert.Equal([6, 6, 5], Counts(vm));
        Assert.Equal(footOfTheShortWire, vm.Design.AllWires().ElementAt(2).Points[^1]);   // its foot did not move
        Assert.Contains(refusals, r => r.StartsWith("1 wire in the group has a different number of points"));

        vm.Undo();
        Assert.Equal([7, 7, 5], Counts(vm));                                               // one entry for the group
        vm.Redo();
        Assert.Equal([6, 6, 5], Counts(vm));
    }

    [Fact]
    public void TheMenusDeleteSegment_FollowsTheSameRule()
    {
        var vm = Group();

        Assert.True(vm.DeleteProfileSegment(2, 1));      // picked on the five-point wire: only it has that shape

        Assert.Equal([7, 7, 4], Counts(vm));
        vm.Undo();
        Assert.Equal([7, 7, 5], Counts(vm));
    }
}
