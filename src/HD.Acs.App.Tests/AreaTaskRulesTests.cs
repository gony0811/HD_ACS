using HD.Acs.App.Services;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>검사 작업 등록(POST)·수정(PUT) 공통 검증 규칙.</summary>
public class AreaTaskRulesTests
{
    // 회전 사각형(축정렬 아님) — bbox가 아니라 실제 폴리곤 내부 판정이어야 한다
    private const string Diamond = "[[5,0],[10,5],[5,10],[0,5]]";

    [Theory]
    [InlineData("LINE")] [InlineData("CROSS3")] [InlineData("CROSS4")] [InlineData("CORNER2")] [InlineData("CORNER3")]
    [InlineData("line")]          // 대소문자 무시 수용(저장 시 대문자 정규화)
    [InlineData(null)]            // 미지정 = 검사 생략 (POST 기본 LINE / PUT 기존값 유지)
    public void ValidSeamTypes_Pass(string? seamType) =>
        Assert.Null(AreaTaskRules.Validate(seamType, Diamond, 4, 5, 6, 5));

    [Theory]
    [InlineData("POLYLINE")] [InlineData("CROSS")] [InlineData("CORNER")] [InlineData("")]
    public void UnknownSeamTypes_Rejected(string seamType) =>
        Assert.Contains("지원하지 않습니다", AreaTaskRules.Validate(seamType, Diamond, 4, 5, 6, 5));

    [Theory]
    [InlineData(1, 1, 6, 5)]      // 시작점이 bbox 안이지만 마름모 밖
    [InlineData(4, 5, 9.5, 9.5)]  // 끝점이 밖
    public void EndpointsOutsidePolygon_Rejected(double su, double sv, double eu, double ev) =>
        Assert.Contains("내부가 아닙니다", AreaTaskRules.Validate("LINE", Diamond, su, sv, eu, ev));

    [Fact]
    public void NonFiniteCoordinates_Rejected() =>
        Assert.Contains("유한한 숫자", AreaTaskRules.Validate("LINE", Diamond, double.NaN, 5, 6, 5));
}
