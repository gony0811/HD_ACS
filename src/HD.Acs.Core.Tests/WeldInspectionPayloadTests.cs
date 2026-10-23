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
              "required": ["tank", "level", "wall_code", "u", "v", "x", "y", "z"],
              "properties": {
                "tank": { "type": "string" }, "level": { "type": "integer" },
                "wall_code": { "type": "string" },
                "u": { "type": "number" }, "v": { "type": "number" },
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
    // L2 바닥 높이 level_z[1] — seam z는 이만큼 빼서 층 바닥 기준으로 발행 [VDA §10 N17]
    private static readonly double[] GoldenLevelZ = { 0.0, 3.2, 6.4, 9.6 };
    private static readonly double GoldenFloorZ = WeldInspectionPayload.FloorZ(GoldenLevelZ, 2);
    private static WeldDrawingData GoldenInput() => new(
        "CT1", 2, "W03",
        new[] { 3.120, 0.0, 4.620 }, new[] { 3.920, 0.0, 4.620 },   // 도면 전역 z (L2 바닥 3.2 + 1.42)
        U: 3.120, V: 1.420);   // 벽면-로컬 u,v [VDA5050_INTERFACE_SPEC §8.4 골든]

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
        var pos = WeldInspectionPayload.BuildPosition(GoldenTransform, GoldenInput(), GoldenFloorZ);

        AssertVec(pos, "seamStartW", 12.510, 5.980, 1.420);
        AssertVec(pos, "seamEndW", 13.310, 5.980, 1.420);
        Assert.Null(pos["wallNormalW"]);   // [SPEC v2] 법선 계약 제거 — 방출되지 않음

        var dp = pos["drawingPos"]!.AsObject();
        Assert.Equal("CT1", dp["tank"]!.GetValue<string>());
        Assert.Equal(2, dp["level"]!.GetValue<int>());
        Assert.Equal("W03", dp["wall_code"]!.GetValue<string>());
        Assert.Equal(3.120, dp["u"]!.GetValue<double>(), 3);
        Assert.Equal(1.420, dp["v"]!.GetValue<double>(), 3);
        Assert.Equal(3.120, dp["x"]!.GetValue<double>(), 3);
        Assert.Equal(0.0, dp["y"]!.GetValue<double>(), 3);
        Assert.Equal(4.620, dp["z"]!.GetValue<double>(), 3);   // drawingPos는 도면 echo — 전역 z 유지
    }

    /// <summary>골든 payload 는 §4.1 스키마 검증을 통과한다.</summary>
    [Fact]
    public void GoldenActionParameters_PassSchema()
    {
        var pos = WeldInspectionPayload.BuildPosition(GoldenTransform, GoldenInput(), GoldenFloorZ);
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
                ["tank"] = "CT1", ["level"] = 2, ["wall_code"] = "W03",
                ["u"] = 0, ["v"] = 0, ["x"] = 0, ["y"] = 0, ["z"] = 0
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

    /// <summary>
    /// seam z = 도면 전역 z − 층 바닥 [VDA §10 N17]. L1은 바닥 0이라 값이 같고(그래서 단층 E2E에선 안 드러났다),
    /// L4는 9.6 m를 빼야 한다 — 실기 IK 실패(접근점 z ≈ 9.7 m)의 재현 방지. x,y는 바닥 기준과 무관.
    /// </summary>
    [Theory]
    [InlineData(1, 1.420, 1.420)]
    [InlineData(3, 7.820, 1.420)]
    [InlineData(4, 11.020, 1.420)]
    public void BuildPosition_SeamZ_IsFloorRelative(int level, double drawingZ, double expectedZ)
    {
        var d = new WeldDrawingData("CT1", level, "PM",
            new[] { 3.120, 0.0, drawingZ }, new[] { 3.920, 0.0, drawingZ + 0.5 }, U: 3.120, V: 1.0);
        var pos = WeldInspectionPayload.BuildPosition(GoldenTransform, d, WeldInspectionPayload.FloorZ(GoldenLevelZ, level));

        AssertVec(pos, "seamStartW", 12.510, 5.980, expectedZ);
        AssertVec(pos, "seamEndW", 13.310, 5.980, expectedZ + 0.5);   // Δz 보존 — 방향 유도 무영향
        Assert.Equal(drawingZ, pos["drawingPos"]!["z"]!.GetValue<double>(), 6);
    }

    /// <summary>층 정의 없음·범위 밖은 0으로 폴백하지 않고 거부 — 조용한 z 오차 금지.</summary>
    [Fact]
    public void FloorZ_RejectsMissingOrOutOfRangeLevel()
    {
        Assert.Throws<InvalidOperationException>(() => WeldInspectionPayload.FloorZ(null, 1));
        Assert.Throws<InvalidOperationException>(() => WeldInspectionPayload.FloorZ(GoldenLevelZ, 0));
        Assert.Throws<InvalidOperationException>(() => WeldInspectionPayload.FloorZ(GoldenLevelZ, 5));
        Assert.Equal(6.4, WeldInspectionPayload.FloorZ(GoldenLevelZ, 3));
    }

    private static void AssertVec(JsonObject o, string key, double x, double y, double z)
    {
        var a = o[key]!.AsArray();
        Assert.Equal(x, a[0]!.GetValue<double>(), 3);
        Assert.Equal(y, a[1]!.GetValue<double>(), 3);
        Assert.Equal(z, a[2]!.GetValue<double>(), 3);
    }
}
