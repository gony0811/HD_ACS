using System.Text.Json.Nodes;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>WP-3 startWeldInspection payload 빌드·검증 단위 테스트 [SPEC §4.1/§4.2/부록 A].</summary>
public class WeldInspectionPayloadTests
{
    // SPEC §4.1 param_schema (db/schema.sql 시드와 동일)
    private const string Schema = """
    {
      "type": "object",
      "required": ["jobRef", "position", "params"],
      "properties": {
        "jobRef": { "type": "string" },
        "position": {
          "type": "object",
          "required": ["seamStartW", "seamEndW", "drawingPos"],
          "properties": {
            "seamStartW":  { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
            "seamEndW":    { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
            "drawingPos": {
              "type": "object",
              "required": ["tank", "level", "wall_code", "x", "y", "z"],
              "properties": {
                "tank": { "type": "string" }, "level": { "type": "integer" },
                "wall_code": { "type": "string" },
                "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" }
              }
            }
          }
        },
        "params": {
          "type": "object",
          "required": ["seamType", "sectionDxfId", "inspectionProfileId", "standoffMm", "anchorGroupId", "seqInGroup"],
          "properties": {
            "seamType":            { "enum": ["LINE", "POLYLINE"] },
            "points":              { "type": "array" },
            "sectionDxfId":        { "type": "string" },
            "inspectionProfileId": { "type": "string" },
            "standoffMm":          { "type": "number" },
            "workingDistanceMm":   { "type": "number" },
            "anchorGroupId":       { "type": "string" },
            "seqInGroup":          { "type": "integer", "minimum": 1 }
          }
        }
      }
    }
    """;

    // 부록 A 를 복원하는 도면 입력 + T_W_D (tx=9.390, ty=5.980, yaw=0)
    private static readonly DrawingTransform GoldenTransform = new(9.390, 5.980, 0);
    private static WeldDrawingData GoldenInput() => new(
        "CT1", 2, "W03",
        new[] { 3.120, 0.0, 1.420 }, new[] { 3.920, 0.0, 1.420 });

    private static JsonObject GoldenParams() => new()
    {
        ["seamType"] = "LINE",
        ["sectionDxfId"] = "DXF-CORR-T12",
        ["inspectionProfileId"] = "INSPECT-STD-01",
        ["standoffMm"] = 400,
        ["workingDistanceMm"] = 400,
        ["anchorGroupId"] = "CT1-L2-W03-ST04",
        ["seqInGroup"] = 2,
    };

    /// <summary>BuildPosition 이 부록 A 의 seamStartW/seamEndW/drawingPos 와 필드 단위 일치. [SPEC v2: 법선 제거]</summary>
    [Fact]
    public void BuildPosition_MatchesGoldenFixture()
    {
        var pos = WeldInspectionPayload.BuildPosition(GoldenTransform, GoldenInput());

        AssertVec(pos, "seamStartW", 12.510, 5.980, 1.420);
        AssertVec(pos, "seamEndW", 13.310, 5.980, 1.420);
        Assert.Null(pos["wallNormalW"]);   // [SPEC v2] 법선 계약 제거 — 방출되지 않음

        var dp = pos["drawingPos"]!.AsObject();
        Assert.Equal("CT1", dp["tank"]!.GetValue<string>());
        Assert.Equal(2, dp["level"]!.GetValue<int>());
        Assert.Equal("W03", dp["wall_code"]!.GetValue<string>());
        Assert.Equal(3.120, dp["x"]!.GetValue<double>(), 3);
        Assert.Equal(0.0, dp["y"]!.GetValue<double>(), 3);
        Assert.Equal(1.420, dp["z"]!.GetValue<double>(), 3);
    }

    /// <summary>골든 payload 는 §4.1 스키마 검증을 통과한다.</summary>
    [Fact]
    public void GoldenActionParameters_PassSchema()
    {
        var pos = WeldInspectionPayload.BuildPosition(GoldenTransform, GoldenInput());
        var ap = WeldInspectionPayload.BuildActionParameters("JOB-CT1-L2-W03-S07-2", pos, GoldenParams());

        var violations = WeldInspectionPayload.ValidateSchema(Schema, ap);

        Assert.Empty(violations);
    }

    /// <summary>필수 필드(seamEndW) 누락 시 스키마 검증 실패.</summary>
    [Fact]
    public void MissingRequiredField_FailsSchema()
    {
        var pos = new JsonObject
        {
            ["seamStartW"] = new JsonArray(1, 2, 3),
            // seamEndW 누락
            ["drawingPos"] = new JsonObject
            {
                ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = "W03", ["x"] = 0, ["y"] = 0, ["z"] = 0
            }
        };
        var ap = WeldInspectionPayload.BuildActionParameters("JOB", pos, GoldenParams());

        var violations = WeldInspectionPayload.ValidateSchema(Schema, ap);

        Assert.NotEmpty(violations);
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
