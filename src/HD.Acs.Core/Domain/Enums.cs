namespace HD.Acs.Core.Domain;

/// <summary>미션 상태 [ADR-010] — 상태의 진실은 HD_AMR의 VDA 5050 state 보고 (robot-is-truth)</summary>
public enum MissionState { Created, Released, Running, Disconnected, Paused, Completed, Aborted }

/// <summary>시나리오 실행(층 미션 시퀀스) 상태 — 층 전환은 작업자 수동 절차 [Q9]</summary>
public enum RunState { Running, WaitingFloorTransfer, Completed, Aborted }

public enum MissionTrigger
{
    Release, RobotProgress, RobotCompleted,
    ConnectionLost, ConnectionRestored,
    PauseRequested, Resumed, AbortRequested
}

public enum NodeType { Waypoint, InspectionStop, Elevator, Charging, Parking, Station }
public enum EdgeType { Travel, ManualTransfer }
public enum VdaActionStatus { Waiting, Initializing, Running, Finished, Failed }
