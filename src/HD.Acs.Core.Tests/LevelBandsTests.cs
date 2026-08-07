using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>층 자동 유도 — 면×층 도달 밴드 단위 테스트 [SPEC v3.1 §5-A].</summary>
public class LevelBandsTests
{
    // 예시 선창(TankGeometryTests와 동일): B=16, W_ceil=12, H=13. level_z=[0,3.2,6.4,9.6] → 4층.
    private static TankGeometry Sample() => new(
        L: 30, WFloor: 10, ThetaLow: Math.PI / 4, HLow: 3,
        HWall: 8, ThetaUp: Math.PI / 4, HUp: 2,
        LevelZ: new[] { 0.0, 3.2, 6.4, 9.6 });

    private static Dictionary<string, GeneratedWall> Walls() => Sample().GenerateWalls().ToDictionary(w => w.WallCode);

    private static (double zLo, double zHi) AreaZ(GeneratedWall w, double vMin, double vMax) =>
        LevelBands.AreaZRange(w.Pose.Origin[2], w.Pose.V[2], vMin, vMax);

    // ── 밴드 구성 ──────────────────────────────────────────
    [Fact]
    public void Compute_FourBands_TopClosedAtH()
    {
        var bands = Sample().LevelBandList();
        Assert.Equal(4, bands.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, bands.Select(b => b.Level));
        Assert.Equal(0.0, bands[0].ZMin, 6);
        Assert.Equal(3.2, bands[0].ZMax, 6);
        Assert.Equal(9.6, bands[3].ZMin, 6);
        Assert.Equal(13.0, bands[3].ZMax, 6);   // 최상층 상한 = H(천장 포함)
    }

    [Fact]
    public void Compute_ReachZ_NarrowsBands()
    {
        // reachMin=0.5, reachMax=2.0 → 각 층 밴드 = [z+0.5, min(next, z+2.0)]
        var bands = LevelBands.Compute(new[] { 0.0, 3.2, 6.4, 9.6 }, 0.5, 2.0, 13.0);
        Assert.Equal(0.5, bands[0].ZMin, 6);
        Assert.Equal(2.0, bands[0].ZMax, 6);           // min(3.2, 0+2.0)=2.0 → 축소(3.2 아님)
        Assert.Equal(9.6 + 0.5, bands[3].ZMin, 6);
        Assert.Equal(Math.Min(13.0, 9.6 + 2.0), bands[3].ZMax, 6);   // min(13, 11.6)=11.6
    }

    // ── 면별 층 유도 ──────────────────────────────────────
    [Fact]
    public void Floor_DerivesLevel1_Only()
    {
        var bands = Sample().LevelBandList();
        var (zLo, zHi) = AreaZ(Walls()["B"], 1, 5);   // 바닥: z 상수 0
        Assert.Equal(1, LevelBands.Derive(zLo, zHi, bands, out _));
    }

    [Fact]
    public void Ceiling_DerivesTopLevel_Only()
    {
        var bands = Sample().LevelBandList();
        var (zLo, zHi) = AreaZ(Walls()["T"], 2, 8);   // 천장: z 상수 13=H
        Assert.Equal(4, LevelBands.Derive(zLo, zHi, bands, out _));
    }

    [Fact]
    public void VerticalWall_DerivesLevel_ByVBand()
    {
        var bands = Sample().LevelBandList();
        var sm = Walls()["SM"];   // origin z=3, V=+z, vLen=8 → z(v)=3+v
        // v∈[0.5,2.5] → z∈[3.5,5.5] ⊂ L2 밴드 [3.2,6.4)
        var (zLo, zHi) = AreaZ(sm, 0.5, 2.5);
        Assert.Equal(2, LevelBands.Derive(zLo, zHi, bands, out _));
        // v∈[3.5,6.5] → z∈[6.5,9.5] ⊂ L3 밴드 [6.4,9.6)
        (zLo, zHi) = AreaZ(sm, 3.5, 6.5);
        Assert.Equal(3, LevelBands.Derive(zLo, zHi, bands, out _));
    }

    [Fact]
    public void UpperChamfer_DerivesTopLevel()
    {
        var bands = Sample().LevelBandList();
        var su = Walls()["SU"];   // origin z=11, z(v)=11+v·0.707, vLen≈2.828 → z∈[11,13]
        var (zLo, zHi) = AreaZ(su, 0, su.VLen);
        Assert.Equal(4, LevelBands.Derive(zLo, zHi, bands, out _));
    }

    // ── 실패 경로 ─────────────────────────────────────────
    [Fact]
    public void BoundaryStraddle_Fails()
    {
        var bands = Sample().LevelBandList();
        var sm = Walls()["SM"];   // z(v)=3+v
        // v∈[0.0,1.0] → z∈[3.0,4.0], 경계 3.2를 가로지름 → 걸침
        var (zLo, zHi) = AreaZ(sm, 0.0, 1.0);
        Assert.Null(LevelBands.Derive(zLo, zHi, bands, out var reason));
        Assert.Contains("경계", reason);
    }

    [Fact]
    public void Unreachable_Fails_InReachGap()
    {
        // reachMax=1.0 → 각 밴드 [z, z+1.0]. 층 사이 (z+1.0, next)는 도달 불가 갭.
        var bands = LevelBands.Compute(new[] { 0.0, 3.2, 6.4, 9.6 }, null, 1.0, 13.0);
        // z∈[1.5, 2.5]는 L1 밴드 [0,1.0] 밖 · 갭 → 도달 불가
        Assert.Null(LevelBands.Derive(1.5, 2.5, bands, out var reason));
        Assert.Contains("도달 불가", reason);
    }

    [Fact]
    public void EpsilonBoundary_ContainedWithinTolerance()
    {
        var bands = Sample().LevelBandList();
        // L2 밴드 [3.2,6.4). 상한을 4mm 초과(허용 5mm 내) → 여전히 L2로 포함
        Assert.Equal(2, LevelBands.Derive(3.2, 6.4 + 0.004, bands, out _));
    }

    // ── 도달 v구간 ────────────────────────────────────────
    [Fact]
    public void ReachableVBand_VerticalWall_ClipsToBand()
    {
        var bands = Sample().LevelBandList();
        var sm = Walls()["SM"];   // z(v)=3+v, vLen=8
        // L2 밴드 [3.2,6.4) → v = z-3 → [0.2, 3.4]
        var band = bands.First(b => b.Level == 2);
        var vb = LevelBands.ReachableVBand(sm.Pose.Origin[2], sm.Pose.V[2], sm.VLen, band);
        Assert.NotNull(vb);
        Assert.Equal(0.2, vb!.Value.VLo, 6);
        Assert.Equal(3.4, vb.Value.VHi, 6);
    }

    [Fact]
    public void ReachableVBand_Floor_WholeFaceOrNull()
    {
        var bands = Sample().LevelBandList();
        var b = Walls()["B"];   // 수평면 z=0
        // L1 밴드 포함 → 전체 면
        var vb1 = LevelBands.ReachableVBand(b.Pose.Origin[2], b.Pose.V[2], b.VLen, bands.First(x => x.Level == 1));
        Assert.NotNull(vb1);
        Assert.Equal(0.0, vb1!.Value.VLo, 6);
        Assert.Equal(b.VLen, vb1.Value.VHi, 6);
        // L2 밴드(3.2~) 밖 → null
        Assert.Null(LevelBands.ReachableVBand(b.Pose.Origin[2], b.Pose.V[2], b.VLen, bands.First(x => x.Level == 2)));
    }
}
