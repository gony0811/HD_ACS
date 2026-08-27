using System.Text.Json.Nodes;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>startWeldInspection actionParameters 빌드·검증 단위 테스트 [VDA5050_INTERFACE §6 계약].</summary>
public class WeldInspectionPayloadTests
{
    // §6 param_schema (db/schema.sql 시드와 동일)
    private const string Schema = """
    {
      "type": "object",
      "required": ["wallId", "seamStart", "seamEnd", "orientation", "patternType"],
      "properties": {
        "wallId":      { "type": "string" },
        "seamStart":   { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "seamEnd":     { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "orientation": { "enum": ["H", "V"] },
        "patternType": { "enum": ["LINEAR"] }
      }
    }
    """;

    // 도면 입력 + T_W_D (tx=9.390, ty=5.980, yaw=0) → seamStart 맵=(12.510,5.980,1.420), seamEnd=(13.310,5.980,1.420)
    private static readonly DrawingTransform GoldenTransform = new(9.390, 5.980, 0);
    private static WeldDrawingData GoldenInput() => new(
        "CT1", 2, "SM",
        new[] { 3.120, 0.0, 1.420 }, new[] { 3.920, 0.0, 1.420 });

    /// <summary>BuildActionParameters 가 §6 계약 필드(wallId·seamStart/End·orientation·patternType)를 맵 좌표로 방출.</summary>
    [Fact]
    public void BuildActionParameters_MatchesGoldenFixture()
    {
        var ap = WeldInspectionPayload.BuildActionParameters(GoldenTransform, GoldenInput());

        Assert.Equal("SM", ap["wallId"]!.GetValue<string>());
        AssertVec(ap, "seamStart", 12.510, 5.980, 1.420);
        AssertVec(ap, "seamEnd", 13.310, 5.980, 1.420);
        Assert.Equal("H", ap["orientation"]!.GetValue<string>());        // 수평(Δz=0)
        Assert.Equal("LINEAR", ap["patternType"]!.GetValue<string>());
    }

    /// <summary>골든 payload 는 §6 스키마 검증을 통과한다.</summary>
    [Fact]
    public void GoldenActionParameters_PassSchema()
    {
        var ap = WeldInspectionPayload.BuildActionParameters(GoldenTransform, GoldenInput());

        var violations = WeldInspectionPayload.ValidateSchema(Schema, ap);

        Assert.Empty(violations);
    }

    /// <summary>필수 필드(seamStart) 누락 시 스키마 검증 실패.</summary>
    [Fact]
    public void MissingRequiredField_FailsSchema()
    {
        var ap = new JsonObject
        {
            ["wallId"] = "SM",
            // seamStart 누락
            ["seamEnd"] = new JsonArray(13.310, 5.980, 1.420),
            ["orientation"] = "H",
            ["patternType"] = "LINEAR",
        };

        var violations = WeldInspectionPayload.ValidateSchema(Schema, ap);

        Assert.NotEmpty(violations);
    }

    /// <summary>orientation 유도 — 높이 변화가 크면 V, 수평이면 H.</summary>
    [Fact]
    public void Orientation_DerivesFromSeamGeometry()
    {
        Assert.Equal("H", WeldInspectionPayload.Orientation(new[] { 0.0, 0, 1 }, new[] { 2.0, 0, 1 }));
        Assert.Equal("V", WeldInspectionPayload.Orientation(new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 3 }));
    }

    /// <summary>맵버전 불일치/부재 시 릴리즈 거부(CalibrationInvalidException), 일치 시 변환 반환 [§2.5].</summary>
    [Fact]
    public void ResolveTransform_RejectsMissingOrMismatchedVersion()
    {
        Assert.Throws<CalibrationInvalidException>(() => WeldInspectionPayload.ResolveTransform(2, null, 0, 0, 0));
        Assert.Throws<CalibrationInvalidException>(() => WeldInspectionPayload.ResolveTransform(2, 1, 5, 3, 0));

        var ok = WeldInspectionPayload.ResolveTransform(2, 2, 9.390, 5.980, 0);
        Assert.Equal(9.390, ok.Tx, 3);
        Assert.Equal(5.980, ok.Ty, 3);
        Assert.Equal(0.0, ok.YawRad, 6);
    }

    private static void AssertVec(JsonObject o, string key, double x, double y, double z)
    {
        var a = o[key]!.AsArray();
        Assert.Equal(x, a[0]!.GetValue<double>(), 3);
        Assert.Equal(y, a[1]!.GetValue<double>(), 3);
        Assert.Equal(z, a[2]!.GetValue<double>(), 3);
    }
}
