using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data.Entities;

namespace HD.Acs.App.Services;

/// <summary>정차 1곳의 지령을 만들 때 검사 작업마다 공통으로 쓰는 값 — 영역·면·좌표 변환·정렬 그룹·리프트.</summary>
/// <param name="FloorZ">그 층 바닥 높이 [m] — 로봇에 보내는 z 는 층 바닥 기준 [VDA §10 N17]</param>
/// <param name="AnchorGroupId">영역 1개 = 정렬 그룹 1개 (`{선창}-L{층}-{면}-{영역}`)</param>
/// <param name="Round">가스검사 — 이 구역(영역)의 검사 횟수(1부터, 없으면 null)</param>
/// <param name="LiftMm">정차의 리프트 높이 [mm] (큐 전개 시점 값 — 발행 때 영역 저장값으로 다시 덮는다)</param>
public sealed record InspectionCommandContext(
    Guid RunId, string TankId, InspectionAreaEntity Area, WallEntity Wall, WallPose Pose,
    DrawingTransform TWd, double FloorZ, string AnchorGroupId, double LiftMm, int? Round = null);

/// <summary>
/// 검사 종류별 지령(VDA 5050 액션) 만들기 — 배차(<see cref="InspectionDispatcher"/>)는 정차·큐·발행만 맡고,
/// "검사 작업 1건을 어떤 액션·파라미터로 보낼지"는 검사 종류마다 이 구현이 정한다.
/// 같은 AMR·VDA 5050 을 쓰고 검사 장비만 다른 제품(용접검사·가스검사)을 한 배차 코드로 돌리기 위한 분리 지점이다.
/// </summary>
public interface IInspectionCommandBuilder
{
    /// <summary>이 구현이 맡는 검사 종류 (Core <see cref="InspectionKinds"/>).</summary>
    string InspectionKind { get; }

    /// <summary>액션 종류 — 액션 카탈로그(ref.action_catalog)에서 param_schema 를 찾는 키이기도 하다.</summary>
    string ActionType { get; }

    /// <summary>
    /// 검사 작업 1건 → 큐에 저장할 액션 `{actionType, taskId, jobRef, position, params}`.
    /// 발행 때 배차가 actionId·attempt·리프트·정차 보정을 덧붙인다. 스키마 위반이면 <see cref="WeldPayloadSchemaException"/>.
    /// </summary>
    JsonObject BuildAction(InspectionCommandContext ctx, AreaTaskEntity task, string? paramSchema);
}

/// <summary>검사 종류 → 지령 만들기 구현. 등록되지 않은 종류는 지령을 만들 수 없다(run 시작 거부의 근거).</summary>
public sealed class InspectionCommandBuilders
{
    private readonly Dictionary<string, IInspectionCommandBuilder> _byKind;

    public InspectionCommandBuilders(params IInspectionCommandBuilder[] builders) =>
        _byKind = builders.ToDictionary(b => b.InspectionKind, StringComparer.OrdinalIgnoreCase);

    /// <summary>값이 없으면(검사 종류가 생기기 전 선창·형상 미등록) 용접검사로 본다.</summary>
    private static string KeyOf(string? kind) => string.IsNullOrWhiteSpace(kind) ? InspectionKinds.Weld : kind.Trim();

    public bool Supports(string? kind) => _byKind.ContainsKey(KeyOf(kind));

    public IInspectionCommandBuilder? Find(string? kind) => _byKind.GetValueOrDefault(KeyOf(kind));
}

/// <summary>
/// 용접검사 지령 — 용접선 1구간 = `startWeldInspection` 액션 1개 [VDA5050_INTERFACE_SPEC §8.1].
/// 좌표·파라미터 구성은 <see cref="WeldInspectionPayload"/>(Core) 정본을 쓴다.
/// </summary>
public sealed class WeldInspectionCommandBuilder(double standoffMm, double workingDistanceMm, ILogger log)
    : IInspectionCommandBuilder
{
    public string InspectionKind => InspectionKinds.Weld;
    public string ActionType => "startWeldInspection";

    public JsonObject BuildAction(InspectionCommandContext ctx, AreaTaskEntity t, string? paramSchema) =>
        BuildAction(ctx, t, paramSchema, null);

    /// <summary>용접선 지령 + 검사 종류별 추가 파라미터(<paramref name="extend"/> — 스키마 검증 전에 params 에 얹는다).</summary>
    public JsonObject BuildAction(InspectionCommandContext ctx, AreaTaskEntity t, string? paramSchema, Action<JsonObject>? extend)
    {
        if (string.IsNullOrWhiteSpace(t.SectionDxfId) || string.IsNullOrWhiteSpace(t.ProfileId))
            throw new InvalidOperationException(
                $"TASK {t.TaskId} 검사 참조 누락: sectionDxfId/profileId를 등록한 뒤 RUN을 시작하세요.");
        var area = ctx.Area;
        var startD = ctx.Pose.LocalToDrawing(t.StartU, t.StartV);
        var endD = ctx.Pose.LocalToDrawing(t.EndU, t.EndV);
        var d = new WeldDrawingData(ctx.TankId, area.Level, area.WallCode, startD, endD, t.StartU, t.StartV, t.EndU, t.EndV);
        var worldPos = WeldInspectionPayload.BuildPosition(ctx.TWd, d, ctx.FloorZ);
        var jobRef = $"JOB-{ctx.AnchorGroupId}-{t.Seq}";
        var taskParams = new JsonObject
        {
            ["seamType"] = t.SeamType,
            ["sectionDxfId"] = t.SectionDxfId,
            ["inspectionProfileId"] = t.ProfileId,
            ["standoffMm"] = standoffMm,
            ["workingDistanceMm"] = workingDistanceMm,
            ["anchorGroupId"] = ctx.AnchorGroupId,
            ["seqInGroup"] = t.Seq,
            ["liftStrokeMm"] = ctx.LiftMm,
            // 용접선 1구간의 영구 식별자 — AMR이 모든 CAPTURE_REQ에 실어 SAIGE productId가 된다
            // [VDA §8.1 / SAIGE §2.5]. attempt는 발행 시점에 발급(PublishStopAsync).
            ["taskId"] = t.TaskId.ToString(),
        };

        // CROSS3/4 교차 가지 끝점 → AMR 면-로컬 mm·규약 순서 [VDA §8.5.1, N13]. ACS 저장은 전개도 프레임이라
        // 반드시 면 법선에서 AMR 프레임을 재구성해 변환한다(좌현·마구리 거울 흡수 — CrossGeometry).
        if (!string.IsNullOrWhiteSpace(t.Points))
        {
            var amrPts = CrossGeometry.BuildAmrPoints(t.SeamType, t.StartU, t.StartV,
                JsonSerializer.Deserialize<double[][]>(t.Points)!, ctx.Pose.U, ctx.Pose.V,
                JsonSerializer.Deserialize<double[]>(ctx.Wall.Normal)!);
            if (amrPts is not null)
            {
                var arr = new JsonArray();
                foreach (var p in amrPts) arr.Add(new JsonArray((JsonNode)p[0], p[1]));
                taskParams["points"] = arr;
            }
        }

        extend?.Invoke(taskParams);

        var actionParams = WeldInspectionPayload.BuildActionParameters(jobRef, worldPos, taskParams);
        var violations = WeldInspectionPayload.ValidateSchema(paramSchema, actionParams);
        if (violations.Count > 0)
        {
            log.LogWarning("Run {Run}: {Job} payload 스키마 위반 — run 시작 중단: {V}",
                ctx.RunId, jobRef, string.Join("; ", violations));
            throw new WeldPayloadSchemaException(violations);
        }

        return new JsonObject
        {
            ["actionType"] = ActionType,
            ["taskId"] = t.TaskId.ToString(),
            ["jobRef"] = jobRef,
            ["position"] = worldPos,
            ["params"] = taskParams.DeepClone(),
        };
    }
}

/// <summary>
/// 가스검사(포밍가스 기밀 검사) 지령 — HD_AMR 은 용접선을 따라가는 같은 시퀀스에서 검사 센서만 가스 감지기로 바꾼다
/// (InspectionSensor:Mode = GasDetector). 그래서 액션은 용접검사와 같은 startWeldInspection 이고,
/// 그 구역(영역)의 검사 횟수를 params.round(1 이상)로 얹는다 — 없으면 싣지 않는다 [HD_AMR docs/GAS_DETECTOR_XL3000FLEX.md §5.1].
/// 결과는 액션이 끝날 때 resultDescription 에 SAIGE 결과 본문(JSON)으로 온다(§5.2 — ACS 의 SAIGE 전송은 별도 단계).
/// </summary>
public sealed class GasInspectionCommandBuilder(WeldInspectionCommandBuilder weld) : IInspectionCommandBuilder
{
    public string InspectionKind => InspectionKinds.Gas;
    public string ActionType => weld.ActionType;

    public JsonObject BuildAction(InspectionCommandContext ctx, AreaTaskEntity task, string? paramSchema) =>
        weld.BuildAction(ctx, task, paramSchema, ctx.Round is int round ? p => p["round"] = round : null);
}
