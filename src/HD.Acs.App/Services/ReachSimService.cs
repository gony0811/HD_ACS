using System.Text.Json;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 리치·간섭 시뮬레이션 조회 — 영역 정차(배차와 같은 <see cref="InspectionDispatcher.StationDrawingOf"/>)에서 FR5-WML 이
/// 용접선에 세 자세(② 400 → 정렬 준비 300 → 초점 0)로 접근할 때 관절 위치와 탱크 면 간섭을 계산한다.
/// 계산은 <see cref="ReachSimulator"/>(Core)가 하고, 여기서는 DB 의 선창·면·영역·작업을 도면 좌표로 엮는다.
/// 조회 전용 — 코봇·DB 를 바꾸지 않는다.
/// </summary>
public sealed partial class ReachSimService
{
    /// <summary>리프트 조사 간격 [mm] — 0~스트로크 최대.</summary>
    public const double StrokeStepMm = 100;

    private readonly AcsDbContext _db;
    private readonly InspectionDispatcher _dispatcher;
    private ReachRobotModel _baseModel;
    private readonly string? _scanProfileName;
    private readonly IConfiguration? _config;
    /// <summary>검사 종류 → (모델, 프로필 찾기, 프로필 출처) — 요청(스코프)마다 종류별 한 번만 만든다.</summary>
    private readonly Dictionary<string, (ReachRobotModel Model, Func<string, string, string, SeamScanProfile?> ProfileOf, string Source)> _resolved = new();
    private ReachRobotModel _model;
    /// <summary>면 코드 → 차체 중심과 검사 면 사이 최소 거리 [m] (<c>Acs:Reach:WallMinDistMm</c>). 정차 최적화·배차 직전 정차 선택·분리 제안이 쓴다.</summary>
    private readonly IReadOnlyDictionary<string, double> _wallMinDistM;

    /// <param name="config">
    /// HD_AMR 실기 값과 맞추는 설정 — <c>Acs:Reach:MountYawDeg</c>(T_A_B rz) · <c>ToolZMm</c>(공구 #1 z) ·
    /// <c>ApproachStandoffMm</c>(inspection.approachStandoffMm) · <c>FocusStandoffMm</c>(초점 자세 TCP 이격 — 공구 #1 365 + 10 = 카메라 초점 375) · <c>PlanarRetreatMm</c>(inspection.planarApproachRetreatMm) ·
    /// <c>StartJoints:{면 코드}</c>(운영 ② 출발 관절각 J1~J6 — 상부 면 T·SU·PU = 홈 위치_2, 그 외 = 작업 준비 위치) ·
    /// <c>OperationJ6Deg</c>(세 자세 J6 — 기본 180°, "none" 이면 IK 값) · <c>ToolMountRollDeg</c>(툴 상자 광축 둘레 장착 보정) ·
    /// 도면 순회 판정 <c>CheckTraversal</c>(기본 true) · <c>ScanStandoffMm</c>(순회 TCP 면 이격, 기본 400) · <c>ScanTiltDeg</c>(포괄 근사 ±기울기, 기본 40) ·
    /// <c>ScanTiltProfile</c>(실제 경유점 [[거리 mm, 기울기 도, Z mm], …] — 주면 포괄 근사 대신 사용) · <c>TraverseMarginTargetDeg</c>(정차 최적화 순회 여유 목표, 기본 10).
    /// 없으면 STEP 장착(0°)·모델 기본값·출발 자세 없음(종전 판정).
    /// </param>
    public ReachSimService(AcsDbContext db, InspectionDispatcher dispatcher, IConfiguration? config = null)
    {
        _db = db;
        _dispatcher = dispatcher;
        _config = config;
        _baseModel = ModelFrom(config);
        _model = _baseModel;
        _wallMinDistM = WallMinDistFrom(config);
        _poseError = PoseErrorFrom(config);
        _scanProfileName = config?["Acs:Reach:ScanProfileName"];
        _useAmrProfiles = config?.GetValue("Acs:Reach:UseAmrProfiles", true) ?? true;
        _useAmrStartJoints = config?.GetValue("Acs:Reach:UseAmrStartJoints", true) ?? true;
    }

    /// <summary>정차 최적화가 보는 위치 오차 조건(<c>Acs:Reach:PoseError</c>) — 꺼져 있으면 null.</summary>
    private readonly PoseErrorOptions? _poseError;

    /// <summary>
    /// <c>Acs:Reach:PoseError{Enabled=true, RadiusM 0.3, YawDeg 3, TopCandidates 6, MaxBatches 3}</c> — 정차 위치 오차(SLAM·도착 오차) 조건.
    /// 설정이 없으면(단위 테스트 등) 보지 않는다. 반경·방향이 모두 0 이하이거나 Enabled=false 면 null.
    /// </summary>
    public static PoseErrorOptions? PoseErrorFrom(IConfiguration? config)
    {
        var sec = config?.GetSection("Acs:Reach:PoseError");
        if (sec is null || !sec.Exists() || !sec.GetValue("Enabled", true)) return null;
        var o = new PoseErrorOptions(
            Math.Max(0, sec.GetValue("RadiusM", 0.3)), Math.Max(0, sec.GetValue("YawDeg", 3.0)),
            Math.Max(1, sec.GetValue("TopCandidates", 6)), Math.Max(1, sec.GetValue("MaxBatches", 3)));
        return o.Enabled ? o : null;
    }

    private readonly bool _useAmrProfiles;
    private Func<string, string, string, SeamScanProfile?> _profileOf = (_, _, _) => null;
    /// <summary>영역 → 면 코드(용접선의 프로필을 찾을 때 쓴다) — <see cref="Setup"/> 이 채운다.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _areaWall = new();
    /// <summary>용접선 점 목록(객체) → 그 용접선의 순회 프로필 — <see cref="SeamPoints"/> 가 채우고 모델의 SeamProfile 이 읽는다.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IReadOnlyList<double[]>, SeamScanProfile> _seamProfiles =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>이 용접선 점 목록에 붙은 순회 프로필(이름·버전) — 없으면 null(설정값·포괄 근사로 판정).</summary>
    public SeamScanProfile? ProfileOf(IReadOnlyList<double[]> seamPoints) =>
        _seamProfiles.TryGetValue(seamPoints, out var p) ? p : null;

    private readonly bool _useAmrStartJoints;

    /// <summary>순회 프로필이 어디서 왔는지 — "AMR {이름} v{버전}" 또는 "설정"(appsettings · 포괄 근사).</summary>
    public string ScanProfileSource { get; private set; } = "설정";

    /// <summary>
    /// 그 선창의 기준 모델 — 선창의 검사 종류(<c>ref.tank_geometry.inspection_kind</c>)로 리치 설정(<see cref="ModelFrom"/> ·
    /// <c>Acs:Reach:Kinds:{종류}</c>)과 순회 프로필을 보낸 AMR(<see cref="ProfileSourceFor"/>)을 고른다. 설정 <c>Acs:Reach:ScanProfileName</c>
    /// 의 프로필을 그 AMR 이 올려 보냈고 직선 프로필이면 그 경유점으로 순회를 판정한다. 종류마다 요청당 한 번만 조회한다.
    /// </summary>
    private async Task<ReachRobotModel> BaseAsync(string? tankId, CancellationToken ct)
    {
        var geo = tankId is null ? null
            : await _db.TankGeometries.AsNoTracking().Where(t => t.TankId == tankId)
                .Select(t => new { t.InspectionKind, t.LevelZ }).FirstOrDefaultAsync(ct);
        var kind = InspectionKinds.TryNormalize(geo?.InspectionKind, out var k) && k is not null ? k : InspectionKinds.Weld;
        var key = $"{kind}|{tankId}";   // 층별 바닥 올림은 선창의 층 높이에 묶이므로 선창마다 따로
        if (_resolved.TryGetValue(key, out var hit))
        {
            (_profileOf, ScanProfileSource) = (hit.ProfileOf, hit.Source);
            return hit.Model;
        }
        var model = _config is null ? _baseModel : ModelFrom(_config, kind);
        if (geo?.LevelZ is { } lz) model = model with { LevelFloorZ = Json<double[]>(lz) };
        // HD_AMR 교시 위치(홈 위치_2·작업 준비 위치 …)에서 온 면별 출발 자세 — 받은 면은 설정(Acs:Reach:StartJoints)보다 먼저 쓴다
        // (AMR 이 다시 교시하면 다음 프로필 전송부터 판정이 따라간다). 받은 적이 없으면 설정 그대로(구버전 AMR)
        if (_useAmrStartJoints
            && await AmrTeachingService.LoadStartJointsAsync(_db, ct, ProfileSourceFor(_config, kind), OtherProfileSources(_config, kind)) is { } amrStarts)
        {
            var merged = new Dictionary<string, double[]>(model.StartJointsByWall, StringComparer.OrdinalIgnoreCase);
            foreach (var (wall, j) in amrStarts.Starts) merged[wall] = j;
            model = model with { StartJointsByWall = merged };
        }
        var profileOf = (Func<string, string, string, SeamScanProfile?>)((_, _, _) => null);
        var sourceText = "설정";
        var source = ProfileSourceFor(_config, kind);
        if (_useAmrProfiles)
        {
            // HD_AMR 지정표 — 용접선의 유형·면·방향마다 그 검사 종류의 AMR 이 실제로 쓰는 프로필(6축 경유점)로 순회를 판정한다
            profileOf = await InspectionProfileService.LoadSeamProfilesAsync(_db, ct, source, OtherProfileSources(_config, kind));
            model = model with { SeamProfile = pts => _seamProfiles.TryGetValue(pts, out var p) ? p.Waypoints : null };
        }
        if (!string.IsNullOrWhiteSpace(_scanProfileName)
            && await InspectionProfileService.FindByNameAsync(_db, _scanProfileName.Trim(), ct, source, OtherProfileSources(_config, kind)) is { } e
            && InspectionProfileService.ToTiltProfile(e) is ({ } tilt, _))
        {
            model = model with { ScanTiltProfile = tilt };
            sourceText = $"AMR {e.Name} v{e.Version}";
        }
        _resolved[key] = (model, profileOf, sourceText);
        (_profileOf, ScanProfileSource) = (profileOf, sourceText);
        return model;
    }

    /// <summary>영역이 속한 선창의 기준 모델(<see cref="BaseAsync"/>) — 영역이 없으면 용접검사 기준.</summary>
    private async Task<ReachRobotModel> BaseForAreaAsync(Guid areaId, CancellationToken ct) =>
        await BaseAsync(await _db.InspectionAreas.AsNoTracking().Where(a => a.AreaId == areaId).Select(a => a.TankId).FirstOrDefaultAsync(ct), ct);

    /// <summary><c>Acs:Reach:WallMinDistMm:{면 코드}</c> [mm] → m. 0 이하·숫자가 아닌 값은 버린다.</summary>
    public static IReadOnlyDictionary<string, double> WallMinDistFrom(IConfiguration? config) =>
        config is null
            ? new Dictionary<string, double>()
            : config.GetSection("Acs:Reach:WallMinDistMm").GetChildren()
                .Select(s => (s.Key, Ok: double.TryParse(s.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v), V: v))
                .Where(x => x.Ok && x.V > 0)
                .ToDictionary(x => x.Key, x => x.V / 1000.0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 설정에서 장착 yaw·면별 출발 관절각을 읽어 로봇 모델을 만든다. 6개가 아닌 값은 버린다.
    /// <paramref name="inspectionKind"/> 를 주면 <c>Acs:Reach:Kinds:{종류}</c> 의 항목이 같은 이름의 <c>Acs:Reach</c> 항목을 통째로 대신한다
    /// (<see cref="ReachConfigFor"/> — 검사 종류마다 다른 AMR·코봇의 J6 범위·대기 자세·출발 자세·링크 치수 등).
    /// </summary>
    public static ReachRobotModel ModelFrom(IConfiguration? config, string? inspectionKind = null)
    {
        config = ReachConfigFor(config, inspectionKind);
        if (config is null) return ReachRobotModel.Default;
        var starts = config.GetSection("Acs:Reach:StartJoints").GetChildren()
            .Select(s => (Code: s.Key, J: s.Get<double[]>()))
            .Where(s => s.J is { Length: 6 })
            .ToDictionary(s => s.Code, s => s.J!, StringComparer.OrdinalIgnoreCase);
        var d = ReachRobotModel.Default;
        var lift = LiftSettings.From(config);
        var liftEnabled = lift.Enabled;
        var stages = d.StageStandoffsMm.ToArray();
        stages[0] = config.GetValue("Acs:Reach:ApproachStandoffMm", stages[0]);
        var scanStandoff = config.GetValue("Acs:Reach:ScanStandoffMm", stages[0]);   // 없으면 ② 접근 이격(점1 Top = ② 접근점)
        stages[^1] = config.GetValue("Acs:Reach:FocusStandoffMm", stages[^1]);   // 공구 TCP 가 초점보다 짧으면 그 차만큼 띄운다
        var j6 = SafeJ6From(config);
        return d with
        {
            // 링크 치수 [d1,a2,a3,d4,d5,d6] — 장비 컨트롤러의 DH 보정값(HD_AMR cobot.kinematics.linksMm 와 같은 값). 없거나 잘못되면 공칭
            Kinematics = Fr5Kinematics.FromLinks(config.GetSection("Acs:Reach:LinksMm").Get<double[]>()).Kinematics,
            MountYawDeg = config.GetValue("Acs:Reach:MountYawDeg", 0.0),
            // 코봇 BASE 높이(차체 바닥 기준)·차체가 층 바닥보다 올라간 높이(나무판 등) — 범위 밖이면 기본값
            MountZMm = MountZFrom(config.GetValue<double?>("Acs:Reach:MountZMm"), d.MountZMm),
            // 숫자 하나 = 모든 층, {"1": 25} = 층별(내부 층 번호 — 화면 L0 = 1) — 적힌 층만 올린다
            AmrFloorOffsetMm = FloorOffsetFrom(FloorOffsetScalar(config)),
            AmrFloorOffsetByLevelMm = FloorOffsetsByLevel(config),
            // 리프트를 쓰지 않으면 후보 높이는 0 하나(StrokeMaxMm 0 → 조사·최적화·보고서·지도 모두 0 으로 고정)이고,
            // 리프트 기둥은 차체 충돌 방지(자기 간섭)·면 간섭 판정에서 빠진다
            LiftEnabled = liftEnabled,
            StrokeMaxMm = liftEnabled ? d.StrokeMaxMm : 0,
            // 리프트 고정 모드 — 후보 높이는 고정값 하나, 저장값·요청값과 무관하게 그 높이로 판정
            FixedStrokeMm = lift.FixedStrokeMm,
            // 리프트 대신 고정 받침 기둥(리프트와 같은 단면, 차체 윗면 → BASE) — 리프트를 쓰지 않을 때만 의미가 있다
            SupportColumn = config.GetValue("Acs:Reach:SupportColumn", false),
            // 차체 윗면 높이 — 바꾸면 차체 윗면에 얹힌 신호탑도 같은 만큼 옮긴다(신호탑 자체 높이는 그대로)
            AmrHeightMm = AmrHeightFrom(config.GetValue<double?>("Acs:Reach:AmrHeightMm"), d.AmrHeightMm),
            TowerTopMm = d.TowerTopMm - d.AmrHeightMm + AmrHeightFrom(config.GetValue<double?>("Acs:Reach:AmrHeightMm"), d.AmrHeightMm),
            ToolZMm = config.GetValue("Acs:Reach:ToolZMm", d.ToolZMm),
            StageStandoffsMm = stages,
            PlanarRetreatMm = config.GetValue("Acs:Reach:PlanarRetreatMm", d.PlanarRetreatMm),
            StartJointsByWall = starts,
            ToolMountRollDeg = config.GetValue("Acs:Reach:ToolMountRollDeg", d.ToolMountRollDeg),
            // 운영 J6 — 따로 주지 않으면 대기 자세 J6 를 설정했을 때 그 값(HD_AMR: 수직 검사 J6 = 대기 자세), 아니면 모델 기본값(180°)
            OperationJ6Deg = OperationJ6From(config["Acs:Reach:OperationJ6Deg"], j6.StandbySet ? j6.Standby : d.OperationJ6Deg),
            CheckTraversal = config.GetValue("Acs:Reach:CheckTraversal", d.CheckTraversal),
            ScanStandoffMm = scanStandoff,
            ScanTiltDeg = config.GetValue("Acs:Reach:ScanTiltDeg", d.ScanTiltDeg),
            // 순회 판정 간격 — 3D 이동 거리·자세 회전각 기준. 1 mm·0.5° 미만은 받지 않는다(계산량)
            ScanSampleMm = config.GetValue<double?>("Acs:Reach:ScanSampleMm") is { } sm && sm >= 1 ? sm : d.ScanSampleMm,
            ScanTiltStepDeg = config.GetValue<double?>("Acs:Reach:ScanTiltStepDeg") is { } sd && sd >= 0.5 ? sd : d.ScanTiltStepDeg,
            ScanTiltProfile = TiltProfileFrom(config.GetSection("Acs:Reach:ScanTiltProfile")),
            TraverseMarginTargetDeg = config.GetValue("Acs:Reach:TraverseMarginTargetDeg", d.TraverseMarginTargetDeg),
            // 순위에서 여유를 비교할 상한 — 없거나 0 이하면 상한 없음(실제 최소 여유각이 큰 쪽)
            MarginRankCapDeg = config.GetValue<double?>("Acs:Reach:MarginRankCapDeg") is { } cap && cap > 0 ? cap : double.PositiveInfinity,
            MinFlangeReachMm = config.GetValue("Acs:Reach:MinFlangeReachMm", d.MinFlangeReachMm),
            MinElbowBendDeg = config.GetValue("Acs:Reach:MinElbowBendDeg", d.MinElbowBendDeg),
            // 손목 여유 |J5| — 경로(진입·순회)는 HD_AMR 과 같은 10°, 판정 자세는 그보다 넉넉한 15°
            // 출발 자세 → ② 해 관절별 최대 이동(가지 전환 판정) — HD_AMR inspection.maxEntryJointTravelDeg 와 같게
            MaxEntryJointTravelDeg = EntryTravelFrom(config.GetValue<double?>("Acs:Reach:MaxEntryJointTravelDeg"), d.MaxEntryJointTravelDeg),
            PlanarWristMarginDeg = WristMarginFrom(config.GetValue<double?>("Acs:Reach:WristMarginDeg"), d.PlanarWristMarginDeg),
            WristMarginDeg = WristMarginFrom(config.GetValue<double?>("Acs:Reach:PostureWristMarginDeg"), d.WristMarginDeg),
            CheckEntryJ6 = config.GetValue("Acs:Reach:CheckEntryJ6", d.CheckEntryJ6),
            EntrySelfMarginMm = config.GetValue("Acs:Reach:EntrySelfMarginMm", d.EntrySelfMarginMm),
            PlanarEntryWalls = PlanarEntryWallsFrom(config.GetSection("Acs:Reach:PlanarEntryWalls")),
            EntryPathMarginMm = config.GetValue("Acs:Reach:EntryPathMarginMm", d.EntryPathMarginMm),
            SafeJ6MinDeg = j6.Min,
            SafeJ6MaxDeg = j6.Max,
            SafeJ6StandbyDeg = j6.Standby,
        };
    }

    /// <summary>
    /// J6 안전 범위·대기 자세 설정 — 대기 자세(<c>SafeJ6StandbyDeg</c>, 기본 −180°)를 포함하지 않는 범위 등 잘못된 값이면 셋 모두 기본값
    /// (HD_AMR <c>ApplySafeJ6Range</c> 와 같은 규칙). StandbySet = 대기 자세를 설정에서 받아 그대로 쓰는지.
    /// </summary>
    public static (double Min, double Max, double Standby, bool StandbySet, bool Ok) SafeJ6From(IConfiguration config)
    {
        var standby = config.GetValue<double?>("Acs:Reach:SafeJ6StandbyDeg");
        var (lo, hi, ok) = ReachRobotModel.ResolveSafeJ6Range(
            config.GetValue<double?>("Acs:Reach:SafeJ6MinDeg"), config.GetValue<double?>("Acs:Reach:SafeJ6MaxDeg"), standby);
        return ok && standby is { } s
            ? (lo, hi, s, true, true)
            : (lo, hi, ReachRobotModel.DefaultSafeJ6StandbyDeg, false, ok);
    }

    /// <summary>검사 종류별 리치 설정이 있는 하위 묶음 — <c>Acs:Reach:Kinds:{WELD|GAS}</c>.</summary>
    public const string KindsSection = "Acs:Reach:Kinds";

    /// <summary>
    /// 검사 종류의 리치 설정 — <c>Acs:Reach</c> 위에 <c>Acs:Reach:Kinds:{종류}</c> 를 얹은 설정. 종류 묶음에 있는 항목은 같은 이름의
    /// 기본 항목을 **통째로** 대신한다(<c>StartJoints</c> 를 주면 면 전부, 배열은 길이째) — 기본 값과 섞이지 않게.
    /// 종류가 없거나 모르는 값이거나 그 묶음이 없으면 원래 설정 그대로(= 종전 동작). <c>Acs:Lift</c> 는 그대로 옮긴다.
    /// </summary>
    public static IConfiguration? ReachConfigFor(IConfiguration? config, string? inspectionKind)
    {
        if (config is null || !InspectionKinds.TryNormalize(inspectionKind, out var kind) || kind is null) return config;
        var ks = config.GetSection($"{KindsSection}:{kind}");
        if (!ks.Exists()) return config;
        var replaced = ks.GetChildren().Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config.GetSection("Acs:Reach").AsEnumerable(makePathsRelative: true))
        {
            if (value is null) continue;
            var top = key.Split(':')[0];
            if (top.Equals("Kinds", StringComparison.OrdinalIgnoreCase) || replaced.Contains(top)) continue;
            dict["Acs:Reach:" + key] = value;
        }
        foreach (var (key, value) in ks.AsEnumerable(makePathsRelative: true))
            if (value is not null) dict["Acs:Reach:" + key] = value;
        foreach (var (key, value) in config.GetSection("Acs:Lift").AsEnumerable())
            if (value is not null) dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>
    /// 그 검사 종류의 순회 프로필을 보낸 AMR(source) — <c>Acs:Reach:Kinds:{종류}:ProfileSource</c>, 용접검사(또는 종류 없음)는
    /// <c>Acs:Reach:ProfileSource</c> 도 본다. 없으면 null(호출측이 다른 종류에 지정된 source 를 뺀 가장 최근 AMR 을 쓴다).
    /// </summary>
    public static string? ProfileSourceFor(IConfiguration? config, string? inspectionKind)
    {
        if (config is null) return null;
        InspectionKinds.TryNormalize(inspectionKind, out var kind);
        var v = kind is null ? null : config[$"{KindsSection}:{kind}:ProfileSource"];
        if (string.IsNullOrWhiteSpace(v) && (kind is null || kind == InspectionKinds.Weld)) v = config["Acs:Reach:ProfileSource"];
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>다른 검사 종류에 지정된 source 들 — 지정이 없는 종류가 "가장 최근 AMR" 을 고를 때 뺀다(두 AMR 이 번갈아 보내도 섞이지 않게).</summary>
    public static IReadOnlyList<string> OtherProfileSources(IConfiguration? config, string? inspectionKind)
    {
        if (config is null) return Array.Empty<string>();
        InspectionKinds.TryNormalize(inspectionKind, out var kind);
        var mine = kind ?? InspectionKinds.Weld;
        return InspectionKinds.All.Where(k => k != mine)
            .Select(k => ProfileSourceFor(config, k)).OfType<string>()
            .Where(s => !string.Equals(s, ProfileSourceFor(config, mine), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 이 AMR(source)의 검사 종류 — 그 source 를 <c>ProfileSource</c> 로 지정한 종류. 없으면 null(기본 설정).
    /// 프로필 전송 때 코봇 모델 값을 그 종류의 설정과 대조하는 데 쓴다.
    /// </summary>
    public static string? KindForSource(IConfiguration? config, string? source)
    {
        if (config is null || string.IsNullOrWhiteSpace(source)) return null;
        return InspectionKinds.All.FirstOrDefault(k =>
            string.Equals(config[$"{KindsSection}:{k}:ProfileSource"]?.Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 리프트 사용 여부 — 설정 <c>Acs:Lift:Enabled</c>(기본 true). HD_AMR 에서 리프트를 끈(<c>Telescopic:Enabled = false</c>) AMR 이면
    /// false 로 둔다: 지령의 <c>liftStrokeMm</c> 은 항상 0, 정차 시뮬레이션·최적화는 리프트 0 만, 리프트 기둥은 간섭 판정에서 제외.
    /// </summary>
    public static bool LiftEnabledFrom(IConfiguration? config) => LiftSettings.From(config).Enabled;

    /// <summary>코봇 BASE 높이 설정 [mm] — 300~3000 만 받는다(밖이면 기본값).</summary>
    public static double MountZFrom(double? v, double fallback) =>
        v is { } x && double.IsFinite(x) && x >= 300 && x <= 3000 ? x : fallback;

    /// <summary>진입 관절 이동 한계 설정 [도] — 30~360 만 받는다(밖이면 기본값).</summary>
    public static double EntryTravelFrom(double? v, double fallback) =>
        v is { } x && double.IsFinite(x) && x >= 30 && x <= 360 ? x : fallback;

    /// <summary>차체 윗면 높이 설정 [mm] — 300~1500 만 받는다(밖이면 기본값).</summary>
    public static double AmrHeightFrom(double? v, double fallback) =>
        v is { } x && double.IsFinite(x) && x >= 300 && x <= 1500 ? x : fallback;

    /// <summary><c>Acs:Reach:AmrFloorOffsetMm</c> 가 숫자 하나면 그 값(모든 층), 층별 객체이거나 없으면 null.</summary>
    private static double? FloorOffsetScalar(IConfiguration? config) =>
        double.TryParse(config?["Acs:Reach:AmrFloorOffsetMm"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// <c>Acs:Reach:AmrFloorOffsetMm</c> 가 층별 객체({"1": 25})면 층 → mm(층 번호 1 이상·값 0~500 만, 나머지는 버림), 아니면 null.
    /// </summary>
    public static IReadOnlyDictionary<int, double>? FloorOffsetsByLevel(IConfiguration? config)
    {
        var children = config?.GetSection("Acs:Reach:AmrFloorOffsetMm").GetChildren().ToList();
        if (children is not { Count: > 0 }) return null;
        var map = new Dictionary<int, double>();
        foreach (var c in children)
            if (int.TryParse(c.Key, out var level) && level >= 1
                && double.TryParse(c.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mm))
                map[level] = FloorOffsetFrom(mm);
        return map;
    }

    /// <summary>차체 바닥 올림(나무판 등) 설정 [mm] — 0~500 만 받는다(밖이면 0).</summary>
    public static double FloorOffsetFrom(double? v) =>
        v is { } x && double.IsFinite(x) && x >= 0 && x <= 500 ? x : 0;

    /// <summary>손목 여유 설정 — 0 초과 90 미만만 받는다(밖이면 기본값).</summary>
    private static double WristMarginFrom(double? v, double fallback) =>
        v is { } x && double.IsFinite(x) && x > 0 && x < 90 ? x : fallback;

    /// <summary>평면 경유 진입을 쓰는 면 — 설정이 없으면 null(모든 면, 종전), 빈 배열이면 어느 면도 쓰지 않는다.</summary>
    private static IReadOnlySet<string>? PlanarEntryWallsFrom(IConfigurationSection section) =>
        section.Exists()
            ? section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// 도면 순회 실제 경유점 — [[거리 mm, 기울기 도(, Z mm)], …]. 없거나 비면 null(±ScanTiltDeg 포괄 근사). 값이 2~3개가 아닌 항목은 버린다.
    /// </summary>
    private static IReadOnlyList<double[]>? TiltProfileFrom(IConfigurationSection section)
    {
        var list = section.GetChildren().Select(c => c.Get<double[]>()).Where(v => v is { Length: 2 or 3 }).Select(v => v!).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>운영 J6 설정 — 없으면 모델 기본값(180°), "none"·빈 값이면 IK 값 그대로(광축 둘레 임의).</summary>
    private static double? OperationJ6From(string? raw, double? fallback) =>
        raw is null ? fallback
        : double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
        : null;

    /// <summary>리프트 후보 — 미사용 [0], 고정 [고정 높이], 자동 0~1000 (<see cref="ReachRobotModel.StrokeCandidates"/>).</summary>
    private IReadOnlyList<double> Strokes => _model.StrokeCandidates(StrokeStepMm);

    /// <summary>
    /// 선창(층·면 필터)의 용접선마다 정차·리프트를 훑어 간섭 없이 닿는지 판정한다. 선창이 없으면 null.
    /// <paramref name="strokeMm"/> 를 주면 리프트를 그 높이 하나로 고정해 판정한다.
    /// <paramref name="areaLift"/> 면 영역마다 리프트를 한 높이로 고정한다(<see cref="AreaLift"/> — 고른 높이에서의 판정을 돌려주고
    /// 영역에 고정 높이·용접선별 최적과의 비교를, 용접선에 용접선별 최적 판정·높이를 싣는다).
    /// </summary>
    public async Task<IReadOnlyList<ReachSurveyArea>?> SurveyAsync(string tankId, int? level, string? wallCode, CancellationToken ct,
        ReachBranchRule branch = ReachBranchRule.MaxMargin, double? strokeMm = null, bool areaLift = false)
    {
        _model = (await BaseAsync(tankId, ct)) with { BranchRule = branch };
        var strokes = strokeMm is { } fixedStroke ? new[] { _model.LiftStrokeMm(fixedStroke) } : Strokes;
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (level is { } lv) q = q.Where(a => a.Level == lv);
        if (wallCode is { Length: > 0 }) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);

        var list = new List<ReachSurveyArea>(areas.Count);
        foreach (var a in areas)
        {
            if (!ctx.Walls.TryGetValue(a.WallCode, out var wall)) continue;
            var setup = Setup(ctx, a, wall);
            if (areaLift)
            {
                list.Add(AreaLiftSurvey(a, setup, strokes));
                continue;
            }
            var seams = a.Tasks.OrderBy(t => t.Seq).Select(t =>
            {
                var pts = SeamPoints(setup.Pose, t);
                var s = ReachSimulator.Survey(_model, setup.Planes, setup.Stations, pts, setup.Normal, strokes, a.WallCode);
                return new ReachSurveySeam(t.TaskId, t.Seq,
                    Math.Round(pts.Min(p => p[2]) - setup.FloorZ, 3), Math.Round(pts.Max(p => p[2]) - setup.FloorZ, 3),
                    s.Verdict.ToString(), s.StrokeMm, s.MinClearanceM is { } c ? Math.Round(c, 4) : null, s.YawIndex, s.Blocker,
                    TraverseMarginDeg: Round1(s.TraverseMarginDeg));
            }).ToList();
            list.Add(new ReachSurveyArea(a.AreaId, a.Name, a.WallCode, a.Level, setup.Stations[0].X, setup.Stations[0].Y, seams));
        }
        return list;
    }

    private static double? Round1(double? v) => v is { } x && double.IsFinite(x) ? Math.Round(x, 1) : null;

    /// <summary>영역 하나를 리프트 고정으로 조사 — 용접선 판정은 고정 높이의 것, 비교용 용접선별 최적을 함께 싣는다.</summary>
    private ReachSurveyArea AreaLiftSurvey(InspectionAreaEntity a, AreaSetup setup, IReadOnlyList<double> strokes)
    {
        var tasks = a.Tasks.OrderBy(t => t.Seq).ToList();
        var pts = tasks.Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
        var r = AreaLift.Evaluate(_model, setup.Planes, setup.Stations, pts, setup.Normal, strokes, a.WallCode);
        var seams = tasks.Select((t, i) =>
        {
            var (s, b) = (r.Fixed[i], r.Best[i]);
            return new ReachSurveySeam(t.TaskId, t.Seq,
                Math.Round(pts[i].Min(p => p[2]) - setup.FloorZ, 3), Math.Round(pts[i].Max(p => p[2]) - setup.FloorZ, 3),
                s.Verdict.ToString(), s.StrokeMm ?? r.StrokeMm, s.MinClearanceM is { } c ? Math.Round(c, 4) : null, s.YawIndex, s.Blocker,
                b.Verdict.ToString(), b.StrokeMm, Round1(s.TraverseMarginDeg));
        }).ToList();
        return new ReachSurveyArea(a.AreaId, a.Name, a.WallCode, a.Level, setup.Stations[0].X, setup.Stations[0].Y, seams,
            r.StrokeMm, r.BestClear, r.BestReachable);
    }

    /// <summary>
    /// 용접선 점 하나(0 시작 · 1 중간 · 2 끝)를 세 자세로 풀어 IK 해 전부·부품 간섭을 돌려준다.
    /// strokeMm 이 없으면 조사로 고른 리프트(없으면 0)를 쓴다. 영역·작업이 없으면 null.
    /// </summary>
    public async Task<ReachDetail?> DetailAsync(Guid areaId, Guid taskId, int point, double? strokeMm,
        double offsetForwardMm, double offsetLeftMm, int yawIndex, CancellationToken ct,
        ReachBranchRule branch = ReachBranchRule.MaxMargin)
    {
        _model = (await BaseForAreaAsync(areaId, ct)) with { BranchRule = branch };
        var area = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).FirstOrDefaultAsync(a => a.AreaId == areaId, ct);
        var task = area?.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (area is null || task is null) return null;
        var ctx = await LoadTankAsync(area.TankId, ct);
        if (ctx is null || !ctx.Walls.TryGetValue(area.WallCode, out var wall)) return null;

        var setup = Setup(ctx, area, wall);
        var pts = SeamPoints(setup.Pose, task);
        yawIndex = Math.Clamp(yawIndex, 0, setup.Stations.Count - 1);
        var surveyed = strokeMm is null
            ? ReachSimulator.Survey(_model, setup.Planes, new[] { setup.Stations[yawIndex] }, pts, setup.Normal, Strokes, area.WallCode)
            : null;
        var h = _model.LiftStrokeMm(strokeMm ?? surveyed?.StrokeMm);
        // 중간·끝점은 조사와 같게 시작점 초점 자세부터 앞 점 초점 해를 따라간다(운영은 시작점 초점 자세에서 용접선을 따라 스캔한다).
        point = Math.Clamp(point, 0, 2);
        double[]? seed = null;
        for (var i = 0; i < point; i++)
        {
            var prev = ReachSimulator.Evaluate(_model, setup.Planes, setup.Stations[yawIndex], pts[i], setup.Normal, h,
                offsetForwardMm, offsetLeftMm, withGeometry: false, wallCode: i == 0 ? area.WallCode : null, checkEntry: false,
                seedJoints: seed);
            seed = ReachSimulator.ScanSeed(prev);
        }
        var r = ReachSimulator.Evaluate(_model, setup.Planes, setup.Stations[yawIndex], pts[point],
            setup.Normal, h, offsetForwardMm, offsetLeftMm, wallCode: area.WallCode, seedJoints: seed);
        // 도면 순회 — 시작점 ② 접근 자세에서 용접선 끝까지(대상 점과 무관하게 같은 판정).
        var startSeed = point == 0 ? ReachSimulator.TraverseSeed(r, _model)
            : ReachSimulator.TraverseSeed(ReachSimulator.Evaluate(_model, setup.Planes, setup.Stations[yawIndex], pts[0], setup.Normal, h,
                offsetForwardMm, offsetLeftMm, withGeometry: false, wallCode: area.WallCode, checkEntry: false), _model);
        var trav = ReachSimulator.TraverseSeam(_model, setup.Stations[yawIndex], h, pts, setup.Normal, startSeed, offsetForwardMm, offsetLeftMm);

        var seams = area.Tasks.OrderBy(t => t.Seq).Select(t =>
        {
            var p = SeamPoints(setup.Pose, t);
            return new ReachSeamLine(t.TaskId, t.Seq, p[0], p[2]);
        }).ToList();
        var st = setup.Stations[yawIndex];
        return new ReachDetail(area.AreaId, area.Name, area.WallCode, area.Level, setup.FloorZ,
            st.X, st.Y, st.Yaw, setup.Stations.Count, yawIndex, h, offsetForwardMm, offsetLeftMm,
            task.TaskId, Math.Clamp(point, 0, 2), pts[Math.Clamp(point, 0, 2)], seams,
            new ReachBodyDto(r.Body.Corners, r.Body.ClearanceM, r.Body.NearestPlane),
            r.Stages.Select(s => new ReachStageDto(s.Stage.ToString(), s.StandoffMm, s.TcpTarget, s.ChosenIndex,
                s.Solutions.Select(x => new ReachSolutionDto(x.JointsDeg, x.PostureOk, x.MarginDeg, x.Limiting, x.WristDown,
                    x.MinClearanceM, x.Focus,
                    x.Parts.Select(p => new ReachPartDto(p.Name, p.A, p.B, p.RadiusM, p.ClearanceM, p.NearestPlane, p.Corners)).ToList(),
                    x.Joints.Select(j => new ReachJointDto(j.Name, j.Position)).ToList())).ToList())).ToList(),
            _model.WarnClearanceM,
            r.Entry is { } e ? new ReachEntryDto(e.Ok, e.Summary, e.InPlaneMm, e.Candidates, e.StartTcp, e.Entry, e.Plunge, e.Rejects) : null,
            r.BranchChange, ReachSimulator.StartJoints(_model, area.WallCode), _model.MountYawDeg, r.ScanOnly,
            trav is null ? null : new ReachTraversalDto(trav.Ok, trav.Blocker,
                double.IsFinite(trav.MinMarginDeg) ? trav.MinMarginDeg : null, trav.Limiting, trav.TwistDeg,
                _model.ScanStandoffMm, _model.ScanTiltProfile is null ? _model.ScanTiltDeg : null,
                trav.Worst is { } w ? new ReachTraversalPointDto(w.AlongMm, w.TiltDeg, w.Tcp, w.JointsDeg, w.MarginDeg, w.Limiting) : null,
                trav.Points.Count));
    }

    /// <summary>
    /// 영역 정차점 최적화(조회 전용) — 지금 정차에서 시작해 거리·좌우·방향을 옮겨 가며 간섭 없이 닿는 용접선이 가장 많은 정차를 찾는다.
    /// 용접선 없는 영역은 건너뛴다. 선창이 없으면 null.
    /// </summary>
    public async Task<IReadOnlyList<ReachStationOptimum>?> OptimizeAsync(string tankId, int? level, string? wallCode, Guid? areaId,
        CancellationToken ct, ReachBranchRule branch = ReachBranchRule.MaxMargin, bool areaLift = true)
    {
        _model = (await BaseAsync(tankId, ct)) with { BranchRule = branch };
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (areaId is { } id) q = q.Where(a => a.AreaId == id);
        if (level is { } lv) q = q.Where(a => a.Level == lv);
        if (wallCode is { Length: > 0 }) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);

        // 임시 제외(enabled = false) 용접선은 정차를 고를 때 보지 않는다 — 배차가 싣지 않는 용접선이 정차를 좌우하지 않게
        foreach (var a in areas) a.Tasks = a.Tasks.Where(t => t.Enabled).ToList();
        var list = new List<ReachStationOptimum>();
        foreach (var a in areas)
        {
            if (!ctx.Walls.TryGetValue(a.WallCode, out var wall) || a.Tasks.Count == 0) continue;
            var setup = Setup(ctx, a, wall);
            if (SearchSpace(a, wall, setup) is not { } space) continue;
            var seams = a.Tasks.OrderBy(t => t.Seq).Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = StationOptimizer.Optimize(_model, setup.Planes, space, seams, setup.Normal, Strokes, ct, a.WallCode, areaLift, _poseError);
            var tasks = a.Tasks.OrderBy(t => t.Seq).ToList();
            var perSeam = tasks.Select((t, i) => new ReachOptimumSeam(t.TaskId, t.Seq,
                i < r.Baseline.Seams.Count ? r.Baseline.Seams[i].Verdict.ToString() : nameof(ReachVerdict.Unreachable),
                i < r.Best.Seams.Count ? r.Best.Seams[i].Verdict.ToString() : nameof(ReachVerdict.Unreachable),
                i < r.Best.Seams.Count ? (r.Best.Seams[i].StrokeMm ?? r.Best.StrokeMm) : r.Best.StrokeMm,
                i < r.Baseline.Seams.Count ? Round1(r.Baseline.Seams[i].TraverseMarginDeg) : null,
                i < r.Best.Seams.Count ? Round1(r.Best.Seams[i].TraverseMarginDeg) : null,
                i < r.Best.Seams.Count && r.Best.Seams[i].Verdict == ReachVerdict.Unreachable ? r.Best.Seams[i].Blocker : null)).ToList();
            list.Add(new ReachStationOptimum(a.AreaId, a.Name, a.WallCode, a.Level,
                a.StationX is not null || a.StationY is not null || a.StationTheta is not null,
                Candidate(r.Baseline), Candidate(r.Best), r.Evaluated, sw.ElapsedMilliseconds, perSeam,
                Improved: !ReferenceEquals(r.Best, r.Baseline)));
        }
        return list;
    }

    /// <summary>
    /// 재검사(리페어) 정차 — 영역마다 **지정한 용접선만** 놓고 정차점 최적화를 돌려 그 용접선에 맞는 정차·리프트를 고른다(조회 전용).
    /// 영역에 저장된 정차는 영역의 모든 용접선을 보고 고른 값이라, 일부 용접선에는 거리·리프트가 모자랄 수 있다.
    /// 닿는 정차를 찾지 못하면(또는 면·탐색 좌표를 만들 수 없으면) 저장된 정차 그대로(<see cref="RepairStop.Applied"/> = false).
    /// </summary>
    public async Task<IReadOnlyList<RepairStop>> RepairStopsAsync(string tankId, IReadOnlySet<Guid> taskIds, CancellationToken ct,
        IReadOnlySet<Guid>? onlyAreas = null)
    {
        _model = (await BaseAsync(tankId, ct));   // 운영 가지 규칙(MaxMargin)
        var list = new List<RepairStop>();
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return list;
        var areas = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks)
            .Where(a => a.TankId == tankId && a.Tasks.Any(t => taskIds.Contains(t.TaskId)))
            .OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);
        foreach (var a in areas)
        {
            if (onlyAreas is not null && !onlyAreas.Contains(a.AreaId)) continue;
            var tasks = a.Tasks.Where(t => t.Enabled && taskIds.Contains(t.TaskId)).OrderBy(t => t.Seq).ToList();
            if (tasks.Count == 0 || !ctx.Walls.TryGetValue(a.WallCode, out var wall)) continue;
            var setup = Setup(ctx, a, wall);
            var saved = setup.Stations[0];
            var savedLift = _model.LiftStrokeMm(a.StationLiftMm);   // 미사용 0 · 고정이면 고정 높이
            if (SearchSpace(a, wall, setup) is not { } space)
            {
                list.Add(new RepairStop(a.AreaId, a.Name, saved.X, saved.Y, saved.Yaw, savedLift, false, tasks.Count, 0, 0, 0, 0, 0,
                    savedLift, "탐색 좌표를 만들 수 없음 — 저장된 정차 그대로"));
                continue;
            }
            var seams = tasks.Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
            // 지금 조건 = 저장된 정차 + 저장된 리프트(배차가 보내는 값)
            var nowSeams = seams.Select(p => ReachSimulator.Survey(_model, setup.Planes, new[] { saved }, p, setup.Normal,
                new[] { savedLift }, a.WallCode)).ToList();
            int nowClear = nowSeams.Count(s => s.Verdict == ReachVerdict.Clear);
            int nowReach = nowSeams.Count(s => s.Verdict != ReachVerdict.Unreachable);

            var r = StationOptimizer.Optimize(_model, setup.Planes, space, seams, setup.Normal, Strokes, ct, a.WallCode, areaLift: true, poseError: _poseError);
            var b = r.Best;
            int bestReach = b.Clear + b.Collision;
            if (!b.Feasible || bestReach == 0)
            {
                list.Add(new RepairStop(a.AreaId, a.Name, saved.X, saved.Y, saved.Yaw, savedLift, false, tasks.Count,
                    nowClear, nowClear, nowReach, nowReach, 0, savedLift, "닿는 정차를 찾지 못함 — 저장된 정차 그대로"));
                continue;
            }
            var lift = b.StrokeMm ?? savedLift;
            var moved = Math.Sqrt(Math.Pow(b.Station.X - saved.X, 2) + Math.Pow(b.Station.Y - saved.Y, 2));
            var note = moved < 0.005 && Math.Abs(lift - savedLift) < 0.5 ? "저장된 정차가 이미 최적"
                : $"정차 {moved:0.00} m 이동 · 리프트 {savedLift:0} → {lift:0} mm";
            list.Add(new RepairStop(a.AreaId, a.Name, Math.Round(b.Station.X, 3), Math.Round(b.Station.Y, 3), Math.Round(b.Station.Yaw, 4),
                lift, true, tasks.Count, nowClear, b.Clear, nowReach, bestReach, Math.Round(moved, 3), savedLift, note));
        }
        return list;
    }

    /// <summary>
    /// 배차 직전 정차 선택(조회 전용) — 로봇 현재 위치(도면 x·y)에서 가장 가까우면서 영역의 모든 용접선을 수행할 수 있는 정차·리프트
    /// (<see cref="StationOptimizer.Nearest"/>). 고를 곳이 없으면(또는 영역·면·탐색 좌표가 없으면) null — 저장된 정차를 쓴다.
    /// </summary>
    public async Task<NearestStop?> NearestStopAsync(Guid areaId, double fromX, double fromY, NearestOptions options, CancellationToken ct,
        double? fromYaw = null)
    {
        _model = (await BaseForAreaAsync(areaId, ct));   // 운영 가지 규칙
        var a = await _db.InspectionAreas.AsNoTracking().Include(x => x.Tasks).FirstOrDefaultAsync(x => x.AreaId == areaId, ct);
        if (a is null) return null;
        a.Tasks = a.Tasks.Where(t => t.Enabled).ToList();   // 임시 제외 용접선은 배차가 싣지 않는다
        if (a.Tasks.Count == 0) return null;
        var ctx = await LoadTankAsync(a.TankId, ct);
        if (ctx is null || !ctx.Walls.TryGetValue(a.WallCode, out var wall)) return null;
        var setup = Setup(ctx, a, wall);
        if (SearchSpace(a, wall, setup) is not { } space) return null;
        var seams = a.Tasks.OrderBy(t => t.Seq).Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = StationOptimizer.Nearest(_model, setup.Planes, space, seams, setup.Normal, Strokes, new[] { fromX, fromY }, options, ct, a.WallCode, fromYaw);
        if (r is null) return null;
        var p = r.Pick;
        return new NearestStop(a.AreaId, a.Name, Math.Round(p.Station.X, 3), Math.Round(p.Station.Y, 3), Math.Round(p.Station.Yaw, 4),
            _model.LiftStrokeMm(p.StrokeMm ?? a.StationLiftMm), r.Full, Math.Round(r.TravelM, 3), Math.Round(r.BaselineTravelM, 3),
            r.Evaluated, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// 영역 재분할 제안(조회 전용 — DB 를 바꾸지 않는다). 한 정차로 영역 모든 용접선에 도면 순회 관절 여유를 확보하지 못하면
    /// 용접선을 면 (u, v) 축 방향으로 잘라 최대 <paramref name="maxGroups"/> 묶음으로 나누고 묶음마다 정차·리프트를 고른다(<see cref="AreaSplitPlanner"/>).
    /// 묶음마다 새 영역 사각형(TASK 묶음 분리와 같은 자동 사각형)을 함께 싣는다. 영역이 없으면 null.
    /// </summary>
    public async Task<ReachSplitPlan?> SplitPlanAsync(Guid areaId, int maxGroups, CancellationToken ct,
        ReachBranchRule branch = ReachBranchRule.MaxMargin)
    {
        _model = (await BaseForAreaAsync(areaId, ct)) with { BranchRule = branch };
        var area = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).FirstOrDefaultAsync(a => a.AreaId == areaId, ct);
        if (area is null) return null;
        var ctx = await LoadTankAsync(area.TankId, ct);
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == area.TankId, ct);
        if (ctx is null || g is null || !ctx.Walls.TryGetValue(area.WallCode, out var wall)) return null;
        var tasks = area.Tasks.OrderBy(t => t.Seq).ToList();
        var setup = Setup(ctx, area, wall);
        if (tasks.Count == 0 || SearchSpace(area, wall, setup) is not { } space)
            return new ReachSplitPlan(area.AreaId, area.Name, area.WallCode, area.Level, 0, new List<Guid>(), 0, 0, new List<ReachSplitGroup>());

        var seams = tasks.Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
        var uv = tasks.Select(t => (IReadOnlyList<double[]>)TaskUv(t)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = AreaSplitPlanner.Plan(_model, setup.Planes, space, seams, uv, AreaGeometry.MaxSpanM, AreaGeometry.MaxSpanV(area.WallCode),
            setup.Normal, Strokes, area.WallCode, Math.Clamp(maxGroups, 1, 6), ct: ct);
        var groups = plan.Groups.Select(gr =>
        {
            var members = gr.Seams.Select(i => tasks[i]).ToList();
            var seamRows = gr.Seams.Select((i, k) =>
            {
                var r = gr.Best.Seams[k];
                return new ReachSplitSeam(tasks[i].TaskId, tasks[i].Seq, r.Verdict.ToString(), Round1(r.TraverseMarginDeg),
                    r.Verdict == ReachVerdict.Unreachable ? r.Blocker : null);
            }).ToList();
            return new ReachSplitGroup(members.Select(t => t.TaskId).ToList(), members.Select(t => t.Seq).ToList(),
                Candidate(gr.Best), AreaRules.AutoCornersForTasks(members, wall, g, area.Level), seamRows);
        }).ToList();
        return new ReachSplitPlan(area.AreaId, area.Name, area.WallCode, area.Level, plan.Unsafe,
            plan.Hopeless.Select(i => tasks[i].TaskId).ToList(), plan.GridCells, sw.ElapsedMilliseconds, groups);
    }

    /// <summary>작업의 면 (u, v) 점 — 시작·끝·교차 가지.</summary>
    private static double[][] TaskUv(AreaTaskEntity t)
    {
        var pts = new List<double[]> { new[] { t.StartU, t.StartV }, new[] { t.EndU, t.EndV } };
        if (!string.IsNullOrWhiteSpace(t.Points) && JsonSerializer.Deserialize<double[][]>(t.Points) is { } arms)
            pts.AddRange(arms.Where(p => p is { Length: >= 2 }).Select(p => new[] { p[0], p[1] }));
        return pts.ToArray();
    }

    /// <summary>
    /// 탐색 좌표계 — 영역 중심(수평)·면 안쪽 수평 방향. 지금 정차(덮어쓰기 포함)를 그 좌표로 바꿔 시작점으로 둔다.
    /// 법선에 수평 성분이 없는 바닥·천장(T·B)은 면 U축 수평 방향을 기준으로 중심 주위 전후·좌우·전 방향(±180°)을 본다.
    /// </summary>
    private StationSearchSpace? SearchSpace(InspectionAreaEntity area, WallEntity wall, AreaSetup setup)
    {
        var (uc, vc) = AreaGeometry.Centroid(Json<double[][]>(area.Corners));
        var c = setup.Pose.LocalToDrawing(uc, vc);
        var st = setup.Stations[0];
        var dx = st.X - c[0];
        var dy = st.Y - c[1];
        var nx = setup.Normal[0];
        var ny = setup.Normal[1];
        var len = Math.Sqrt(nx * nx + ny * ny);
        if (len < 1e-6 || wall.FacingYaw is null)
        {
            var ux = setup.Pose.U[0];
            var uy = setup.Pose.U[1];
            var ulen = Math.Sqrt(ux * ux + uy * uy);
            if (ulen < 1e-6) return null;
            ux /= ulen;
            uy /= ulen;
            var uFacing = Math.Atan2(uy, ux);
            var uDyaw = Math.Atan2(Math.Sin(st.Yaw - uFacing), Math.Cos(st.Yaw - uFacing)) * 180 / Math.PI;
            return new StationSearchSpace(new[] { c[0], c[1] }, new[] { ux, uy }, uFacing, setup.FloorZ,
                dx * ux + dy * uy, -dx * uy + dy * ux, uDyaw,
                MinDistM: -1.2, MaxDistM: 1.2, MaxLateralM: 1.2, MaxYawDeg: 180, YawStepDeg: 15);
        }
        nx /= len;
        ny /= len;
        var facing = wall.FacingYaw.Value;
        var dyaw = Math.Atan2(Math.Sin(st.Yaw - facing), Math.Cos(st.Yaw - facing)) * 180 / Math.PI;
        return new StationSearchSpace(new[] { c[0], c[1] }, new[] { nx, ny }, facing, setup.FloorZ,
            dx * nx + dy * ny, -dx * ny + dy * nx, dyaw,
            MinWallDistM: _wallMinDistM.GetValueOrDefault(area.WallCode));
    }

    private static ReachStationCandidate Candidate(StationCandidate c) => new(
        Math.Round(c.Station.X, 3), Math.Round(c.Station.Y, 3), Math.Round(c.Station.Yaw, 4),
        Math.Round(c.DistM, 3), Math.Round(c.LateralM, 3), Math.Round(c.YawDeg, 1),
        Math.Round(c.BodyClearanceM, 3), c.Feasible, c.Clear, c.Collision, c.Unreachable, Math.Round(c.Robustness, 4),
        c.StrokeMm, c.SafeClear, Round1(c.TraverseMarginDeg), c.MarginTargetDeg,
        c.Error?.WorstMiss, c.Error?.WorstCollision, Round1(c.Error?.MinMarginDeg), c.Error?.Samples, c.Error?.RadiusM, c.Error?.YawDeg,
        c.Error is null ? null : Round1(c.GuaranteedMarginDeg));

    /// <summary>
    /// 영역을 도면 (x, y, 방향)에 세웠을 때의 판정(조회 전용) — 정차 최적화와 같은 판정에 위치 오차 표본 판정을 더한다.
    /// 리프트를 주면 그 높이로 고정, 없으면 영역 공통 높이를 고른다. 영역·면·탐색 좌표가 없으면 null.
    /// </summary>
    public async Task<ReachStationCandidate?> EvaluateStationAsync(Guid areaId, double x, double y, double thetaRad, double? strokeMm, CancellationToken ct)
    {
        _model = await BaseForAreaAsync(areaId, ct);
        var a = await _db.InspectionAreas.AsNoTracking().Include(t => t.Tasks).FirstOrDefaultAsync(t => t.AreaId == areaId, ct);
        if (a is null) return null;
        a.Tasks = a.Tasks.Where(t => t.Enabled).ToList();
        var ctx = await LoadTankAsync(a.TankId, ct);
        if (ctx is null || !ctx.Walls.TryGetValue(a.WallCode, out var wall) || a.Tasks.Count == 0) return null;
        var setup = Setup(ctx, a, wall);
        if (SearchSpace(a, wall, setup) is not { } space) return null;
        var seams = a.Tasks.OrderBy(t => t.Seq).Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
        double rx = x - space.CenterXY[0], ry = y - space.CenterXY[1];
        var dist = rx * space.InwardH[0] + ry * space.InwardH[1];
        var lateral = -rx * space.InwardH[1] + ry * space.InwardH[0];
        var dyaw = thetaRad - space.FacingYaw;
        var yawDeg = Math.Atan2(Math.Sin(dyaw), Math.Cos(dyaw)) * 180 / Math.PI;
        var strokes = strokeMm is { } h ? new[] { _model.LiftStrokeMm(h) } : Strokes;
        var c = StationOptimizer.Evaluate(_model, setup.Planes, space, dist, lateral, yawDeg, seams, setup.Normal, strokes,
            surveyInfeasible: true, wallCode: a.WallCode);
        if (_poseError is { } pe) c = StationOptimizer.WithPoseError(_model, setup.Planes, space, c, seams, setup.Normal, strokes, pe, ct, a.WallCode);
        return Candidate(c);
    }

    // ── 내부 ─────────────────────────────────────────────────────────────

    private sealed record TankCtx(double[] LevelZ, Dictionary<string, WallEntity> Walls, ReachPlane[] WallPlanes);

    private sealed record AreaSetup(WallPose Pose, double[] Normal, double FloorZ,
        IReadOnlyList<ReachPlane> Planes, IReadOnlyList<ReachStation> Stations);

    private async Task<TankCtx?> LoadTankAsync(string tankId, CancellationToken ct)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct);
        if (g is null) return null;
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId).ToDictionaryAsync(w => w.WallCode, ct);
        var planes = walls.Values.Select(w => new ReachPlane(w.WallCode, Json<double[]>(w.Origin), Json<double[]>(w.Normal))).ToArray();
        return new TankCtx(Json<double[]>(g.LevelZ), walls, planes);
    }

    /// <summary>영역의 정차 후보 — 배차와 같은 정차점. 정차 방향이 없는 바닥·천장은 네 방향을 후보로 둔다.</summary>
    private AreaSetup Setup(TankCtx ctx, InspectionAreaEntity area, WallEntity wall)
    {
        _areaWall[area.AreaId] = wall.WallCode;
        var floorZ = area.Level >= 1 && area.Level <= ctx.LevelZ.Length ? ctx.LevelZ[area.Level - 1] : 0;
        var (x, y, yaw) = InspectionDispatcher.StationDrawingOf(area, wall, _dispatcher.DefaultStandoffM(wall));
        var yaws = yaw is { } v ? new[] { v } : new[] { 0, Math.PI / 2, Math.PI, -Math.PI / 2 };
        var pose = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
        return new AreaSetup(pose, Json<double[]>(wall.Normal), floorZ,
            ReachSimulator.PlanesForLevel(ctx.WallPlanes, floorZ, area.Level),
            yaws.Select(t => new ReachStation(x, y, t, floorZ)).ToArray());
    }

    /// <summary>용접선 시작·중간·끝(도면 m).</summary>
    private double[][] SeamPoints(WallPose pose, AreaTaskEntity t)
    {
        var a = pose.LocalToDrawing(t.StartU, t.StartV);
        var b = pose.LocalToDrawing(t.EndU, t.EndV);
        var pts = new[] { a, Vec3.Scale(Vec3.Add(a, b), 0.5), b };
        if (_areaWall.TryGetValue(t.AreaId, out var wall)
            && _profileOf(t.SeamType, wall, InspectionProfileService.DirectionOf(t.StartU, t.StartV, t.EndU, t.EndV)) is { } profile)
            _seamProfiles[pts] = profile;
        return pts;
    }

    private static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s)!;
}

/// <summary>
/// 재검사 정차 하나(영역 1곳) — 지정한 용접선만 보고 고른 정차(도면 x·y·방향)·리프트. Applied = false 면 저장된 정차 그대로.
/// ClearBefore/ReachBefore = 저장된 정차·리프트에서 간섭 없이 닿는/닿는 용접선 수, …After = 고른 정차에서. MovedM = 정차 이동 거리.
/// </summary>
public sealed record RepairStop(Guid AreaId, string AreaName, double X, double Y, double Yaw, double LiftMm, bool Applied,
    int Welds, int ClearBefore, int ClearAfter, int ReachBefore, int ReachAfter, double MovedM, double SavedLiftMm, string Note);

/// <summary>
/// 배차 직전에 고른 정차(도면 x·y·방향)·리프트. Full = 전부 간섭 없음·순회 여유 확보(false = 전부 도달만).
/// TravelM / SavedTravelM = 로봇 현재 위치에서 고른 정차 / 저장된 정차까지 직선 거리.
/// </summary>
public sealed record NearestStop(Guid AreaId, string AreaName, double X, double Y, double Yaw, double LiftMm, bool Full,
    double TravelM, double SavedTravelM, int Evaluated, long ElapsedMs);

/// <summary>
/// 조사 결과 — 영역 하나. 리프트 고정 조사(<c>lift=area</c>)면 <paramref name="FixedStrokeMm"/> = 영역 고정 높이,
/// BestClear·BestReachable = 용접선마다 높이를 따로 고를 때의 간섭 없음·도달 수(비교 기준). 아니면 셋 다 null.
/// </summary>
public sealed record ReachSurveyArea(Guid AreaId, string Name, string WallCode, int Level, double StationX, double StationY,
    IReadOnlyList<ReachSurveySeam> Seams, double? FixedStrokeMm = null, int? BestClear = null, int? BestReachable = null);

/// <summary>조사 결과 — 용접선 하나. Z 는 층 바닥 위 높이[m], Verdict = Clear | Collision | Unreachable.</summary>
/// Blocker = Unreachable 사유(출발 자세 가지 전환·진입 경로 없음 등, 없으면 null).
/// BestVerdict·BestStrokeMm = 리프트 고정 조사에서 이 용접선만 보고 높이를 고를 때의 판정·높이(아니면 null).
/// TraverseMarginDeg = 도면 순회의 가장 빡빡한 관절 여유[도](순회 판정을 안 했거나 도달 불가면 null).
public sealed record ReachSurveySeam(Guid TaskId, int Seq, double ZMin, double ZMax, string Verdict,
    double? StrokeMm, double? MinClearanceM, int YawIndex, string? Blocker = null,
    string? BestVerdict = null, double? BestStrokeMm = null, double? TraverseMarginDeg = null);

/// <summary>
/// 영역 재분할 제안 — 묶음(작업·정차·리프트·새 사각형·용접선별 판정). Unsafe = 나눈 뒤에도 순회 여유(목표)를 확보하지 못한 용접선 수
/// (가망 없는 용접선 제외), Hopeless = 어느 정차·리프트에서도 여유를 확보하지 못한 작업(분할로 해결 안 됨). 묶음이 1개면 나누지 않아도 된다.
/// </summary>
public sealed record ReachSplitPlan(Guid AreaId, string Name, string WallCode, int Level, int Unsafe, List<Guid> Hopeless,
    int GridCells, long ElapsedMs, List<ReachSplitGroup> Groups);

/// <summary>재분할 묶음 하나 — 작업(용접선)·정차 후보(리프트·여유 포함)·새 영역 사각형(면 u,v)·용접선별 판정.</summary>
public sealed record ReachSplitGroup(List<Guid> TaskIds, List<int> Seqs, ReachStationCandidate Station, double[][] Corners,
    List<ReachSplitSeam> Seams);

/// <summary>묶음 안 용접선 하나의 판정(Clear | Collision | Unreachable)·도면 순회 관절 여유[도]·막힘 사유.</summary>
public sealed record ReachSplitSeam(Guid TaskId, int Seq, string Verdict, double? TraverseMarginDeg, string? Blocker);

/// <summary>정차점 최적화 결과 — 영역 하나. Overridden = 이미 정차 덮어쓰기가 있음.</summary>
public sealed record ReachStationOptimum(Guid AreaId, string Name, string WallCode, int Level, bool Overridden,
    ReachStationCandidate Current, ReachStationCandidate Best, int Evaluated, long ElapsedMs, IReadOnlyList<ReachOptimumSeam> Seams,
    bool Improved = false);

/// <summary>용접선 하나의 지금·찾은 정차 판정과 찾은 정차에서 고른 리프트·도면 순회 관절 여유[도](순회 판정 없음·도달 불가면 null)·찾은 정차에서 막히는 사유.</summary>
public sealed record ReachOptimumSeam(Guid TaskId, int Seq, string Current, string Best, double? BestStrokeMm,
    double? CurrentTraverseMarginDeg = null, double? BestTraverseMarginDeg = null, string? BestBlocker = null);

/// <summary>후보 정차 — 도면 x·y·방향, 탐색 좌표(중심에서 안쪽 거리·좌측·방향 보정 °), 차체 여유, 판정 개수, 공통 리프트.
/// Error* = 위치 오차 표본(반경 ErrorRadiusM 8방향 + 방향 ±ErrorYawDeg)에서 가장 나쁜 값 — 도달 불가 수·간섭 수·순회 최소 여유.
/// GuaranteedMarginDeg = 제자리와 오차 표본 전체의 최소 여유(오차 판정을 안 했으면 null).</summary>
public sealed record ReachStationCandidate(double X, double Y, double Theta, double DistM, double LateralM, double YawDeg,
    double BodyClearanceM, bool Feasible, int Clear, int Collision, int Unreachable, double Robustness,
    double? StrokeMm = null, int SafeClear = 0, double? TraverseMarginDeg = null, double MarginTargetDeg = 10,
    int? ErrorMiss = null, int? ErrorCollision = null, double? ErrorMarginDeg = null, int? ErrorSamples = null,
    double? ErrorRadiusM = null, double? ErrorYawDeg = null, double? GuaranteedMarginDeg = null);

public sealed record ReachSeamLine(Guid TaskId, int Seq, double[] Start, double[] End);
public sealed record ReachBodyDto(double[][] Corners, double ClearanceM, string NearestPlane);
/// <summary>부품 — 캡슐(A·B·반경) 또는 상자(Corners 8개, 반경 0). 도면 m.</summary>
public sealed record ReachPartDto(string Name, double[] A, double[] B, double RadiusM, double ClearanceM, string NearestPlane,
    double[][]? Corners = null);
public sealed record ReachJointDto(string Name, double[] Position);
public sealed record ReachSolutionDto(double[] JointsDeg, bool PostureOk, double MarginDeg, string Limiting, bool WristDown,
    double MinClearanceM, double[] Focus, IReadOnlyList<ReachPartDto> Parts, IReadOnlyList<ReachJointDto> Joints);
public sealed record ReachStageDto(string Stage, double StandoffMm, double[] TcpTarget, int ChosenIndex,
    IReadOnlyList<ReachSolutionDto> Solutions);

/// <summary>상세 — 정차(도면)·리프트·정차 이동·대상 점·영역 용접선·차체·세 자세. 좌표는 도면 m.</summary>
public sealed record ReachDetail(
    Guid AreaId, string AreaName, string WallCode, int Level, double FloorZ,
    double StationX, double StationY, double StationYaw, int YawCount, int YawIndex,
    double StrokeMm, double OffsetForwardMm, double OffsetLeftMm,
    Guid TaskId, int Point, double[] Target, IReadOnlyList<ReachSeamLine> Seams,
    ReachBodyDto Body, IReadOnlyList<ReachStageDto> Stages, double WarnClearanceM,
    ReachEntryDto? Entry = null, string? BranchChange = null, double[]? StartJointsDeg = null, double MountYawDeg = 0,
    bool ScanOnly = false, ReachTraversalDto? Traversal = null);

/// <summary>
/// 도면 순회 판정 — 시작점 초점 자세에서 용접선 끝까지 경유점을 이어 풀 때 HD_AMR 이 막는가.
/// <paramref name="MinMarginDeg"/> = 가장 빡빡한 관절 여유(해가 없으면 null), <paramref name="TiltDeg"/> = 포괄 근사 ±기울기(실제 경유점을 쓰면 null).
/// </summary>
public sealed record ReachTraversalDto(bool Ok, string? Blocker, double? MinMarginDeg, string Limiting, double TwistDeg,
    double StandoffMm, double? TiltDeg, ReachTraversalPointDto? Worst, int States);

/// <summary>순회 판정 자세 하나 — 시작점에서의 거리 mm·기울기 도·TCP(도면 m)·관절각·여유.</summary>
public sealed record ReachTraversalPointDto(double AlongMm, double TiltDeg, double[] Tcp, double[] JointsDeg, double MarginDeg, string Limiting);

/// <summary>운영 ② 진입 경로(후퇴 평면 경유) 판정 — 출발 자세가 있는 면만. 좌표 도면 m.</summary>
public sealed record ReachEntryDto(bool Ok, string Summary, double InPlaneMm, int Candidates,
    double[] StartTcp, double[]? Entry, double[] Plunge, IReadOnlyList<string> Rejects);
