using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Reach;

/// <summary>
/// 리치·간섭 시뮬레이션의 로봇 모델 — AMR 차체·리프트·FR5-WML·검사 툴. 길이 mm(간섭 여유만 m).
/// 링크 굵기는 캡슐 근사이고, BASE 는 차체 중심 위 <see cref="MountZMm"/> + 리프트 스트로크에 있다(STEP 장착, 축 평행).
/// 차체·신호탑·리프트 기둥은 차체 기준 상자(STEP E26003-E-A000 실측)로, 탱크 면과 팔(자기 간섭) 모두와 잰다.
/// </summary>
public sealed record ReachRobotModel
{
    public Fr5Kinematics Kinematics { get; init; } = Fr5Kinematics.Fr5Wml;
    /// <summary>플랜지 → TCP(검사 툴 초점) [mm]. 광축 = 플랜지 +Z.</summary>
    public double ToolZMm { get; init; } = 375;
    /// <summary>
    /// 검사 툴 외곽 — 플랜지 기준 상자 [mm](STEP E26003-E-A000 안 A200 조립품 부품별 실측, 2026-11-02).
    /// 좌표: z = 광축(플랜지 면 0, 면 쪽 +), x = A200 +X(센서 헤드 반대쪽), y = A200 +Z. 레이저·D435i·커넥터 블록이 −x 쪽에 몰려 있다.
    /// 마지막 상자(카메라 브래킷)가 툴 끝이다. 카메라·렌즈·조명 브래킷은 카메라 브래킷 상자 안에 든다.
    /// STEP 의 레이저 부품에는 작동거리(약 300mm)까지 레이저선 형상이 들어 있어 실물 몸체(z ≤ 72)만 넣었다 —
    /// 종전 회전 대칭 원통(플랜지 369mm)은 이 레이저선까지 감싼 값이었다.
    /// </summary>
    public IReadOnlyList<ReachToolBox> ToolBoxes { get; init; } = new[]
    {
        new ReachToolBox("검사 툴(커넥터 블록)", new[] { -221.0, -38, -58 }, new[] { -186.0, 38, 22 }),
        new ReachToolBox("검사 툴(장착판)", new[] { -186.0, -38, 0 }, new[] { 40.0, 38, 12 }),
        new ReachToolBox("검사 툴(레이저 브래킷)", new[] { -170.0, -70, 0 }, new[] { -88.0, 78, 52 }),
        new ReachToolBox("검사 툴(레이저)", new[] { -193.5, -90, -1.3 }, new[] { -67.9, 98, 72 }),
        new ReachToolBox("검사 툴(D435i)", new[] { -222.5, -48, 8.9 }, new[] { -148.2, 48, 52.3 }),
        new ReachToolBox("검사 툴(카메라 브래킷)", new[] { -90.0, -90, 20 }, new[] { 90.0, 90, 276.5 }),
    };

    /// <summary>
    /// 툴 상자를 플랜지 z 둘레로 더 돌리는 각 [도]. 0 = 센서 헤드가 플랜지 −x — HD_AMR 이 J6 안전 범위를 90~180°로 둔 이유
    /// (그 밖이면 센서 헤드–링크암 간섭)와 홈 자세(J6 180°)에서 헤드가 옆을 향한다는 실기 기록에 맞춘 값이다.
    /// 실기 사진과 다르면 설정 <c>Acs:Reach:ToolMountRollDeg</c> 로 고친다.
    /// </summary>
    public double ToolMountRollDeg { get; init; }

    /// <summary>
    /// 운영 J6 [도] — HD_AMR 은 ② 접근 자세의 광축 비틀림을 J6 180°(VerticalJ6Deg)로 맞추고, 정렬 준비·초점은 그 자세에서
    /// 광축 방향으로만 움직이므로 세 자세 모두 같은 값이다. null 이면 IK 가 고른 J6(광축 둘레 임의). 설정 <c>Acs:Reach:OperationJ6Deg</c>.
    /// </summary>
    public double? OperationJ6Deg { get; init; } = 180;

    /// <summary>
    /// 자기 간섭(차체·기둥)에 쓰는 툴 외곽 — 몸체 브래킷 축 0~293 · 반경 169 만. 한쪽에 치우친 브래킷·조명은 J6 각에 따라
    /// 위치가 달라 회전 대칭으로 감싸면 실기 홈 자세부터 차체와 간섭으로 잘못 나온다(HD_AMR CobotEnvelope 와 같은 규칙).
    /// </summary>
    public ReachToolSegment SelfToolSegment { get; init; } = new("검사 툴(몸체)", 0, 293, 169);
    /// <summary>차체 바닥 → 코봇 BASE 면 높이(리프트 0) [mm].</summary>
    public double MountZMm { get; init; } = 1635;
    /// <summary>
    /// 장착 yaw [도] — BASE +X 가 차체 +X 에서 반시계로 돈 각(HD_AMR T_A_B rz). 링크 기하·간섭은 바뀌지 않고 관절각 이름표(J1)만
    /// 달라지지만, 출발 자세(<see cref="StartJointsByWall"/>)·J1 한계를 실기와 맞추려면 실제 값이어야 한다. 설정 <c>Acs:Reach:MountYawDeg</c>.
    /// </summary>
    public double MountYawDeg { get; init; }
    public double StrokeMaxMm { get; init; } = 1000;

    public double AmrFrontMm { get; init; } = 395;
    public double AmrRearMm { get; init; } = 365;
    public double AmrHalfWidthMm { get; init; } = 350;
    public double AmrHeightMm { get; init; } = 887;

    /// <summary>J2 축 방향 가로 오프셋 — 어깨(상완 위치)·팔꿈치 이후(전완 위치). DH d4 = 어깨 − 팔꿈치 + 손목.</summary>
    public double ShoulderOffsetMm { get; init; } = 185;
    public double ForearmOffsetMm { get; init; } = 45;

    /// <summary>리프트 기둥(CTD-LF144) 사각 단면 반폭 [mm] — 200×200, 차체 중심, 차체 윗면 → BASE.</summary>
    public double ColumnHalfMm { get; init; } = 100;
    /// <summary>차체 윗면 신호탑(SWTEL) — 후방 77~225 · 좌우 ±108 · 높이 933 [mm].</summary>
    public double TowerRearFromMm { get; init; } = 77;
    public double TowerRearToMm { get; init; } = 225;
    public double TowerHalfWidthMm { get; init; } = 108;
    public double TowerTopMm { get; init; } = 933;
    public double BaseRadiusMm { get; init; } = 80;
    public double ShoulderRadiusMm { get; init; } = 70;
    public double UpperArmRadiusMm { get; init; } = 60;
    public double ElbowRadiusMm { get; init; } = 55;
    public double ForearmRadiusMm { get; init; } = 50;
    public double WristRadiusMm { get; init; } = 45;

    /// <summary>
    /// 관절 허용 하한·상한(J1~J5) [도] — FAIRINO FR5-WML 사양(HD_AMR PostureLimits.Default 와 같다): J1 ±175 ·
    /// J2 −265~+85 · J3 ±170 · J4 −265~+85 · J5 ±175. 실측값이 아니다 — 실기 소프트리밋을 확인하면 그 값으로 맞출 것.
    /// J3 ±160 은 일반 FR5(리치 922mm) 값이었다. J6 은 운영이 따로 맞추므로 판정하지 않는다.
    /// </summary>
    public double[] JointMinDeg { get; init; } = { -175, -265, -170, -265, -175 };
    public double[] JointMaxDeg { get; init; } = { 175, 85, 170, 85, 175 };
    public double WristMarginDeg { get; init; } = 15;
    public double ElbowMarginDeg { get; init; } = 8;

    /// <summary>
    /// 플랜지가 검사면 쪽으로 최소한 나가 있어야 하는 거리 [mm] — HD_AMR <c>PostureLimits.MinFlangeReachMm</c>(실기 350, 실측 전 잠정값).
    /// 플랜지 뻗음 = BASE 에서 면까지 법선 거리 − ② 접근 이격 − 공구 길이. 이보다 작으면 팔을 접은 자세가 되어 AMR 이 출발 전에 거부한다
    /// (2026-11-11 천장 T: 리프트 100 mm 면 342 mm < 350 mm — 52건 전부 거부). 0 이면 보지 않는다.
    /// </summary>
    public double MinFlangeReachMm { get; init; }

    /// <summary>
    /// 팔 뻗음 상한 — 판정 자세·도면 순회에서 팔꿈치가 이 각도보다 펴지면(|J3| 가 작으면) 도달 불가로 본다 [도].
    /// 관절 한계만 보면 팔을 거의 다 뻗는 먼 정차가 통과하지만, 실기 컨트롤러는 그 근처에서 IK 를 못 풀거나 검사 중 추가 이동
    /// (경유점·피치 이동·비드 중심 맞춤)에서 막힌다(2026-11-11~12 천장 T 실적). 0 이면 <see cref="ElbowMarginDeg"/> 만 쓴다.
    /// </summary>
    public double MinElbowBendDeg { get; init; }

    /// <summary>판정에 쓰는 팔꿈치 하한 [도] — 특이점 여유와 팔 뻗음 상한 중 큰 값.</summary>
    public double ElbowLimitDeg => Math.Max(ElbowMarginDeg, MinElbowBendDeg);
    /// <summary>이 여유 미만이면 '주의' [m].</summary>
    public double WarnClearanceM { get; init; } = 0.05;

    /// <summary>접근 세 자세의 TCP 면 후퇴 [mm] — ② 거친 접근 · 정렬 준비(수평 맞춤 등) · 초점 검사(초점을 용접선 위에).</summary>
    public double[] StageStandoffsMm { get; init; } = { 400, 300, 0 };

    /// <summary>② 접근 자세의 IK 가지 고르기 — 출발 자세가 없는 면에서만 쓴다(자세 여유 최대, 또는 간섭 여유 최대).</summary>
    public ReachBranchRule BranchRule { get; init; } = ReachBranchRule.MaxMargin;

    /// <summary>
    /// 면 코드 → 운영 ② 가 출발하는 관절각 J1~J6 [도] — HD_AMR 티칭(상부 면 T·SU·PU = 홈 위치_2, 그 외 = 작업 준비 위치 ready).
    /// 있는 면은 ② 해를 이 자세에서 같은 가지로 고르고(가지 전환이면 도달 불가) 진입 경로(후퇴 평면)까지 검사한다.
    /// 없는 면은 종전처럼 <see cref="BranchRule"/> 로 판정한다. 설정 <c>Acs:Reach:StartJoints</c>.
    /// </summary>
    public IReadOnlyDictionary<string, double[]> StartJointsByWall { get; init; } = new Dictionary<string, double[]>();

    // ── 운영 ② 진입 규칙 — HD_AMR CobotInspectionMoveStep(2026-11-02 c490bb1·6791a6d)과 같은 값. 바뀌면 함께 맞출 것. ──
    /// <summary>출발 자세 → ② 해·진입점 관절별(J1~J5) 최대 이동 [도] — MaxEntryJointTravelDeg.</summary>
    public double MaxEntryJointTravelDeg { get; init; } = 140;
    /// <summary>
    /// 자세 사이(② → 정렬 준비 → 초점, 시작점 ② → 중간·끝점 ②) 관절별(J1~J5) 최대 변화 [도]. 운영은 이 사이를 광축 방향 직선 이동이나
    /// 같은 자세의 짧은 이동으로 잇는다(관절 변화 수십 도 이내). 넘으면 다른 IK 가지로 갈아탄 해라 운영이 갈 수 없다 — 도달 불가.
    /// </summary>
    public double MaxStageJumpDeg { get; init; } = 60;
    /// <summary>
    /// 용접선 앞 점 → 다음 점(시작 → 중간 → 끝, 영역 폭 ≤ 1.44 m 라 한 간격 ≤ 0.72 m) ② 해 사이 관절별 최대 변화 [도].
    /// 용접선 따라 이동이라 <see cref="MaxStageJumpDeg"/> 보다 넉넉히 두고, 팔꿈치·손목 반전은 크기와 무관하게 막는다.
    /// </summary>
    public double MaxSeamStepJumpDeg { get; init; } = 90;
    /// <summary>후퇴 평면 — ② 접근점 면 이격 평면에서 더 물러난 거리 [mm] — inspection.planarApproachRetreatMm 기본값.</summary>
    public double PlanarRetreatMm { get; init; } = 100;
    /// <summary>진입 MoveJ 가 출발 TCP 투영점에서 면을 따라 갈 수 있는 최대 거리 [mm] — MaxEntryInPlaneMm.</summary>
    public double MaxEntryInPlaneMm { get; init; } = 500;
    /// <summary>
    /// 평면 경유 진입(후퇴 평면·<see cref="MaxEntryInPlaneMm"/> 제한)을 쓰는 면 코드. null 이면 모든 면(종전).
    /// 여기에 없는 면은 <b>직접 진입</b>이다 — 들어갈 점까지 면 방향 거리 제한 없이 MoveJ 한 번에 가되, 그 관절 이동 경로를
    /// <see cref="EntryPathStepDeg"/> 간격으로 따라가며 면·자기 간섭을 본다(<see cref="ReachSimulator.JointPathBlocker"/>).
    /// HD_AMR <c>inspection.planarEntryWalls</c>(기본 PU,SU)와 같은 값이어야 한다. 설정 <c>Acs:Reach:PlanarEntryWalls</c>.
    /// </summary>
    public IReadOnlySet<string>? PlanarEntryWalls { get; init; }
    /// <summary>직접 진입 MoveJ 경로 점검 간격 — 관절별 최대 변화 [도]. HD_AMR <c>EntryPathStepDeg</c>.</summary>
    public double EntryPathStepDeg { get; init; } = 5;
    /// <summary>
    /// 직접 진입 MoveJ 경로가 지켜야 할 탱크 면 여유 [mm](자기 간섭은 닿는지만 본다). 출발·도착 자세의 여유가 이보다 작으면 그 작은 쪽을 기준으로 삼는다
    /// (경로가 양 끝보다 더 가까워지지 않을 것). HD_AMR <c>EntryPathMarginMm</c>.
    /// </summary>
    public double EntryPathMarginMm { get; init; } = 100;
    /// <summary>직선 경로 검증 간격 [mm] — PlanarSampleMm.</summary>
    public double PlanarSampleMm { get; init; } = 100;
    /// <summary>면 방향 거리가 이보다 짧으면 들어갈 점으로 바로 진입 [mm] — PlanarSkipMm.</summary>
    public double PlanarSkipMm { get; init; } = 50;
    /// <summary>경로 검증 점 사이 관절 최대 변화 [도] — MaxPathJointStepDeg.</summary>
    public double MaxPathJointStepDeg { get; init; } = 30;
    /// <summary>경로 손목 특이점 여유 |J5| [도] — PlanarWristMarginDeg.</summary>
    public double PlanarWristMarginDeg { get; init; } = 5;

    // ── 도면 순회(⑱ inspectionRun) 판정 — HD_AMR InspectionRunStep.CheckWaypointChainAsync 와 같은 규칙. 바뀌면 함께 맞출 것. ──
    /// <summary>
    /// 용접선 따라 검사 툴 순회(티칭 프로필 경유점)를 판정할지. 세 자세가 닿아도 순회 중 기울인 자세에서 손목 특이점·J6 안전 범위·
    /// 관절 한계에 걸리면 운영이 움직이지 않고 중단한다(실기 2026-11-03 L2-R02C05 H04 경유점 #5 J5 0.48°).
    /// </summary>
    public bool CheckTraversal { get; init; } = true;
    /// <summary>
    /// 순회 작업물 프레임 Top(점1)의 TCP 면 이격 [mm] — LINE 레시피는 ② 접근점(면 이격 inspection.approachStandoffMm, 현장 400)에서
    /// 점1(Top)을 기록하고 프레임 원점을 그 자리 − 33.8(면 쪽)에 둔다(2026-11-03·06·09 AMR 로그). 그래서 경유점 Z 33.8 = 이 이격.
    /// App 은 설정이 없으면 ② 접근 이격(<c>Acs:Reach:ApproachStandoffMm</c>)을 쓴다. 설정 <c>Acs:Reach:ScanStandoffMm</c>.
    /// </summary>
    public double ScanStandoffMm { get; init; } = 400;
    /// <summary>
    /// 순회 중 광축 기울기 최대 [도] — 경유점이 코러게이션 법선을 따라 용접선 진행 방향으로 앞뒤로 기운다(용접선에 수직인 면 내 축 둘레).
    /// 실기 H04 #5 = +39°. 프로필 경유점을 ACS 가 모르므로 용접선 전 구간에서 ±이 값까지 기울 수 있다고 보고 판정한다(포괄 근사).
    /// 설정 <c>Acs:Reach:ScanTiltDeg</c>.
    /// </summary>
    public double ScanTiltDeg { get; init; } = 40;
    /// <summary>
    /// 실제 티칭 프로필 경유점 — [용접선 시작점에서의 거리 mm(프레임 X), 광축 기울기 도(Ry θ), (선택) 프레임 Z mm] 순서 목록.
    /// 주면 포괄 근사(±<see cref="ScanTiltDeg"/>) 대신 이 순서대로 이어 판정한다(HD_AMR 프로필 WaypointsJson 의 X·Theta·Z 를 옮겨 적는다).
    /// Z 는 작업물 프레임 원점(Top − <see cref="ScanTopZMm"/>, 면 쪽) 기준이라 TCP 면 이격 = <see cref="ScanStandoffMm"/> + (Z − 33.8). 설정 <c>Acs:Reach:ScanTiltProfile</c>.
    /// </summary>
    public IReadOnlyList<double[]>? ScanTiltProfile { get; init; }
    /// <summary>작업물 프레임 Top 높이 [mm] — HD_AMR InternalWorkpieceFrame.DefaultCorrugationTopHeightMm. 점1(Top)이 이 Z 다.</summary>
    public const double ScanTopZMm = 33.8;
    /// <summary>기울기 판정 간격 [도] — HD_AMR 경로 검증 자세 간격(ChainSampleDeg 10°)과 같다.</summary>
    public double ScanTiltStepDeg { get; init; } = 10;
    /// <summary>용접선 따라 판정 간격 [mm] — 경로 검증 간격(100mm)보다 촘촘히(360mm 용접선 = 7점).</summary>
    public double ScanSampleMm { get; init; } = 60;
    /// <summary>경로 관절 한계 여유 [도] — PathJointLimitMarginDeg(하드 한계에서 이만큼 안쪽이어야 통과).</summary>
    public double PathJointLimitMarginDeg { get; init; } = 2;
    /// <summary>
    /// J6 안전 범위(실제 관절값, 대기 자세 −180° 쪽 감김 기준) [도] — HD_AMR WObjAttitude.SafeJ6Min/MaxDeg. 밖이면 센서 헤드–링크암 간섭·케이블 감김.
    /// </summary>
    /// <summary>
    /// 도면 순회 관절 여유 목표 [도] — 정차 최적화가 이 여유 이상으로 순회하는 용접선 수('여유 확보')를 먼저 늘리고, 같으면 최소 여유를 이 값까지 키운다
    /// (넘는 여유는 같게 본다). 실기 백테스트에서 모델 오차가 약 ±5° 라(경계 −4° 사례가 실기 통과) 그 두 배. 설정 <c>Acs:Reach:TraverseMarginTargetDeg</c>.
    /// </summary>
    public double TraverseMarginTargetDeg { get; init; } = DefaultTraverseMarginTargetDeg;
    public const double DefaultTraverseMarginTargetDeg = 10;
    public double SafeJ6MinDeg { get; init; } = -310;
    public double SafeJ6MaxDeg { get; init; } = -160;

    /// <summary>
    /// 진입 경로 판정에 J6 를 넣는다(기본 켬) — HD_AMR 은 진입점·경로의 자세를 ② 접근점 자세(J6 = 운영 값)로 고정해 풀고,
    /// 그때 달라지는 J6 를 관절 이동량(6축 모두)과 J6 안전 범위로 거른다. 끄면 종전처럼 J1~J5 만 본다.
    /// <see cref="OperationJ6Deg"/> 가 없으면 J6 를 모르므로 꺼진 것과 같다.
    /// </summary>
    public bool CheckEntryJ6 { get; init; } = true;

    /// <summary>
    /// 진입 경로에서 요구하는 자기 간섭 여유 [mm] — 팔·툴이 차체·리프트 기둥·신호탑에서 이보다 가까우면 그 진입 후보를 쓰지 않는다.
    /// HD_AMR 은 여유 0 으로 판정하지만 모델·정차 오차(도착 허용 0.2 m · 5.7°)로 경계가 흔들린다 — 실기 2026-11-12 PU 에서
    /// ACS 여유 5~16 mm 였던 진입 4건 중 3건이 실기에서 "자기 간섭"으로 거부됐다. 자세 고정 진입 판정(<see cref="CheckEntryJ6"/>)에만 쓴다.
    /// 설정 <c>Acs:Reach:EntrySelfMarginMm</c>.
    /// </summary>
    public double EntrySelfMarginMm { get; init; } = 30;

    public static ReachRobotModel Default { get; } = new();
}

/// <summary>검사 툴 외곽 원통 한 구간 — 플랜지 면 기준 광축 방향 [mm].</summary>
public sealed record ReachToolSegment(string Name, double FromMm, double ToMm, double RadiusMm);

/// <summary>검사 툴 외곽 상자 — 플랜지 기준 [mm] 최소·최대(x·y·z, z = 광축).</summary>
public sealed record ReachToolBox(string Name, double[] Min, double[] Max);

/// <summary>② 접근 자세의 IK 가지 규칙. 뒤 자세는 어느 쪽이든 바로 앞 해에 가장 가까운 허용 해.</summary>
public enum ReachBranchRule
{
    /// <summary>자세 여유 최대 — HD_AMR 운영 규칙.</summary>
    MaxMargin,
    /// <summary>자세 한계 안에서 간섭 여유(탱크 면·차체·기둥) 최대 — 운영 가지가 간섭일 때 다른 가지로 되는지 본다.</summary>
    MaxClearance,
}

/// <summary>탱크 면(또는 데크) 평면 — Normal 은 내부향 단위벡터. 탱크 단면이 볼록이라 모든 평면 앞(+)이 곧 내부다.</summary>
public sealed record ReachPlane(string Code, double[] Origin, double[] Normal)
{
    public const string DeckCode = "DECK";
    public bool IsFloor => Code is "B" or DeckCode;
    public double SignedDistance(double[] p) => Vec3.Dot(Vec3.Sub(p, Origin), Normal);
}

/// <summary>정차 — 도면 프레임 차체 중심 (x,y)[m], 차체 전방 yaw[rad], 층 바닥 z[m].</summary>
public sealed record ReachStation(double X, double Y, double Yaw, double FloorZ);

public enum ReachStage { Approach, Align, Focus }

/// <summary>간섭 판정 부품 — 캡슐(끝점·반경) 또는 상자(<paramref name="Corners"/> 8개: 아래 4 → 위 4, A·B 는 상자 축 양 끝 중심). 도면 m.</summary>
public sealed record ReachPart(string Name, double[] A, double[] B, double RadiusM, double ClearanceM, string NearestPlane,
    double[][]? Corners = null);

/// <summary>관절 표시점(도면 m).</summary>
public sealed record ReachJointMark(string Name, double[] Position);

/// <summary>IK 해 하나 — 자세 여유와(요청 시) 부품 간섭.</summary>
public sealed record ReachSolution(
    double[] JointsDeg, bool PostureOk, double MarginDeg, string Limiting,
    IReadOnlyList<ReachPart> Parts, IReadOnlyList<ReachJointMark> Joints, double[] Focus,
    bool WristDown, double MinClearanceM);

/// <summary>자세 하나(② / 정렬 준비 / 초점)의 해 전부와 운영 선택.</summary>
public sealed record ReachStageResult(ReachStage Stage, double StandoffMm, double[] TcpTarget,
    IReadOnlyList<ReachSolution> Solutions, int ChosenIndex)
{
    public ReachSolution? Chosen => ChosenIndex >= 0 ? Solutions[ChosenIndex] : null;
}

/// <summary>AMR 차체 상자 — 꼭짓점 8개(아래 4 → 위 4, 도면 m)와 바닥 외 면까지 여유.</summary>
public sealed record ReachBody(double[][] Corners, double ClearanceM, string NearestPlane);

/// <summary>
/// 운영 ② 진입 경로 판정 — 출발 자세 TCP 를 후퇴 평면에 투영한 점에서 진입점 후보를 골라
/// 진입 MoveJ → 후퇴 평면 MoveL → 법선 MoveL 경로가 이어지는지(HD_AMR 평면 경유 접근과 같은 규칙). 좌표 도면 m.
/// </summary>
/// <param name="StartTcp">출발 자세 TCP.</param>
/// <param name="Entry">고른 진입점(없으면 null).</param>
/// <param name="Plunge">들어갈 점 — ② 접근점에서 면 바깥으로 후퇴 거리만큼.</param>
/// <param name="InPlaneMm">투영점 → 들어갈 점 면 방향 거리.</param>
/// <param name="Rejects">거부된 후보와 사유(순서대로).</param>
public sealed record ReachEntry(bool Ok, double[] StartTcp, double[]? Entry, double[] Plunge, double InPlaneMm,
    int Candidates, IReadOnlyList<string> Rejects)
{
    public string Summary => Ok
        ? $"진입 경로 있음 — 면 방향 {InPlaneMm:0}mm" + (Rejects.Count == 0 ? "" : $" (앞 후보 {Rejects.Count}개 거부)")
        : $"진입 경로 없음 — 면 방향 {InPlaneMm:0}mm, 후보 {Candidates}개 모두 거부: {string.Join(" / ", Rejects.Take(4))}"
          + (Rejects.Count > 4 ? $" 외 {Rejects.Count - 4}건" : "");
}

/// <summary>용접선 점 하나를 한 정차·리프트에서 세 자세로 평가한 결과.</summary>
/// <param name="BranchChange">출발 자세에서 ② 해로 가려면 팔꿈치·손목을 뒤집거나 관절을 크게 돌려야 하면 그 사유.</param>
/// <param name="Entry">출발 자세가 있는 면의 진입 경로 판정(없는 면은 null).</param>
/// <param name="ScanOnly">용접선 중간·끝점 — 판정은 초점 자세만(②·정렬 준비는 시작점에서만 하므로 참고로 실은 자세).</param>
/// <param name="FlangeShort">플랜지 뻗음이 최소값 미만이면 그 사유(AMR 리치 사전 점검이 출발 전에 거부).</param>
public sealed record ReachPointResult(ReachBody Body, double[] ColumnTop, IReadOnlyList<ReachStageResult> Stages,
    string? BranchChange = null, ReachEntry? Entry = null, bool ScanOnly = false, string? FlangeShort = null)
{
    /// <summary>판정에 쓰는 자세 — 보통 세 자세, <see cref="ScanOnly"/> 면 초점 자세만.</summary>
    public IEnumerable<ReachStageResult> JudgedStages => ScanOnly ? Stages.Where(s => s.Stage == ReachStage.Focus) : Stages;

    /// <summary>판정 자세 모두 운영 선택 해가 있고 자세 한계 안이며, 가지 전환 없이 (출발 자세가 있으면) 진입 경로가 있는가.</summary>
    public bool Reachable => FlangeShort is null && JudgedStages.All(s => s.Chosen is { PostureOk: true })
        && BranchChange is null && Entry?.Ok != false;

    /// <summary>도달하지 못하는 첫 사유(도달이면 null).</summary>
    public string? Blocker =>
        JudgedStages.FirstOrDefault(s => s.Chosen is not { PostureOk: true }) is { } bad
            ? $"{bad.Stage} {(bad.Chosen is { } c ? c.Limiting : "해 없음")}"
            : FlangeShort ?? BranchChange ?? (Entry is { Ok: false } e ? e.Summary : null);

    /// <summary>차체·판정 자세 운영 선택 해 부품 중 가장 작은 여유 [m]. 도달하지 못하면 차체만.</summary>
    public double MinClearanceM => JudgedStages.Select(s => s.Chosen?.MinClearanceM ?? double.PositiveInfinity)
        .Append(Body.ClearanceM).Min();
}

public enum ReachVerdict { Clear, Collision, Unreachable }

/// <summary>용접선 하나의 종합 판정 — 리프트를 훑어 간섭 없이 닿는 높이가 있으면 Clear.</summary>
/// <param name="StrokeMm">Clear 면 여유가 가장 큰 리프트, Collision 이면 간섭이 가장 덜한 리프트, 아니면 null.</param>
/// <param name="YawIndex">바닥·천장처럼 차체 방향이 자유일 때 고른 후보 순번.</param>
/// <param name="Blocker">Unreachable 일 때 사유 — 세 자세는 닿는데 출발 자세 가지·진입 경로로 막힌 경우를 먼저 고른다.</param>
/// <param name="TraverseMarginDeg">고른 리프트·방향에서 도면 순회의 가장 빡빡한 관절 여유 [도](순회 판정을 안 했으면 null).</param>
public sealed record ReachSeamSurvey(ReachVerdict Verdict, double? StrokeMm, double? MinClearanceM, int YawIndex,
    string? Blocker = null, double? TraverseMarginDeg = null);

/// <summary>
/// 리치·간섭 시뮬레이터. 정차·리프트·정차 이동이 주어지면 용접선 점마다 운영과 같은 세 접근 자세
/// (② 400 → 정렬 준비 300 → 초점 0, <see cref="ReachRobotModel.StageStandoffsMm"/>)를 FR5-WML 모델로 풀고,
/// 차체·리프트·코봇 링크·검사 툴 캡슐이 탱크 면에서 얼마나 떨어져 있는지 잰다.
///
/// 운영 선택 규칙: ② 는 자세 여유 최대 해(<see cref="ReachRobotModel.BranchRule"/> 로 간섭 여유 최대도 가능),
/// 그 뒤 자세는 바로 앞 자세 해에 가장 가까운 허용 해(같은 IK 가지로 연속 이동).
/// TOOL 은 광축(+Z)이 면 법선 반대(면을 바라봄)이다. 광축 둘레 회전은 J6 만 바꾸므로(J1~J5 무관) IK 해의 J6 를
/// 운영 값(<see cref="ReachRobotModel.OperationJ6Deg"/>)으로 바꿔 한쪽으로 치우친 툴 상자의 실제 방향을 잰다.
/// </summary>
public static partial class ReachSimulator
{
    /// <param name="seamPoint">용접선 점(도면 m).</param>
    /// <param name="wallNormal">면 내부향 단위 법선(도면).</param>
    /// <param name="offsetForwardMm">정차 이동 — 차체 전방(+ = 벽 쪽) [mm].</param>
    /// <param name="offsetLeftMm">정차 이동 — 차체 좌측 [mm].</param>
    /// <param name="withGeometry">false 면 운영 선택 해만 부품 간섭을 계산한다(전체 판정용 — 빠르다).</param>
    /// <param name="wallCode">면 코드 — <see cref="ReachRobotModel.StartJointsByWall"/> 에 출발 자세가 있으면 ② 를 그 자세의
    /// 가지로 고르고 진입 경로를 검사한다. null 이거나 출발 자세가 없으면 종전 규칙.</param>
    /// <param name="checkEntry">false 면 진입 경로를 풀지 않는다(조사가 리프트 후보를 먼저 고른 뒤 상위 후보만 본다).</param>
    /// <param name="seedJoints">이 점의 ② 해를 고를 기준 관절각 — 용접선 중간·끝점은 바로 앞 점의 ② 해를 넘긴다(운영은 시작점 자세에서
    /// 용접선을 따라 이어 가므로 같은 가지). 주면 출발 자세 가지·진입 경로는 보지 않고(<paramref name="wallCode"/> 무시), 기준에 가장 가까운
    /// 해를 고르며 팔꿈치·손목이 반전되면 도달 불가.</param>
    public static ReachPointResult Evaluate(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, ReachStation station,
        double[] seamPoint, double[] wallNormal, double strokeMm,
        double offsetForwardMm = 0, double offsetLeftMm = 0, bool withGeometry = true, string? wallCode = null,
        bool checkEntry = true, double[]? seedJoints = null)
    {
        var frame = new BaseFrame(model, station, strokeMm, offsetForwardMm, offsetLeftMm);
        var body = Body(model, planes, frame);
        var columnTop = frame.AmrToDrawing(0, 0, model.MountZMm + strokeMm);
        var start = seedJoints is null ? StartJoints(model, wallCode) : null;
        string? branchChange = null;
        ReachEntry? entry = null;

        var stageKinds = new[] { ReachStage.Approach, ReachStage.Align, ReachStage.Focus };
        var count = Math.Min(model.StageStandoffsMm.Length, stageKinds.Length);

        // 자세 하나를 푼다 — reference 가 있으면 그 해에 가장 가까운 해, 없으면 출발 자세 가지 또는 가지 규칙.
        (ReachStageResult Stage, double[]? J) SolveStage(int i, double[]? reference)
        {
            var standoff = model.StageStandoffsMm[i];
            var tcp = Vec3.Add(seamPoint, Vec3.Scale(wallNormal, standoff / 1000.0));
            var evaluated = SolveIk(model, frame, tcp, wallNormal).Select(j => (J: j, P: Posture(model, j))).ToList();
            var byClearance = reference is null && start is null && model.BranchRule == ReachBranchRule.MaxClearance;
            var solved = withGeometry || byClearance
                ? evaluated.Select(s => Solve(model, planes, frame, s.J, s.P)).ToList()
                : null;
            var chosen = reference is not null ? Nearest(evaluated, reference) ?? BestMargin(evaluated)
                : start is not null ? SameBranch(model, evaluated.Select(s => s.J).ToList(), start) ?? BestMargin(evaluated)
                : byClearance ? BestClearance(solved!) : BestMargin(evaluated);
            var sols = evaluated.Select((s, k) => solved?[k] ?? (k == chosen
                ? Solve(model, planes, frame, s.J, s.P)
                : new ReachSolution(s.J, s.P.Ok, s.P.Margin, s.P.Limiting, Array.Empty<ReachPart>(),
                    Array.Empty<ReachJointMark>(), tcp, false, double.PositiveInfinity))).ToList();
            return (new ReachStageResult(stageKinds[i], standoff, tcp, sols, chosen), chosen >= 0 ? evaluated[chosen].J : null);
        }
        ReachStageResult Skipped(int i) => new(stageKinds[i], model.StageStandoffsMm[i],
            Vec3.Add(seamPoint, Vec3.Scale(wallNormal, model.StageStandoffsMm[i] / 1000.0)), Array.Empty<ReachSolution>(), -1);

        var stages = new List<ReachStageResult>(count);
        if (seedJoints is null)
        {
            // 용접선 시작점 — 운영 ② → 정렬 준비 → 초점. 뒤 자세는 앞 자세 해에서 이어 가고, 관절이 크게 튀면 다른 가지다.
            double[]? previous = null;
            for (var i = 0; i < count; i++)
            {
                var (stage, j) = SolveStage(i, previous);
                stages.Add(stage);
                if (j is null) break;   // 운영도 이 자세에서 멈춘다 — 뒤 자세는 풀지 않는다
                if (previous is null && start is not null)
                    branchChange = BranchChangeReason(model, start, j);
                else if (previous is not null && branchChange is null
                         && StageJump(previous, j) is { Travel: var jump, Joint: var jn } && jump > model.MaxStageJumpDeg)
                    branchChange = $"자세 사이 가지 전환 — {StageName(stageKinds[i])} J{jn + 1} {jump:0}° 이동 > {model.MaxStageJumpDeg:0}°";
                previous = j;
            }
        }
        else
        {
            // 용접선 중간·끝점 — 운영은 시작점 초점 자세에서 용접선을 따라 스캔한다. 초점 자세만 앞 점 초점 해에서 이어 판정하고,
            // ②·정렬 준비는 그 해에서 거꾸로 이어 참고로만 싣는다(판정 제외 — ReachPointResult.ScanOnly).
            var focus = SolveStage(count - 1, seedJoints);
            if (focus.J is { } fj)
            {
                if (ElbowFlip(seedJoints, fj) || WristFlip(seedJoints, fj))
                    branchChange = $"용접선 따라 가지 전환 — 앞 점에서 {(ElbowFlip(seedJoints, fj) ? $"팔꿈치(J3 {seedJoints[2]:0}° → {fj[2]:0}°)" : $"손목(J5 {seedJoints[4]:0}° → {fj[4]:0}°)")} 반전";
                else if (StageJump(seedJoints, fj) is { Travel: var sj, Joint: var sn } && sj > model.MaxSeamStepJumpDeg)
                    branchChange = $"용접선 따라 가지 전환 — 앞 점에서 J{sn + 1} {sj:0}° 이동 > {model.MaxSeamStepJumpDeg:0}°";
            }
            var refs = new ReachStageResult[count];
            refs[count - 1] = focus.Stage;
            var next = focus.J;
            for (var i = count - 2; i >= 0; i--)
            {
                // 참고 자세는 판정에 쓰지 않는다(ScanOnly) — 조사·최적화(withGeometry: false)에서는 풀지 않고 상세 화면에서만 푼다.
                if (next is null || !withGeometry) { refs[i] = Skipped(i); continue; }
                var (stage, j) = SolveStage(i, next);
                refs[i] = stage;
                next = j;
            }
            stages.AddRange(refs);
        }
        // 진입 경로는 세 자세가 닿고 가지 전환이 없을 때만 본다 — 앞에서 막히면 운영도 진입하지 않는다(IK 가 가장 많이 드는 단계).
        if (start is not null && checkEntry && branchChange is null && stages.Count > 0
            && stages.All(s => s.Chosen is { PostureOk: true }))
            entry = PlanEntry(model, planes, frame, start, stages[0].TcpTarget, wallNormal, stages[0].Chosen?.JointsDeg,
                UsesPlanarEntry(model, wallCode));
        return new ReachPointResult(body, columnTop, stages, branchChange, entry, ScanOnly: seedJoints is not null,
            FlangeShort: FlangeShortReason(model, station, strokeMm, seamPoint, wallNormal, offsetForwardMm, offsetLeftMm));
    }

    /// <summary>
    /// 플랜지 뻗음(BASE→면 법선 거리 − ② 접근 이격 − 공구 길이) [mm] — HD_AMR <c>ApproachReach.Check</c> 와 같은 식.
    /// 면이 평면이라 그 면의 어느 점으로 재도 같다.
    /// </summary>
    public static double FlangeReachMm(ReachRobotModel model, ReachStation station, double strokeMm, double[] facePoint,
        double[] wallNormal, double offsetForwardMm = 0, double offsetLeftMm = 0)
    {
        var f = new BaseFrame(model, station, strokeMm, offsetForwardMm, offsetLeftMm);
        var n = Vec3.Normalize(wallNormal) ?? new[] { 0.0, 0, 1 };
        var normalM = Vec3.Dot(Vec3.Sub(f.BaseToDrawing(new[] { 0.0, 0, 0 }), facePoint), n);
        return normalM * 1000 - model.StageStandoffsMm[0] - model.ToolZMm;
    }

    /// <summary>
    /// 이 면에 최소 플랜지 뻗음 규칙을 적용하는가 — 천장(안쪽 법선이 아래)만 뺀다(2026-11-13). HD_AMR
    /// <c>ApproachReach.AppliesTo</c>(wall_code ≠ T)와 같은 규칙이다 — 바꾸면 함께 맞출 것.
    /// 근거 실측(뻗음 230mm 손목 특이점 3회)은 수직벽이었다. 천장은 툴이 위를 향해 ② 자세 J5 가 90° 근처에 머물러
    /// 접힌 팔에서도 손목 특이점이 생기지 않고(리프트를 올려 뻗음 143mm 까지 자세 여유 유지 — 더 가까우면 상완–리프트
    /// 기둥 간섭·가지 전환이 따로 막는다), 이 규칙은 리프트를 올린 정차만 막아 먼 용접선의 팔 뻗음 실패를 풀 수단을 없앴다.
    /// 경사면은 단독으로 막는 경우가 없고 바닥은 걸리지 않아 그대로 둔다.
    /// </summary>
    public static bool FlangeRuleApplies(double[] wallNormal)
        => !(Vec3.Normalize(wallNormal) is { } n && n[2] < -0.99);

    /// <summary>플랜지 뻗음이 <see cref="ReachRobotModel.MinFlangeReachMm"/> 미만이면 사유(아니거나 규칙이 꺼져 있거나 천장이면 null).</summary>
    public static string? FlangeShortReason(ReachRobotModel model, ReachStation station, double strokeMm, double[] facePoint,
        double[] wallNormal, double offsetForwardMm = 0, double offsetLeftMm = 0)
    {
        if (model.MinFlangeReachMm <= 0 || model.StageStandoffsMm.Length == 0 || !FlangeRuleApplies(wallNormal)) return null;
        var reach = FlangeReachMm(model, station, strokeMm, facePoint, wallNormal, offsetForwardMm, offsetLeftMm);
        return reach < model.MinFlangeReachMm
            ? $"플랜지 뻗음 부족 — {reach:0}mm < 최소 {model.MinFlangeReachMm:0}mm (면에 너무 가깝거나 리프트가 높음 — AMR 사전 점검이 거부)"
            : null;
    }

    /// <summary>
    /// 도면 순회 직전 자세 — 순회 높이(<see cref="ReachRobotModel.ScanStandoffMm"/> − Top 높이)에 가장 가까운 판정 자세의 해.
    /// 순회를 ② 접근 높이에서 하면 ② 해, 초점 거리에서 하면(실기 2026-11-11 이후 — 레이저 WD 설정 뒤 점1 을 잡는다) 초점 해다.
    /// </summary>
    public static double[]? TraverseSeed(ReachPointResult r, ReachRobotModel model)
    {
        var height = model.ScanStandoffMm - ReachRobotModel.ScanTopZMm;
        return r.Stages.Where(s => s.Chosen is not null).OrderBy(s => Math.Abs(s.StandoffMm - height))
            .FirstOrDefault()?.Chosen?.JointsDeg;
    }

    /// <summary>
    /// 도면 순회 직전 자세 — 시작점 ② 접근 자세 해(J6 운영 값). LINE 레시피는 이 자리(면 이격 = ② 이격)에서 작업물 프레임 점1(Top)을 잡고
    /// 순회를 시작하며, 손목·팔꿈치 가지와 J6 감김의 기준도 이 자세다. 없으면 null.
    /// </summary>
    public static double[]? TraverseSeed(ReachPointResult r) =>
        r.Stages.FirstOrDefault(s => s.Stage == ReachStage.Approach)?.Chosen?.JointsDeg;

    /// <summary>점 평가에서 다음 점으로 넘길 기준 — 초점 자세 해(용접선 스캔은 초점 거리에서 이어 간다). 없으면 null.</summary>
    public static double[]? ScanSeed(ReachPointResult r) =>
        r.Stages.LastOrDefault(s => s.Stage == ReachStage.Focus)?.Chosen?.JointsDeg;

    private static (double Travel, int Joint) StageJump(double[] a, double[] b) =>
        Enumerable.Range(0, 5).Select(k => (Math.Abs(a[k] - b[k]), k)).MaxBy(t => t.Item1);

    private static string StageName(ReachStage s) => s switch
    {
        ReachStage.Approach => "② 접근",
        ReachStage.Align => "정렬 준비",
        _ => "초점 검사",
    };

    /// <summary>면의 운영 ② 출발 관절각(없으면 null).</summary>
    public static double[]? StartJoints(ReachRobotModel model, string? wallCode) =>
        wallCode is not null && model.StartJointsByWall.TryGetValue(wallCode, out var s) && s is { Length: >= 5 } ? s : null;

    /// <summary>정차 하나에서 AMR 차체(리프트·팔과 무관)의 면 여유만 잰다 — 정차 후보를 빠르게 거를 때 쓴다.</summary>
    public static ReachBody BodyAt(ReachRobotModel model, IReadOnlyList<ReachPlane> planes, ReachStation station) =>
        Body(model, planes, new BaseFrame(model, station, 0, 0, 0));

    /// <summary>
    /// 용접선(시작·중간·끝 점)을 정차 후보 방향·리프트 높이마다 평가해 간섭 없이 닿는 조합을 찾는다.
    /// 출발 자세가 있는 면의 운영 진입(가지 전환·진입 경로)은 시작점(<paramref name="seamPoints"/>[0])에만 적용한다 —
    /// 운영 ② 는 용접선 시작점으로 진입하고, 중간·끝은 그 자세에서 이어 간다 — 그래서 중간·끝점은 바로 앞 점의 ② 해를 기준으로
    /// 가장 가까운 해를 고른다(<see cref="Evaluate"/> seedJoints — 팔꿈치·손목 반전이나 같은 점 자세 사이 관절 점프가
    /// <see cref="ReachRobotModel.MaxStageJumpDeg"/> 를 넘으면 도달 불가).
    /// 진입 경로는 IK 가 가장 많이 드는 단계라, 세 자세가 닿는 (방향, 리프트) 후보를 먼저 모두 모은 뒤
    /// 좋은 순서(간섭 없음 → 여유 큰 순)로 하나씩 보며 통과하는 첫 후보에서 멈춘다.
    /// </summary>
    public static ReachSeamSurvey Survey(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, IReadOnlyList<ReachStation> stations,
        IReadOnlyList<double[]> seamPoints, double[] wallNormal, IReadOnlyList<double> strokesMm, string? wallCode = null)
    {
        var candidates = new List<(double H, double Cl, int Yaw, double[] Seed)>();
        string? blocker = null, entryBlocker = null;
        for (var yi = 0; yi < stations.Count; yi++)
            foreach (var h in strokesMm)
            {
                // 플랜지 뻗음이 모자란 (정차, 리프트)는 IK 를 풀지 않는다 — AMR 이 출발 전에 거부한다
                if (seamPoints.Count > 0 && FlangeShortReason(model, stations[yi], h, seamPoints[0], wallNormal) is { } shortReach)
                {
                    blocker ??= shortReach;
                    continue;
                }
                var min = double.PositiveInfinity;
                var reachable = true;
                double[]? seed = null;   // 중간·끝점은 시작점 ② 해의 가지를 따라간다(운영과 같게)
                double[]? startSeed = null;   // 도면 순회 직전 자세 = 시작점 초점 자세
                for (var i = 0; i < seamPoints.Count; i++)
                {
                    var r = Evaluate(model, planes, stations[yi], seamPoints[i], wallNormal, h, withGeometry: false,
                        wallCode: i == 0 ? wallCode : null, checkEntry: false, seedJoints: seed);
                    seed = ScanSeed(r);   // 다음 점은 이 점의 초점 자세에서 용접선을 따라 이어 간다
                    if (i == 0) startSeed = TraverseSeed(r, model);
                    if (!r.Reachable)
                    {
                        reachable = false;
                        blocker ??= r.Blocker;
                        if (r.JudgedStages.All(s => s.Chosen is { PostureOk: true })) entryBlocker ??= r.Blocker;
                        break;
                    }
                    min = Math.Min(min, r.MinClearanceM);
                }
                if (reachable) candidates.Add((h, min, yi, startSeed!));
            }

        var needEntry = seamPoints.Count > 0 && StartJoints(model, wallCode) is not null;
        string? traverseBlocker = null;
        foreach (var c in candidates.OrderByDescending(c => c.Cl >= 0).ThenByDescending(c => c.Cl))
        {
            if (needEntry)
            {
                var e = Evaluate(model, planes, stations[c.Yaw], seamPoints[0], wallNormal, c.H, withGeometry: false, wallCode: wallCode);
                if (!e.Reachable) { entryBlocker ??= e.Blocker; continue; }
            }
            double? margin = null;
            if (TraverseSeam(model, stations[c.Yaw], c.H, seamPoints, wallNormal, c.Seed) is { } t)
            {
                if (!t.Ok) { traverseBlocker ??= t.Blocker; continue; }
                margin = t.MinMarginDeg;
            }
            return new ReachSeamSurvey(c.Cl >= 0 ? ReachVerdict.Clear : ReachVerdict.Collision, c.H, c.Cl, c.Yaw,
                TraverseMarginDeg: margin);
        }
        return new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0, traverseBlocker ?? entryBlocker ?? blocker);
    }

    /// <summary>
    /// 용접선(시작 … 끝 점) 도면 순회 판정 — <see cref="ReachRobotModel.CheckTraversal"/> 가 꺼져 있거나 점이 없으면 null.
    /// <paramref name="startSeed"/> 는 시작점 ② 접근 자세 해(<see cref="TraverseSeed"/>).
    /// </summary>
    public static ReachTraversal? TraverseSeam(ReachRobotModel model, ReachStation station, double strokeMm,
        IReadOnlyList<double[]> seamPoints, double[] wallNormal, double[]? startSeed,
        double offsetForwardMm = 0, double offsetLeftMm = 0) =>
        model.CheckTraversal && seamPoints.Count > 0 && startSeed is { Length: >= 6 }
            ? Traverse(model, station, strokeMm, seamPoints[0], seamPoints[^1], wallNormal, startSeed, offsetForwardMm, offsetLeftMm)
            : null;

    /// <summary>층의 장애물 평면 — 탱크 면 + (2층 이상) 그 층 데크면.</summary>
    public static IReadOnlyList<ReachPlane> PlanesForLevel(IEnumerable<ReachPlane> walls, double floorZ, int level)
    {
        var list = walls.ToList();
        if (level > 1) list.Add(new ReachPlane(ReachPlane.DeckCode, new[] { 0.0, 0, floorZ }, new[] { 0.0, 0, 1 }));
        return list;
    }

    // ── 운영 ② 출발 자세·진입 경로(HD_AMR CobotInspectionMoveStep 평면 경유 접근과 같은 규칙) ──────────
    // ② 해를 고를 때는 J6 을 보지 않는다(광축 둘레 회전은 J6 만 바꾼다). 진입 경로는 J6 을 본다 — HD_AMR 은 진입점·경로를
    // ② 접근점 자세(J6 = 운영 값) 그대로 풀기 때문에 위치가 달라지면 J6 이 달라지고, 그 값이 관절 이동량·J6 안전 범위에 걸린다
    // (실기 2026-11-12 PU: 면 방향 536~828 mm 떨어진 용접선 7건이 "진입 관절 이동 221°"·"J6 안전 범위 밖"으로 거부).
    // 손목 반전 해는 J5 부호 반전으로 따로 거른다(HD_AMR IsWristFlip — 140° 완화로 이동 한계만으로는 통과할 수 있다).

    /// <summary>TCP(도면)에서 광축이 면을 바라보는 자세의 IK 해(범위 밖 관절은 ±360 감음).</summary>
    private static List<double[]> SolveIk(ReachRobotModel m, BaseFrame f, double[] tcpDrawing, double[] wallNormal) =>
        m.Kinematics.InverseFlange(FlangeTarget(m, f, tcpDrawing, wallNormal)).Select(j => WithOperationJ6(m, Normalize(m, j))).ToList();

    /// <summary>J6 를 운영 값으로 — IK 값에 가장 가까운 감김(같은 물리각). 광축 둘레 회전이라 TCP·광축·J1~J5 는 그대로다.</summary>
    private static double[] WithOperationJ6(ReachRobotModel m, double[] j)
    {
        if (m.OperationJ6Deg is not { } target || j.Length < 6) return j;
        var r = (double[])j.Clone();
        r[5] = target + 360.0 * Math.Round((j[5] - target) / 360.0);
        return r;
    }

    private static bool InRange(ReachRobotModel m, double[] j) =>
        Enumerable.Range(0, m.JointMinDeg.Length).All(i => j[i] >= m.JointMinDeg[i] && j[i] <= m.JointMaxDeg[i]);

    private static double Travel(double[] a, double[] b) => Enumerable.Range(0, 5).Max(i => Math.Abs(a[i] - b[i]));

    private static bool ElbowFlip(double[] reference, double[] j) =>
        Math.Sign(reference[2]) != 0 && Math.Sign(j[2]) != 0 && Math.Sign(reference[2]) != Math.Sign(j[2]);

    /// <summary>손목 가지 전환 — J5 부호가 다르면 J5=0 특이점을 지나며 J4·J6 이 크게 돈다(HD_AMR IsWristFlip).</summary>
    private static bool WristFlip(double[] reference, double[] j) =>
        Math.Sign(reference[4]) != 0 && Math.Sign(j[4]) != 0 && Math.Sign(reference[4]) != Math.Sign(j[4]);

    /// <summary>
    /// 기준 자세에서 가장 가까운(J1~J5 이동 합) 관절 범위 안 해 — 팔꿈치 가지가 같은 해를 먼저 고른다
    /// (HD_AMR GetInverseKinKeepWrist). 없으면 null.
    /// </summary>
    private static int? SameBranch(ReachRobotModel m, IReadOnlyList<double[]> sols, double[] reference)
    {
        int? best = null;
        var bestCost = double.PositiveInfinity;
        for (var i = 0; i < sols.Count; i++)
        {
            if (!InRange(m, sols[i])) continue;
            var cost = Enumerable.Range(0, 5).Sum(k => Math.Abs(sols[i][k] - reference[k])) + (ElbowFlip(reference, sols[i]) ? 10000 : 0);
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        return best;
    }

    /// <summary>기준 자세에서 J1~J5 이동 합이 가장 작은 관절 범위 안 해(HD_AMR GetInverseKinNearest). 없으면 null.</summary>
    private static double[]? NearestInRange(ReachRobotModel m, IReadOnlyList<double[]> sols, double[] seed) =>
        sols.Where(j => InRange(m, j)).MinBy(j => Enumerable.Range(0, 5).Sum(k => Math.Abs(j[k] - seed[k])));

    /// <summary>
    /// 출발 자세 → ② 해가 운영 ② 가 하지 않는 가지 전환인지(HD_AMR ReachMap.BranchChangeReason) — 팔꿈치(J3)·손목(J5) 부호 반전,
    /// 관절별(J1~J5) 이동 &gt; <see cref="ReachRobotModel.MaxEntryJointTravelDeg"/>. 아니면 null.
    /// </summary>
    public static string? BranchChangeReason(ReachRobotModel m, double[] start, double[] approach)
    {
        var parts = new List<string>();
        if (ElbowFlip(start, approach))
            parts.Add($"팔꿈치 반전(J3 {start[2]:0.0}° → {approach[2]:0.0}°)");
        if (Math.Sign(start[4]) != 0 && Math.Sign(start[4]) != Math.Sign(approach[4]))
            parts.Add($"손목 반전(J5 {start[4]:0.0}° → {approach[4]:0.0}°)");
        var (joint, travel) = Enumerable.Range(0, 5).Select(i => (i, Math.Abs(approach[i] - start[i]))).MaxBy(t => t.Item2);
        if (travel > m.MaxEntryJointTravelDeg)
            parts.Add($"J{joint + 1} {travel:0}° 이동 > {m.MaxEntryJointTravelDeg:0}°");
        return parts.Count == 0 ? null : "출발 자세에서 가지 전환 — " + string.Join(", ", parts);
    }

    /// <summary>
    /// 평면 경유 진입 — 출발 TCP 를 ② 접근점 이격 평면에서 <see cref="ReachRobotModel.PlanarRetreatMm"/> 더 물러난 후퇴 평면에
    /// 투영하고, 진입점 후보(① 들어갈 점 직접: 면 방향 500mm 이내일 때만 ② 투영점부터 100mm 간격, 500mm 까지)마다
    /// 진입 해(같은 가지·관절 이동 ≤140°·손목 반전 없음·자기 간섭 없음)와 그 뒤 경로(후퇴 평면 → 들어갈 점 → 접근점, 100mm 간격)의
    /// 관절 튐·팔꿈치 반전·손목 특이점·자기 간섭을 본다. 자세는 전 구간 ② 자세로 고정.
    /// </summary>
    /// <summary>이 면이 평면 경유 진입을 쓰는가 — <see cref="ReachRobotModel.PlanarEntryWalls"/> 가 없으면 모든 면.</summary>
    public static bool UsesPlanarEntry(ReachRobotModel m, string? wallCode) =>
        m.PlanarEntryWalls is null || (wallCode is not null && m.PlanarEntryWalls.Contains(wallCode));

    /// <summary>
    /// 진입점 후보 — 평면 경유면 ① 들어갈 점 직접(면 방향 <see cref="ReachRobotModel.MaxEntryInPlaneMm"/> 이내일 때만) ② 투영점부터
    /// 100 mm 간격(같은 제한까지). 직접 진입이면 제한 없이 ① 들어갈 점 직접 ② 투영점부터 들어갈 점 앞까지 전부.
    /// </summary>
    private static List<(double[] P, bool Direct)> EntryCandidates(ReachRobotModel m, double[] projected, double[] plunge,
        double inPlaneMm, bool planar)
    {
        var candidates = new List<(double[] P, bool Direct)>();
        if (inPlaneMm < m.PlanarSkipMm) { candidates.Add((plunge, true)); return candidates; }
        var cap = planar ? m.MaxEntryInPlaneMm : double.PositiveInfinity;
        if (inPlaneMm <= cap) candidates.Add((plunge, true));
        candidates.Add((projected, false));
        var pts = Sample(projected, plunge, m.PlanarSampleMm);
        var step = inPlaneMm / pts.Count;
        for (var k = 0; k < pts.Count - 1; k++)
            if ((k + 1) * step <= cap + 1e-6) candidates.Add((pts[k], false));
        return candidates;
    }

    /// <summary>
    /// 직접 진입 MoveJ(관절 보간) 경로가 막히는 사유, 지나갈 수 있으면 null. <paramref name="from"/> → <paramref name="to"/> 를
    /// 관절별 최대 <see cref="ReachRobotModel.EntryPathStepDeg"/> 간격으로 나눠, 중간 자세마다 자기 간섭이 없고 탱크 면 여유가
    /// min(<see cref="ReachRobotModel.EntryPathMarginMm"/>, 출발 여유, 도착 여유) 아래로 내려가지 않는지 본다.
    /// 평면 경유가 하던 "팔이 면 쪽으로 휘둘리지 않는가"를 경로 자체로 확인한다.
    /// </summary>
    public static string? JointPathBlocker(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, ReachStation station, double strokeMm,
        double[] from, double[] to) => JointPathBlocker(m, planes, new BaseFrame(m, station, strokeMm, 0, 0), from, to);

    private static string? JointPathBlocker(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f, double[] from, double[] to)
    {
        var n = Math.Min(from.Length, to.Length);
        var travel = Enumerable.Range(0, n).Max(i => Math.Abs(to[i] - from[i]));
        var steps = (int)Math.Ceiling(travel / Math.Max(0.5, m.EntryPathStepDeg));
        if (steps <= 1) return null;
        // 면 여유는 팔 캡슐·툴 상자를 탱크 면과만 잰다. 자기 간섭(차체·기둥·신호탑)은 닿는지만 본다 — 홈 자세는 원래
        // 기둥·차체에 가까워 "양 끝보다 가까워지지 않을 것"을 자기 간섭에 쓰면 실기에서 늘 하던 진입까지 막는다.
        (double Mm, string Part, string Near) Wall(double[] j)
        {
            var fr = m.Kinematics.Frames(j);
            var segs = ArmSegments(m, fr);
            var worst = segs[..^1].Select(q => Part(q.Name, f.BaseToDrawing(q.A), f.BaseToDrawing(q.B), q.RadiusMm, planes))
                .Concat(m.ToolBoxes.Select(t => ToolBoxPart(m, t, fr[6], f, planes)))
                .MinBy(q => q.ClearanceM)!;
            return (worst.ClearanceM * 1000, worst.Name, worst.NearestPlane);
        }
        double[] At(double t) => to.Select((v, i) => i < n ? from[i] + (v - from[i]) * t : v).ToArray();
        var floor = Math.Min(m.EntryPathMarginMm, Math.Min(Wall(At(0)).Mm, Wall(to).Mm)) - 1.0;
        for (var k = 1; k < steps; k++)
        {
            var j = At((double)k / steps);
            if (SelfClearance(m, f.BaseZMm, j) is { ClearanceMm: < 0 } self)
                return $"관절 이동 경로 {100.0 * k / steps:0}% 지점 자기 간섭 ({self.Part}–{self.Obstacle})";
            var w = Wall(j);
            if (w.Mm < floor)
                return $"관절 이동 경로 {100.0 * k / steps:0}% 지점 면 여유 {w.Mm:0}mm < {floor + 1:0}mm ({w.Part}–{w.Near})";
        }
        return null;
    }

    private static ReachEntry PlanEntry(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f, double[] start,
        double[] approach, double[] normal, double[]? approachJoints = null, bool planar = true)
    {
        if (m.CheckEntryJ6 && m.OperationJ6Deg is not null && approachJoints is { Length: >= 6 } && start.Length >= 6)
            return PlanEntryFixedPose(m, planes, f, start, approach, normal, approachJoints, planar);
        var fr = m.Kinematics.Frames(start);
        var cur = f.BaseToDrawing(Vec3.Add(Mat4.Origin(fr[6]), Vec3.Scale(Mat4.AxisZ(fr[6]), m.ToolZMm)));
        var r = m.PlanarRetreatMm / 1000.0;
        var h = Vec3.Dot(Vec3.Sub(cur, approach), normal);
        var projected = Vec3.Sub(cur, Vec3.Scale(normal, h - r));
        var plunge = Vec3.Add(approach, Vec3.Scale(normal, r));
        var inPlaneMm = Vec3.Length(Vec3.Sub(plunge, projected)) * 1000;

        var candidates = EntryCandidates(m, projected, plunge, inPlaneMm, planar);

        string Fmt(double[] p) => string.Join(", ", f.DrawingToBase(p).Select(v => v.ToString("0")));
        var rejects = new List<string>();
        foreach (var (cand, direct) in candidates)
        {
            var label = direct ? "들어갈 점 직접" : $"후퇴 평면 [{Fmt(cand)}]";
            var sols = SolveIk(m, f, cand, normal);
            if (SameBranch(m, sols, start) is not { } ci) { rejects.Add($"{label}: 도달 불가"); continue; }
            var jc = sols[ci];
            var travel = Travel(start, jc);
            if (travel > m.MaxEntryJointTravelDeg) { rejects.Add($"{label}: 진입 관절 이동 {travel:0}° > {m.MaxEntryJointTravelDeg:0}°"); continue; }
            if (ElbowFlip(start, jc)) { rejects.Add($"{label}: 팔꿈치 가지 전환"); continue; }
            if (WristFlip(start, jc)) { rejects.Add($"{label}: 손목 가지 전환(J5 {start[4]:0}° → {jc[4]:0}°)"); continue; }
            if (SelfClearance(m, f.BaseZMm, jc) is { ClearanceMm: < 0 } sc) { rejects.Add($"{label}: 자기 간섭 ({sc.Part}–{sc.Obstacle})"); continue; }
            if (!planar && JointPathBlocker(m, planes, f, start, jc) is { } jp) { rejects.Add($"{label}: {jp}"); continue; }

            var path = new List<double[]>();
            if (!direct) path.AddRange(Sample(cand, plunge, m.PlanarSampleMm));
            if (m.PlanarRetreatMm > 0.5) path.AddRange(Sample(plunge, approach, m.PlanarSampleMm));
            var seed = jc;
            string? why = null;
            foreach (var pt in path)
            {
                if (NearestInRange(m, SolveIk(m, f, pt, normal), seed) is not { } j) { why = $"[{Fmt(pt)}] 도달 불가"; break; }
                var jump = Travel(seed, j);
                if (jump > m.MaxPathJointStepDeg) { why = $"[{Fmt(pt)}] 관절이 {jump:0}° 튐"; break; }
                if (ElbowFlip(jc, j)) { why = $"[{Fmt(pt)}] 팔꿈치 가지 전환"; break; }
                if (Math.Sign(j[4]) != Math.Sign(jc[4]) || Math.Abs(j[4]) < m.PlanarWristMarginDeg) { why = $"[{Fmt(pt)}] 손목 특이점(J5 {j[4]:0.0}°)"; break; }
                if (SelfClearance(m, f.BaseZMm, j) is { ClearanceMm: < 0 } s) { why = $"[{Fmt(pt)}] 자기 간섭 ({s.Part}–{s.Obstacle})"; break; }
                seed = j;
            }
            if (why is not null) { rejects.Add($"{label}: 경로 {why}"); continue; }
            return new ReachEntry(true, cur, cand, plunge, inPlaneMm, candidates.Count, rejects);
        }
        return new ReachEntry(false, cur, null, plunge, inPlaneMm, candidates.Count, rejects);
    }

    /// <summary>
    /// 평면 경유 진입(자세 고정) — HD_AMR <c>PlanarApproachAsync</c> 와 같은 순서. 진입점·경로의 자세를 ② 접근 해의 플랜지 자세로
    /// 고정해 6축 전부를 푼다. 진입점마다 해를 J6 감김 유지 순으로 보며 첫 통과 해를 쓴다(<c>EntryJointBlocker</c>: 관절 이동[6축] ≤ 한계,
    /// 팔꿈치·손목 반전 없음, |J5| 여유, J6 안전 범위, 자기 간섭). 그 뒤 경로는 100 mm 간격으로 이어 풀어 관절 한계 근접·관절 튐[6축]·
    /// 팔꿈치·손목·J6 안전 범위·자기 간섭을 본다.
    /// </summary>
    private static ReachEntry PlanEntryFixedPose(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f, double[] start,
        double[] approach, double[] normal, double[] approachJoints, bool planar)
    {
        var fr = m.Kinematics.Frames(start);
        var cur = f.BaseToDrawing(Vec3.Add(Mat4.Origin(fr[6]), Vec3.Scale(Mat4.AxisZ(fr[6]), m.ToolZMm)));
        var r = m.PlanarRetreatMm / 1000.0;
        var h = Vec3.Dot(Vec3.Sub(cur, approach), normal);
        var projected = Vec3.Sub(cur, Vec3.Scale(normal, h - r));
        var plunge = Vec3.Add(approach, Vec3.Scale(normal, r));
        var inPlaneMm = Vec3.Length(Vec3.Sub(plunge, projected)) * 1000;

        var candidates = EntryCandidates(m, projected, plunge, inPlaneMm, planar);

        // ② 접근 해의 플랜지 자세(BASE) — 진입점·경로 전 구간에서 이 자세를 유지한다.
        var pose = m.Kinematics.Frames(approachJoints)[6];
        var axisZ = Mat4.AxisZ(pose);
        double[,] Target(double[] tcpDrawing)
        {
            var o = Vec3.Sub(f.DrawingToBase(tcpDrawing), Vec3.Scale(axisZ, m.ToolZMm));
            var t = (double[,])pose.Clone();
            t[0, 3] = o[0]; t[1, 3] = o[1]; t[2, 3] = o[2];
            return t;
        }
        List<double[]> Ik(double[] tcpDrawing, double[] reference) =>
            WoundSolutions(m, m.Kinematics.InverseFlange(Target(tcpDrawing)), reference);
        static double Travel6(double[] a, double[] b) => Enumerable.Range(0, 6).Max(i => Math.Abs(a[i] - b[i]));
        static double Sum6(double[] a, double[] b) => Enumerable.Range(0, 6).Sum(i => Math.Abs(a[i] - b[i]));
        var shift = J6WindingShift(m, start[5]);
        string? J6Out(double j6) => j6 + shift >= m.SafeJ6MinDeg && j6 + shift <= m.SafeJ6MaxDeg
            ? null
            : $"J6 안전 범위({m.SafeJ6MinDeg:0}°~{m.SafeJ6MaxDeg:0}°) 밖 — J6 {start[5]:0.0}° → {j6:0.0}°";
        string? EntryBlocker(double[] j)
        {
            var travel = Travel6(start, j);
            if (travel > m.MaxEntryJointTravelDeg) return $"진입 관절 이동 {travel:0}° > {m.MaxEntryJointTravelDeg:0}°";
            if (ElbowFlip(start, j)) return "팔꿈치 가지 전환";
            if (WristFlip(start, j)) return $"손목 가지 전환(J5 {start[4]:0}° → {j[4]:0}°)";
            if (Math.Abs(j[4]) < m.PlanarWristMarginDeg) return $"손목 특이점(|J5| {Math.Abs(j[4]):0.0}° < {m.PlanarWristMarginDeg:0}°)";
            if (J6Out(j[5]) is { } j6) return j6;
            if (SelfBlocker(j) is { } self) return self;
            return null;
        }
        // 자기 간섭은 여유까지 본다 — 여유가 몇 mm 뿐인 진입은 모델·정차 오차로 실기에서 간섭으로 거부된다(EntrySelfMarginMm).
        string? SelfBlocker(double[] j)
        {
            var sc = SelfClearance(m, f.BaseZMm, j);
            if (sc.ClearanceMm < 0) return $"자기 간섭 ({sc.Part}–{sc.Obstacle})";
            return sc.ClearanceMm < m.EntrySelfMarginMm
                ? $"자기 간섭 여유 부족 ({sc.Part}–{sc.Obstacle} {sc.ClearanceMm:0}mm < {m.EntrySelfMarginMm:0}mm)"
                : null;
        }

        string Fmt(double[] p) => string.Join(", ", f.DrawingToBase(p).Select(v => v.ToString("0")));
        var rejects = new List<string>();
        foreach (var (cand, direct) in candidates)
        {
            var label = direct ? "들어갈 점 직접" : $"후퇴 평면 [{Fmt(cand)}]";
            // J6 감김 유지 순(HD_AMR KeepWristCost: J6 변화 × 8 + 관절 변위 합 + 팔꿈치 반전 벌점)
            var sols = Ik(cand, start)
                .OrderBy(j => 8.0 * Math.Abs(j[5] - start[5]) + Sum6(j, start) + (ElbowFlip(start, j) ? 10000 : 0)).ToList();
            if (sols.Count == 0) { rejects.Add($"{label}: 도달 불가"); continue; }
            double[]? jc = null;
            string? firstWhy = null;
            foreach (var sol in sols)
            {
                // 직접 진입(평면 경유를 쓰지 않는 면)은 진입 MoveJ 가 길 수 있다 — 그 관절 이동 경로의 면·자기 간섭을 본다.
                var whySol = EntryBlocker(sol) ?? (planar ? null : JointPathBlocker(m, planes, f, start, sol));
                if (whySol is null) { jc = sol; break; }
                firstWhy ??= whySol;
            }
            if (jc is null) { rejects.Add($"{label}: {firstWhy}{(sols.Count > 1 ? $" (해 {sols.Count}개 모두)" : "")}"); continue; }

            var path = new List<double[]>();
            if (!direct) path.AddRange(Sample(cand, plunge, m.PlanarSampleMm));
            if (m.PlanarRetreatMm > 0.5) path.AddRange(Sample(plunge, approach, m.PlanarSampleMm));
            var seed = jc;
            string? why = null;
            foreach (var pt in path)
            {
                var near = Ik(pt, seed).MinBy(j => Sum6(j, seed));
                if (near is null) { why = $"[{Fmt(pt)}] 도달 불가"; break; }
                var limit = Enumerable.Range(0, Math.Min(5, m.JointMinDeg.Length))
                    .Where(i => near[i] < m.JointMinDeg[i] + m.PathJointLimitMarginDeg || near[i] > m.JointMaxDeg[i] - m.PathJointLimitMarginDeg)
                    .Select(i => (int?)i).FirstOrDefault();
                if (limit is { } li) { why = $"[{Fmt(pt)}] J{li + 1} 관절 한계 근접({near[li]:0.0}°)"; break; }
                var jump = Travel6(seed, near);
                if (jump > m.MaxPathJointStepDeg) { why = $"[{Fmt(pt)}] 관절이 {jump:0}° 튐"; break; }
                if (ElbowFlip(jc, near)) { why = $"[{Fmt(pt)}] 팔꿈치 가지 전환"; break; }
                if (Math.Sign(near[4]) != Math.Sign(jc[4]) || Math.Abs(near[4]) < m.PlanarWristMarginDeg) { why = $"[{Fmt(pt)}] 손목 특이점(J5 {near[4]:0.0}°)"; break; }
                if (J6Out(near[5]) is { } j6p) { why = $"[{Fmt(pt)}] {j6p}"; break; }
                if (SelfBlocker(near) is { } sp) { why = $"[{Fmt(pt)}] {sp}"; break; }
                seed = near;
            }
            if (why is not null) { rejects.Add($"{label}: 경로 {why}"); continue; }
            return new ReachEntry(true, cur, cand, plunge, inPlaneMm, candidates.Count, rejects);
        }
        return new ReachEntry(false, cur, null, plunge, inPlaneMm, candidates.Count, rejects);
    }

    /// <summary>
    /// IK 해를 기준 자세 쪽으로 감아 푼다 — J1~J5 는 ±360 중 관절 범위 안에서 기준에 가장 가까운 값(범위 안 값이 없으면 그 해는 버림),
    /// J6 은 기준에 가장 가까운 감김(HD_AMR SolveIkCandidates 의 감아 풀기).
    /// </summary>
    private static List<double[]> WoundSolutions(ReachRobotModel m, IReadOnlyList<double[]> sols, double[] reference)
    {
        var list = new List<double[]>(sols.Count);
        foreach (var raw in sols)
        {
            var j = new double[raw.Length];
            var ok = true;
            for (var i = 0; i < raw.Length && ok; i++)
            {
                if (i >= m.JointMinDeg.Length)
                {
                    j[i] = raw[i] + 360.0 * Math.Round((reference[i] - raw[i]) / 360.0);
                    continue;
                }
                double? pick = null;
                foreach (var c in new[] { raw[i], raw[i] - 360, raw[i] + 360 })
                    if (c >= m.JointMinDeg[i] && c <= m.JointMaxDeg[i] && (pick is null || Math.Abs(c - reference[i]) < Math.Abs(pick.Value - reference[i])))
                        pick = c;
                if (pick is null) ok = false;
                else j[i] = pick.Value;
            }
            if (ok) list.Add(j);
        }
        return list;
    }

    /// <summary>from→to 직선을 stepMm 이하 간격으로 나눈 점(시작 제외, 끝 포함). 좌표 m.</summary>
    private static List<double[]> Sample(double[] from, double[] to, double stepMm)
    {
        var len = Vec3.Length(Vec3.Sub(to, from)) * 1000;
        var n = Math.Max(1, (int)Math.Ceiling(len / stepMm));
        return Enumerable.Range(1, n).Select(k => Vec3.Add(from, Vec3.Scale(Vec3.Sub(to, from), (double)k / n))).ToList();
    }

    /// <summary>자기 간섭 여유 — 팔 부품·검사 툴 몸체와 차체·신호탑·리프트 기둥 사이 최소 [mm] (HD_AMR CobotEnvelope 와 같은 규칙).</summary>
    public readonly record struct SelfClearanceResult(double ClearanceMm, string Part, string Obstacle);

    /// <summary>
    /// 관절각 한 벌의 자기 간섭 여유 — HD_AMR <c>CobotEnvelope.Evaluate</c> 와 같다: BASE·J1 은 제외, 어깨는 기둥과 재지 않고,
    /// 툴은 몸체 원통(<see cref="ReachRobotModel.SelfToolSegment"/>)만 본다. 장착 yaw 0(STEP).
    /// </summary>
    public static SelfClearanceResult SelfClearance(ReachRobotModel m, double baseZMm, double[] j)
    {
        var best = new SelfClearanceResult(double.PositiveInfinity, "", "");
        var boxes = SelfBoxes(m, baseZMm);
        double mc = Math.Cos(m.MountYawDeg * Math.PI / 180), ms = Math.Sin(m.MountYawDeg * Math.PI / 180);
        double[] Amr(double[] p) => new[] { mc * p[0] - ms * p[1], ms * p[0] + mc * p[1], p[2] + baseZMm };
        foreach (var s in ArmSegments(m, m.Kinematics.Frames(j)).Where(s => s.Name != "BASE·J1"))
        {
            var pa = Amr(s.A);
            var pb = Amr(s.B);
            foreach (var box in s.NearColumn ? boxes[..2] : boxes)
            {
                var cl = SegmentBoxDistanceMm(pa, pb, box.Min, box.Max) - s.RadiusMm;
                if (cl < best.ClearanceMm) best = new SelfClearanceResult(cl, s.Name, box.Name);
            }
        }
        return best;
    }

    /// <summary>팔 부품 캡슐(BASE 기준 mm) — 마지막은 자기 간섭용 툴 몸체 원통.</summary>
    private readonly record struct ArmSegment(string Name, double[] A, double[] B, double RadiusMm, bool NearColumn);

    private static ArmSegment[] ArmSegments(ReachRobotModel m, IReadOnlyList<double[,]> fr)
    {
        double[] O(int i) => Mat4.Origin(fr[i]);
        double[] Along(double[] p, double[] axis, double mm) => Vec3.Add(p, Vec3.Scale(axis, mm));
        var z1 = Mat4.AxisZ(fr[1]);
        var z6 = Mat4.AxisZ(fr[6]);
        var s1 = Along(O(1), z1, m.ShoulderOffsetMm);
        var e1 = Along(O(2), z1, m.ShoulderOffsetMm);
        var e2 = Along(O(2), z1, m.ForearmOffsetMm);
        var w1 = Along(O(3), z1, m.ForearmOffsetMm);
        var core = m.SelfToolSegment;
        return new[]
        {
            new ArmSegment("BASE·J1", O(0), O(1), m.BaseRadiusMm, true),
            new ArmSegment("어깨(J2)", O(1), s1, m.ShoulderRadiusMm, true),
            new ArmSegment("상완", s1, e1, m.UpperArmRadiusMm, false),
            new ArmSegment("팔꿈치(J3)", e1, e2, m.ElbowRadiusMm, false),
            new ArmSegment("전완", e2, w1, m.ForearmRadiusMm, false),
            new ArmSegment("손목1(J4)", w1, O(4), m.WristRadiusMm, false),
            new ArmSegment("손목2(J5)", O(4), O(5), m.WristRadiusMm, false),
            new ArmSegment("손목3(J6)", O(5), O(6), m.WristRadiusMm, false),
            new ArmSegment(core.Name, Along(O(6), z6, core.FromMm), Along(O(6), z6, core.ToMm), core.RadiusMm, false),
        };
    }

    // ── 내부 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 차체 기준(mm, x 전방·y 좌측·z 바닥 위) ↔ 도면(m). BASE 는 차체 중심 위 마운트 + 스트로크이고,
    /// BASE +X 는 차체 +X 에서 <see cref="ReachRobotModel.MountYawDeg"/> 만큼 반시계로 돌아 있다(HD_AMR T_A_B rz).
    /// </summary>
    private sealed class BaseFrame
    {
        private readonly double _cos, _sin, _ox, _oy, _floor, _mc, _ms;
        public double BaseZMm { get; }

        public BaseFrame(ReachRobotModel m, ReachStation st, double strokeMm, double dF, double dL)
        {
            _cos = Math.Cos(st.Yaw); _sin = Math.Sin(st.Yaw);
            _ox = st.X + (_cos * dF - _sin * dL) / 1000.0;
            _oy = st.Y + (_sin * dF + _cos * dL) / 1000.0;
            _floor = st.FloorZ;
            _mc = Math.Cos(m.MountYawDeg * Math.PI / 180); _ms = Math.Sin(m.MountYawDeg * Math.PI / 180);
            BaseZMm = m.MountZMm + strokeMm;
        }

        public double[] AmrToDrawing(double x, double y, double z) =>
            new[] { _ox + (_cos * x - _sin * y) / 1000.0, _oy + (_sin * x + _cos * y) / 1000.0, _floor + z / 1000.0 };

        /// <summary>BASE 기준 점(mm) → 차체 기준(mm).</summary>
        public double[] BaseToAmr(double[] p) => new[] { _mc * p[0] - _ms * p[1], _ms * p[0] + _mc * p[1], p[2] + BaseZMm };

        public double[] BaseToDrawing(double[] p)
        {
            var a = BaseToAmr(p);
            return AmrToDrawing(a[0], a[1], a[2]);
        }

        public double[] DrawingToBase(double[] p)
        {
            double dx = (p[0] - _ox) * 1000, dy = (p[1] - _oy) * 1000;
            double ax = _cos * dx + _sin * dy, ay = -_sin * dx + _cos * dy;
            return new[] { _mc * ax + _ms * ay, -_ms * ax + _mc * ay, (p[2] - _floor) * 1000 - BaseZMm };
        }

        public double[] DirectionToBase(double[] v)
        {
            double ax = _cos * v[0] + _sin * v[1], ay = -_sin * v[0] + _cos * v[1];
            return new[] { _mc * ax + _ms * ay, -_ms * ax + _mc * ay, v[2] };
        }
    }

    private static double[,] FlangeTarget(ReachRobotModel m, BaseFrame f, double[] tcpDrawing, double[] wallNormal)
    {
        var z = Vec3.Scale(f.DirectionToBase(wallNormal), -1);   // 광축 = 면을 바라봄
        var x = Vec3.Normalize(Vec3.Cross(new[] { 0.0, 0, 1 }, z)) ?? new[] { 1.0, 0, 0 };
        var y = Vec3.Cross(z, x);
        var tcp = f.DrawingToBase(tcpDrawing);
        return Mat4.FromAxes(x, y, z, Vec3.Sub(tcp, Vec3.Scale(z, m.ToolZMm)));
    }

    /// <summary>범위 밖 관절은 ±360 감아 범위 안이면 그 값(같은 자세) — J2 +120 = −240.</summary>
    private static double[] Normalize(ReachRobotModel m, double[] j) => j.Select((v, i) =>
        i < m.JointMinDeg.Length && (v < m.JointMinDeg[i] || v > m.JointMaxDeg[i])
            ? new[] { v - 360, v + 360 }.FirstOrDefault(c => c >= m.JointMinDeg[i] && c <= m.JointMaxDeg[i], v)
            : v).ToArray();

    private readonly record struct PostureEval(bool Ok, double Margin, string Limiting);

    /// <summary>HD_AMR PostureLimits.Evaluate 와 같은 여유(J6 제외, 어깨 특이점 생략).</summary>
    private static PostureEval Posture(ReachRobotModel m, double[] j)
    {
        var slack = double.PositiveInfinity;
        var why = "여유 있음";
        for (var i = 0; i < 5; i++)
        {
            var s = Math.Min(j[i] - m.JointMinDeg[i], m.JointMaxDeg[i] - j[i]);
            if (s < slack) { slack = s; why = $"J{i + 1} 관절한계"; }
        }
        var wrist = Math.Abs(j[4]) - m.WristMarginDeg;
        if (wrist < slack) { slack = wrist; why = "손목 특이점(|J5| 작음)"; }
        var elbow = Math.Abs(j[2]) - m.ElbowLimitDeg;
        if (elbow < slack) { slack = elbow; why = m.MinElbowBendDeg > m.ElbowMarginDeg ? $"팔 뻗음 과다(|J3| {Math.Abs(j[2]):0}° < {m.ElbowLimitDeg:0}°)" : "팔꿈치 특이점(|J3| 작음)"; }
        return new PostureEval(slack > 0, slack, slack > 0 ? "여유 있음" : why);
    }

    private static int BestMargin(List<(double[] J, PostureEval P)> sols)
    {
        var best = -1;
        for (var i = 0; i < sols.Count; i++)
            if (best < 0 || sols[i].P.Margin > sols[best].P.Margin) best = i;
        return best;
    }

    /// <summary>자세 한계 안 해 중 간섭 여유 최대(없으면 자세 여유 최대).</summary>
    private static int BestClearance(List<ReachSolution> sols)
    {
        var best = -1;
        for (var i = 0; i < sols.Count; i++)
            if (sols[i].PostureOk && (best < 0 || sols[i].MinClearanceM > sols[best].MinClearanceM)) best = i;
        if (best >= 0) return best;
        for (var i = 0; i < sols.Count; i++)
            if (best < 0 || sols[i].MarginDeg > sols[best].MarginDeg) best = i;
        return best;
    }

    private static int? Nearest(List<(double[] J, PostureEval P)> sols, double[] seed)
    {
        int? best = null;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < sols.Count; i++)
        {
            if (!sols[i].P.Ok) continue;
            var d = Enumerable.Range(0, 5).Max(k => Math.Abs(sols[i].J[k] - seed[k]));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static ReachSolution Solve(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f, double[] j, PostureEval posture)
    {
        var fr = m.Kinematics.Frames(j);
        double[] O(int i) => Mat4.Origin(fr[i]);
        double[] Along(double[] p, double[] axis, double mm) => Vec3.Add(p, Vec3.Scale(axis, mm));
        var z1 = Mat4.AxisZ(fr[1]);
        var z6 = Mat4.AxisZ(fr[6]);
        double[] B(double[] p) => f.BaseToDrawing(p);

        var self = SelfBoxes(m, f.BaseZMm);
        var segs = ArmSegments(m, fr);
        var core = segs[^1];
        var parts = new[] { Column(m, planes, f) }
            .Concat(segs[..^1].Select(s =>
                WithSelf(Part(s.Name, B(s.A), B(s.B), s.RadiusMm, planes), s.A, s.B, s.RadiusMm, f, s.NearColumn ? self[..2] : self)))
            .Concat(m.ToolBoxes.Select(t =>
                WithSelf(ToolBoxPart(m, t, fr[6], f, planes), core.A, core.B, core.RadiusMm, f, self)))
            .ToArray();
        var joints = new[]
        {
            new ReachJointMark("J1", B(O(0))),
            new ReachJointMark("J2", B(Along(O(1), z1, 100))),
            new ReachJointMark("J3", B(Along(O(2), z1, 115))),
            new ReachJointMark("J4", B(Along(O(3), z1, 100))),
            new ReachJointMark("J5", B(O(4))),
            new ReachJointMark("J6", B(O(5))),
            new ReachJointMark("플랜지", B(O(6))),
        };
        // J5 몸체(J4→J5)가 아래를 향하나 — 실기에서 천장 간섭을 피하려고 J4 를 아래로 돌린 자세 구분용.
        var wristDown = O(5)[2] < O(4)[2];
        return new ReachSolution(j, posture.Ok, posture.Margin, posture.Limiting, parts, joints,
            B(Along(O(6), z6, m.ToolZMm)), wristDown, parts.Min(p => p.ClearanceM));
    }

    /// <summary>차체 기준 상자(mm, x 전방·y 좌측·z 바닥 위).</summary>
    private readonly record struct SelfBox(string Name, double[] Min, double[] Max);

    /// <summary>자기 간섭 상자 — 신호탑·차체·리프트 기둥(차체 윗면 → BASE). 앞 둘은 BASE·어깨도 잰다(기둥에 붙은 부품).</summary>
    private static SelfBox[] SelfBoxes(ReachRobotModel m, double baseZMm) => new[]
    {
        new SelfBox("AMR 차체", new[] { -m.AmrRearMm, -m.AmrHalfWidthMm, 0 }, new[] { m.AmrFrontMm, m.AmrHalfWidthMm, m.AmrHeightMm }),
        new SelfBox("신호탑", new[] { -m.TowerRearToMm, -m.TowerHalfWidthMm, m.AmrHeightMm },
            new[] { -m.TowerRearFromMm, m.TowerHalfWidthMm, m.TowerTopMm }),
        new SelfBox("리프트 기둥", new[] { -m.ColumnHalfMm, -m.ColumnHalfMm, m.AmrHeightMm },
            new[] { m.ColumnHalfMm, m.ColumnHalfMm, baseZMm }),
    };

    /// <summary>리프트 기둥 — 사각 기둥 꼭짓점으로 탱크 면 여유를 잰다(그림은 반폭 반경 캡슐).</summary>
    private static ReachPart Column(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f)
    {
        var h = m.ColumnHalfMm;
        var corners = new[] { m.AmrHeightMm, f.BaseZMm }
            .SelectMany(z => new[] { (h, h), (h, -h), (-h, h), (-h, -h) }.Select(c => f.AmrToDrawing(c.Item1, c.Item2, z)))
            .ToList();
        var (cl, code) = Clearance(corners, 0, planes, skipFloor: true);
        return new ReachPart("리프트 기둥", f.AmrToDrawing(0, 0, m.AmrHeightMm), f.AmrToDrawing(0, 0, f.BaseZMm), h / 1000.0, cl, code);
    }

    /// <summary>팔 부품의 탱크 면 여유와 차체·신호탑·기둥 여유 중 작은 쪽. a·b 는 BASE 기준 mm.</summary>
    private static ReachPart WithSelf(ReachPart tank, double[] a, double[] b, double radiusMm, BaseFrame f, SelfBox[] boxes)
    {
        var pa = f.BaseToAmr(a);
        var pb = f.BaseToAmr(b);
        var best = tank;
        foreach (var box in boxes)
        {
            var cl = (SegmentBoxDistanceMm(pa, pb, box.Min, box.Max) - radiusMm) / 1000.0;
            if (cl < best.ClearanceM) best = best with { ClearanceM = cl, NearestPlane = box.Name };
        }
        return best;
    }

    /// <summary>선분–축정렬 상자 최단거리 [mm]. 선분 위 점에서 상자까지 거리는 볼록이라 삼분 탐색으로 찾는다.</summary>
    public static double SegmentBoxDistanceMm(double[] a, double[] b, double[] min, double[] max)
    {
        double At(double t)
        {
            var s = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var p = a[i] + (b[i] - a[i]) * t;
                var d = p < min[i] ? min[i] - p : p > max[i] ? p - max[i] : 0;
                s += d * d;
            }
            return Math.Sqrt(s);
        }
        double lo = 0, hi = 1;
        for (var k = 0; k < 40 && hi - lo > 1e-4; k++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (At(m1) <= At(m2)) hi = m2; else lo = m1;
        }
        return Math.Min(At((lo + hi) / 2), Math.Min(At(0), At(1)));
    }

    /// <summary>
    /// 검사 툴 상자의 면 여유 — 플랜지 기준 상자를 <see cref="ReachRobotModel.ToolMountRollDeg"/> 만큼 광축 둘레로 돌려
    /// 플랜지 프레임(<paramref name="flange"/>, BASE 기준)에 놓는다. 상자는 볼록이라 평면까지 최소 거리 = 꼭짓점 최소.
    /// </summary>
    private static ReachPart ToolBoxPart(ReachRobotModel m, ReachToolBox box, double[,] flange, BaseFrame f, IReadOnlyList<ReachPlane> planes)
    {
        double rc = Math.Cos(m.ToolMountRollDeg * Math.PI / 180), rs = Math.Sin(m.ToolMountRollDeg * Math.PI / 180);
        double[] Place(double x, double y, double z)
        {
            double fx = rc * x - rs * y, fy = rs * x + rc * y;
            return f.BaseToDrawing(new[]
            {
                flange[0, 0] * fx + flange[0, 1] * fy + flange[0, 2] * z + flange[0, 3],
                flange[1, 0] * fx + flange[1, 1] * fy + flange[1, 2] * z + flange[1, 3],
                flange[2, 0] * fx + flange[2, 1] * fy + flange[2, 2] * z + flange[2, 3],
            });
        }
        double[] lo = box.Min, hi = box.Max;
        var corners = new List<double[]>(8);
        foreach (var z in new[] { lo[2], hi[2] })
            foreach (var (x, y) in new[] { (lo[0], lo[1]), (hi[0], lo[1]), (hi[0], hi[1]), (lo[0], hi[1]) })
                corners.Add(Place(x, y, z));
        var (cl, code) = Clearance(corners, 0, planes, skipFloor: false);
        double cx = (lo[0] + hi[0]) / 2, cy = (lo[1] + hi[1]) / 2;
        return new ReachPart(box.Name, Place(cx, cy, lo[2]), Place(cx, cy, hi[2]), 0, cl, code, corners.ToArray());
    }

    private static ReachPart Part(string name, double[] a, double[] b, double radiusMm, IReadOnlyList<ReachPlane> planes, bool skipFloor = false)
    {
        var (cl, code) = Clearance(new[] { a, b }, radiusMm / 1000.0, planes, skipFloor);
        return new ReachPart(name, a, b, radiusMm / 1000.0, cl, code);
    }

    /// <summary>점 집합(선분 끝점·상자 꼭짓점)의 평면 여유 최소 — 평면까지 거리는 선형이라 끝점만 보면 된다.</summary>
    private static (double Clearance, string Code) Clearance(IEnumerable<double[]> points, double radiusM,
        IReadOnlyList<ReachPlane> planes, bool skipFloor)
    {
        var best = double.PositiveInfinity;
        var code = "";
        foreach (var pl in planes)
        {
            if (skipFloor && pl.IsFloor) continue;
            foreach (var p in points)
            {
                var d = pl.SignedDistance(p) - radiusM;
                if (d < best) { best = d; code = pl.Code; }
            }
        }
        return (best, code);
    }

    private static ReachBody Body(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f)
    {
        var corners = new List<double[]>(8);
        foreach (var z in new[] { 0.0, m.AmrHeightMm })
            foreach (var (x, y) in new[] { (m.AmrFrontMm, -m.AmrHalfWidthMm), (m.AmrFrontMm, m.AmrHalfWidthMm),
                                            (-m.AmrRearMm, m.AmrHalfWidthMm), (-m.AmrRearMm, -m.AmrHalfWidthMm) })
                corners.Add(f.AmrToDrawing(x, y, z));
        var (cl, code) = Clearance(corners, 0, planes, skipFloor: true);
        return new ReachBody(corners.ToArray(), cl, code);
    }
}
