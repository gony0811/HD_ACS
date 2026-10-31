using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>정차 온보드 보정 허용 범위 params.stationAdjust [VDA §4.4.2 / N23 초안].</summary>
public class StationAdjustTests
{
    private static readonly StationAdjustLimits Limits = new(0, 400, 200, 5);

    [Fact]
    public void Disabled_ReturnsNull()
        => Assert.Null(StationAdjustLimits.Normalize(false, 0, 400, 200, 5));

    [Fact]
    public void AllZero_TreatedAsDisabled()
        => Assert.Null(StationAdjustLimits.Normalize(true, 0, 0, 0, 0));

    [Theory]
    [InlineData(-1, 400, 200, 5)]
    [InlineData(0, -1, 200, 5)]
    [InlineData(0, 400, double.NaN, 5)]
    [InlineData(0, 400, 200, -0.1)]
    public void NegativeOrNaN_Rejected(double f, double b, double l, double t)
        => Assert.Throws<ArgumentOutOfRangeException>(() => StationAdjustLimits.Normalize(true, f, b, l, t));

    /// <summary>영역별 후진 상한 덮어쓰기 — null=전역값 유지, 값=후진만 교체(다른 한계 유지), 0=그 영역 후진 금지.</summary>
    [Theory]
    [InlineData(null, 400)]
    [InlineData(150.0, 150)]
    [InlineData(0.0, 0)]
    public void ForArea_OverridesBackwardOnly(double? area, double expected)
    {
        var l = Limits.ForArea(area);
        Assert.Equal(expected, l.MaxBackwardMm);
        Assert.Equal((Limits.MaxForwardMm, Limits.MaxLateralMm, Limits.MaxThetaDeg), (l.MaxForwardMm, l.MaxLateralMm, l.MaxThetaDeg));
        Assert.Equal(expected, StationAdjustLimits.ToParam(l, 1.0)!["maxBackwardMm"]!.GetValue<double>());   // 0 이어도 필드는 실림
    }

    /// <summary>바닥·천장처럼 정차각이 없는 정차는 정면 프레임이 없어 보정 범위를 싣지 않는다.</summary>
    [Fact]
    public void NoStationTheta_OmitsField()
        => Assert.Null(StationAdjustLimits.ToParam(Limits, null));

    [Fact]
    public void ToParam_CarriesFourLimits()
    {
        var p = StationAdjustLimits.ToParam(Limits, 1.571)!;
        Assert.Equal(0, p["maxForwardMm"]!.GetValue<double>());
        Assert.Equal(400, p["maxBackwardMm"]!.GetValue<double>());
        Assert.Equal(200, p["maxLateralMm"]!.GetValue<double>());
        Assert.Equal(5, p["maxThetaDeg"]!.GetValue<double>());
    }

    /// <summary>정본 param_schema가 새 필드를 받아들이고, 필드가 없어도(꺼짐) 통과한다 — 하위 호환.</summary>
    [Fact]
    public void CanonicalSchema_AcceptsWithAndWithoutStationAdjust()
    {
        var with = ActionParams(StationAdjustLimits.ToParam(Limits, 0.0));
        var without = ActionParams(null);
        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, with));
        Assert.Empty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, without));
    }

    /// <summary>필드 일부만 오면(구 3필드 초안 등) 거부 — AMR이 빠진 한계를 0으로 오해하지 않게.</summary>
    [Fact]
    public void CanonicalSchema_RejectsMissingLimit()
    {
        var partial = new JsonObject { ["maxNormalMm"] = 300, ["maxLateralMm"] = 200, ["maxThetaDeg"] = 5 };
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, ActionParams(partial)));
    }

    [Fact]
    public void CanonicalSchema_RejectsNegativeLimit()
    {
        var bad = new JsonObject { ["maxForwardMm"] = 0, ["maxBackwardMm"] = -10, ["maxLateralMm"] = 200, ["maxThetaDeg"] = 5 };
        Assert.NotEmpty(WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema, ActionParams(bad)));
    }

    private static JsonObject ActionParams(JsonObject? stationAdjust)
    {
        var prm = new JsonObject
        {
            ["seamType"] = "LINE", ["sectionDxfId"] = "S-1", ["inspectionProfileId"] = "P-1",
            ["standoffMm"] = 400, ["anchorGroupId"] = "CT1-L1-P-A1", ["seqInGroup"] = 1,
            ["taskId"] = Guid.NewGuid().ToString(), ["attempt"] = 1,
        };
        if (stationAdjust is not null) prm["stationAdjust"] = stationAdjust;
        var position = new JsonObject
        {
            ["seamStartW"] = new JsonArray(1.0, 2.0, 0.5),
            ["seamEndW"] = new JsonArray(2.0, 2.0, 0.5),
            ["drawingPos"] = new JsonObject
            {
                ["tank"] = "CT1", ["level"] = 1, ["wall_code"] = "P",
                ["u"] = 0.1, ["v"] = 0.2, ["x"] = 1.0, ["y"] = 2.0, ["z"] = 0.5,
            },
        };
        return WeldInspectionPayload.BuildActionParameters("JOB-1", position, prm);
    }
}
