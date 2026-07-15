namespace HD.Acs.Core.Abstractions;

/// <summary>Order 생성 입력 — 시나리오에서 층 단위로 분해된 계획 (한 미션 = 한 층)</summary>
public sealed record PlannedPoint(string NodeId, IReadOnlyList<PlannedAction> Actions);

public sealed record PlannedAction(
    Guid ActionId,                       // ACS가 발급·보존 — state.actionStates 대조 키
    string ActionType,                   // ref.action_catalog 참조 [Q1]
    string BlockingType,                 // NONE | SOFT | HARD
    IReadOnlyDictionary<string, object?> Parameters);
