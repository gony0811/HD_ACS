using HD.Acs.App.Services;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>검사 작업 등록(POST)·수정(PUT) 공통 검증 규칙.</summary>
public class AreaTaskRulesTests
{
    // 회전 사각형(축정렬 아님) — bbox가 아니라 실제 폴리곤 내부 판정이어야 한다
    private const string Diamond = "[[5,0],[10,5],[5,10],[0,5]]";

    [Theory]
    [InlineData("LINE")]
    [InlineData("CROSS3_R0")] [InlineData("CROSS3_R90")] [InlineData("CROSS3_R180")] [InlineData("CROSS3_R270")]
    [InlineData("CROSS4")] [InlineData("CORNER2")] [InlineData("CORNER3")]
    [InlineData("line")]          // 대소문자 무시 수용(저장 시 canonical 정규화)
    [InlineData("CROSS")]         // legacy 별칭 → CROSS4 (§8.5.1 전환 유예)
    [InlineData("CORNER")]        // legacy 별칭 → CORNER3
    [InlineData("CROSS3")]        // legacy bare → CROSS3_R0
    [InlineData(null)]            // 미지정 = 검사 생략 (POST 기본 LINE / PUT 기존값 유지)
    public void ValidSeamTypes_Pass(string? seamType) =>
        Assert.Null(AreaTaskRules.Validate(seamType, Diamond, 4, 5, 6, 5));

    [Theory]
    [InlineData("POLYLINE")] [InlineData("CROSS3_R45")] [InlineData("TRIANGLE")] [InlineData("")]
    public void UnknownSeamTypes_Rejected(string seamType) =>
        Assert.Contains("지원하지 않습니다", AreaTaskRules.Validate(seamType, Diamond, 4, 5, 6, 5));

    [Theory]
    [InlineData("CROSS", "CROSS4")]
    [InlineData("CORNER", "CORNER3")]
    [InlineData("CROSS3", "CROSS3_R0")]
    [InlineData("cross3_r90", "CROSS3_R90")]
    [InlineData("line", "LINE")]
    [InlineData(null, "LINE")]
    public void Normalize_MapsLegacyToCanonical(string? input, string expected) =>
        Assert.Equal(expected, AreaTaskRules.Normalize(input));

    [Theory]
    [InlineData(1, 1, 6, 5)]      // 시작점이 bbox 안이지만 마름모 밖
    [InlineData(4, 5, 9.5, 9.5)]  // 끝점이 밖
    public void EndpointsOutsidePolygon_Rejected(double su, double sv, double eu, double ev) =>
        Assert.Contains("내부가 아닙니다", AreaTaskRules.Validate("LINE", Diamond, su, sv, eu, ev));

    [Fact]
    public void NonFiniteCoordinates_Rejected() =>
        Assert.Contains("유한한 숫자", AreaTaskRules.Validate("LINE", Diamond, double.NaN, 5, 6, 5));

    // ── ValidatePoints: CROSS 가지 끝점 [VDA §8.5.1] ──
    private static readonly double[][] In4 = { new[] { 6.0, 5 }, new[] { 5.0, 6 }, new[] { 4.0, 5 }, new[] { 5.0, 4 } };
    private static readonly double[][] In3 = { new[] { 6.0, 5 }, new[] { 5.0, 6 }, new[] { 5.0, 4 } };

    [Fact] public void Points_Cross4_FourInside_Pass() => Assert.Null(AreaTaskRules.ValidatePoints("CROSS4", Diamond, In4));
    [Fact] public void Points_Cross3_ThreeInside_Pass() => Assert.Null(AreaTaskRules.ValidatePoints("CROSS3_R90", Diamond, In3));
    [Fact] public void Points_LegacyCross_Normalized_Pass() => Assert.Null(AreaTaskRules.ValidatePoints("CROSS", Diamond, In4));
    [Fact] public void Points_Null_Allowed() => Assert.Null(AreaTaskRules.ValidatePoints("CROSS4", Diamond, null));

    [Fact] public void Points_Cross4_WrongCount_Rejected() =>
        Assert.Contains("4개가 필요", AreaTaskRules.ValidatePoints("CROSS4", Diamond, In3));
    [Fact] public void Points_Cross3_WrongCount_Rejected() =>
        Assert.Contains("3개가 필요", AreaTaskRules.ValidatePoints("CROSS3_R0", Diamond, In4));
    [Fact] public void Points_OnLine_Rejected() =>
        Assert.Contains("CROSS3/CROSS4 에만", AreaTaskRules.ValidatePoints("LINE", Diamond, In4));
    [Fact] public void Points_Outside_Rejected() =>
        Assert.Contains("내부가 아닙니다", AreaTaskRules.ValidatePoints("CROSS4", Diamond,
            new[] { new[] { 9.9, 9.9 }, new[] { 5.0, 6 }, new[] { 4.0, 5 }, new[] { 5.0, 4 } }));
}
