using HD.Acs.App.Services;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>기동 시드 멱등성 — jsonb 가 키 순서·공백을 바꿔 돌려줘도 "같음"으로 판정해야 부팅마다 재기록하지 않는다.</summary>
public class ActionCatalogSeedTests
{
    [Fact]
    public void SameJson_IgnoresKeyOrderAndWhitespace()
    {
        // PostgreSQL jsonb 재정렬 흉내: 키 순서 뒤집힘 + 공백 재포맷
        const string stored = """{"type": "object", "properties": {"b": {"maximum": 255, "minimum": 1, "type": "integer"}, "a": {"enum": ["LINE", "CROSS3"]}}}""";
        const string canonical = """{ "type":"object","properties":{ "a":{"enum":["LINE","CROSS3"]}, "b":{"type":"integer","minimum":1,"maximum":255} } }""";
        Assert.True(ActionCatalogSeed.SameJson(stored, canonical));
    }

    [Theory]
    [InlineData("""{"a":{"enum":["LINE","CROSS3"]}}""", """{"a":{"enum":["CROSS3","LINE"]}}""")]   // 배열 순서는 의미가 있다
    [InlineData("""{"a":1}""", """{"a":1,"b":2}""")]
    [InlineData("""{"a":1}""", "not json")]
    [InlineData("""{"a":1}""", null)]
    public void SameJson_DetectsRealDifferences(string a, string? b) => Assert.False(ActionCatalogSeed.SameJson(a, b));

    /// <summary>정본 스키마는 자기 자신과 같고, 1.6 필드(taskId·attempt)를 담고 있다.</summary>
    [Fact]
    public void CanonicalSchema_ContainsTaskIdAndAttempt()
    {
        Assert.True(ActionCatalogSeed.SameJson(ActionCatalogSeed.StartWeldInspectionParamSchema, ActionCatalogSeed.StartWeldInspectionParamSchema));
        Assert.Contains("\"taskId\"", ActionCatalogSeed.StartWeldInspectionParamSchema);
        Assert.Contains("\"attempt\"", ActionCatalogSeed.StartWeldInspectionParamSchema);
    }
}
