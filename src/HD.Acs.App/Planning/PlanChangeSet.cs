namespace HD.Acs.App.Planning;

/// <summary>
/// 계획 변경 연산 1건 [계획 어시스턴트 — ADR-013]. LLM(또는 사람이 쓴 JSON)이 만드는 **고수준 연산**이다.
/// 필드는 평탄(flat) — 연산마다 쓰는 필드만 채운다(LLM structured output 스키마를 단순하게 유지하기 위함).
/// 좌표 규약: v 는 **층-로컬**(그 면에서 해당 층 도달 구간의 아래 끝 = 0) — 계획 화면 입력 폼과 같다. u 는 면 그대로.
/// 서버(<see cref="PlanChangeSetEngine"/>)가 원자 연산으로 전개·검증하고, 운영자 승인 후에만 DB에 반영한다.
/// </summary>
public sealed record PlanOp
{
    /// <summary>
    /// createArea · createTask · updateTask · deleteTask (직접 연산)
    /// copyArea · gridAreas · renameAreas · moveAreas · deleteAreas · setSeamType · shiftTasks · deleteTasks · setScenarioAreas (매크로)
    /// query (읽기 전용 — 변경 없음)
    /// </summary>
    public string Op { get; init; } = "";

    // ── 대상 선택(필터) ─────────────────────────────
    public string? WallCode { get; init; }
    public int? Level { get; init; }
    /// <summary>영역 이름 정확히 일치.</summary>
    public string? AreaName { get; init; }
    /// <summary>영역 이름 glob(* ?) — 예 "PM-L2-*".</summary>
    public string? NamePattern { get; init; }
    /// <summary>작업 대상 seamType 필터(작업 매크로용).</summary>
    public string? MatchSeamType { get; init; }
    /// <summary>작업 대상 seq 목록(작업 매크로·직접 연산용). 비우면 전부.</summary>
    public int[]? Seqs { get; init; }

    // ── 영역 ────────────────────────────────────────
    public string? Name { get; init; }
    /// <summary>영역 코너 [[u,v]…] 3~4점(층-로컬 v).</summary>
    public double[][]? Corners { get; init; }
    public double? StandoffM { get; init; }

    // ── 작업(용접선) ────────────────────────────────
    public int? Seq { get; init; }
    public string? TaskName { get; init; }
    public string? SeamType { get; init; }
    public double? StartU { get; init; }
    public double? StartV { get; init; }
    public double? EndU { get; init; }
    public double? EndV { get; init; }

    // ── gridAreas ───────────────────────────────────
    public double? CellU { get; init; }
    public double? CellV { get; init; }
    public double? Gap { get; init; }
    public double? UFrom { get; init; }
    public double? UTo { get; init; }
    public double? VFrom { get; init; }
    public double? VTo { get; init; }
    public string? NamePrefix { get; init; }

    // ── renameAreas ─────────────────────────────────
    public string? Find { get; init; }
    public string? Replace { get; init; }
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }

    // ── copyArea ────────────────────────────────────
    /// <summary>복사 위치(전개도 화면 기준): left(u 감소)·right(u 증가)·above(v 증가)·below(v 감소). du/dv 를 주면 그 값 우선.</summary>
    public string? Placement { get; init; }
    /// <summary>원본 영역의 작업(용접선)도 같은 상대 위치로 복사(기본 true).</summary>
    public bool? CopyTasks { get; init; }

    // ── moveAreas / shiftTasks ──────────────────────
    public double? Du { get; init; }
    public double? Dv { get; init; }

    // ── setScenarioAreas ────────────────────────────
    public string? ScenarioName { get; init; }
    /// <summary>add(기본) · remove · replace.</summary>
    public string? Mode { get; init; }
    public bool? CreateIfMissing { get; init; }

    // ── query ───────────────────────────────────────
    /// <summary>count(기본) · list.</summary>
    public string? Aggregate { get; init; }
}

/// <summary>
/// 전개된 원자 연산 1건 — 좌표는 **면-전체 v**, 생성 대상 ID는 미리보기 시점에 확정(적용 때 같은 ID로 만든다 — 미리보기=적용).
/// </summary>
public sealed record AtomicOp
{
    public string Kind { get; init; } = "";        // createArea · updateArea · deleteArea · createTask · updateTask · deleteTask · createScenario · setScenarioAreas
    public int Source { get; init; }               // 원본 PlanOp 인덱스(0-based)
    public Guid? AreaId { get; init; }
    public Guid? TaskId { get; init; }
    public Guid? ScenarioId { get; init; }
    public string? WallCode { get; init; }
    public string? Name { get; init; }
    public double[][]? Corners { get; init; }
    public double? StandoffM { get; init; }
    /// <summary>updateArea: 영역과 함께 소속 작업도 (du,dv) 이동.</summary>
    public double? MoveTasksDu { get; init; }
    public double? MoveTasksDv { get; init; }
    public int? Seq { get; init; }
    public string? TaskName { get; init; }
    public string? SeamType { get; init; }
    public double? StartU { get; init; }
    public double? StartV { get; init; }
    public double? EndU { get; init; }
    public double? EndV { get; init; }
    public Guid[]? AreaIds { get; init; }
    /// <summary>createTask: 단면 DXF·프로파일 — null 이면 기본 임시값(copyArea 는 원본 값을 그대로 넘긴다).</summary>
    public string? SectionDxfId { get; init; }
    public string? ProfileId { get; init; }
}

/// <summary>원자 연산 검증 결과 + 미리보기용 표시 정보(면·층·영역명·면-전체 좌표).</summary>
public sealed record OpResult(
    AtomicOp Op, bool Ok, string? Error,
    string? WallCode, int? Level, string? AreaName,
    double[][]? Corners, double[]? Segment, string Summary);

public sealed record PlanPreview(
    IReadOnlyList<OpResult> Ops,
    IReadOnlyList<string> Messages,     // 매크로 전개 경고·조회 결과(사람이 읽는 문장)
    string Fingerprint,                 // 미리보기 시점 선창 계획 데이터 지문 — 적용 때 다르면 409
    bool AllOk)
{
    public int Creates => Ops.Count(o => o.Op.Kind.StartsWith("create"));
    public int Updates => Ops.Count(o => o.Op.Kind.StartsWith("update") || o.Op.Kind == "setScenarioAreas");
    public int Deletes => Ops.Count(o => o.Op.Kind.StartsWith("delete"));
    public int Failed => Ops.Count(o => !o.Ok);
}
