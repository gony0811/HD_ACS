using Stateless;
using Stateless.Graph;

namespace HD.Acs.Core.Domain;

/// <summary>
/// Stateless 기반 미션 상태머신 [ADR-010].
/// 전이 이벤트는 OnTransition으로 발행되어 hist.transition_log에 기록된다.
/// </summary>
public sealed class MissionStateMachine
{
    private readonly StateMachine<MissionState, MissionTrigger> _machine;
    private MissionState _state;

    public event Action<MissionState, MissionState, MissionTrigger>? OnTransition;

    public MissionState State => _state;

    public MissionStateMachine(MissionState initial = MissionState.Created)
    {
        _state = initial;
        _machine = new StateMachine<MissionState, MissionTrigger>(() => _state, s => _state = s);

        _machine.Configure(MissionState.Created)
            .Permit(MissionTrigger.Release, MissionState.Released)
            .Permit(MissionTrigger.AbortRequested, MissionState.Aborted);

        _machine.Configure(MissionState.Released)
            .Permit(MissionTrigger.RobotProgress, MissionState.Running)
            .Permit(MissionTrigger.ConnectionLost, MissionState.Disconnected)
            .Permit(MissionTrigger.AbortRequested, MissionState.Aborted);

        _machine.Configure(MissionState.Running)
            .InternalTransition(MissionTrigger.RobotProgress, _ => { }) // 주기 state 보고: 상태 유지
            .Permit(MissionTrigger.RobotCompleted, MissionState.Completed)
            .Permit(MissionTrigger.ConnectionLost, MissionState.Disconnected)
            .Permit(MissionTrigger.PauseRequested, MissionState.Paused)
            .Permit(MissionTrigger.AbortRequested, MissionState.Aborted);

        _machine.Configure(MissionState.Disconnected)  // 로봇은 온보드 실행 지속 [ADR-002]
            .Permit(MissionTrigger.ConnectionRestored, MissionState.Running)
            .Permit(MissionTrigger.RobotCompleted, MissionState.Completed)
            .Permit(MissionTrigger.AbortRequested, MissionState.Aborted);

        _machine.Configure(MissionState.Paused)
            .Permit(MissionTrigger.Resumed, MissionState.Running)
            .Permit(MissionTrigger.AbortRequested, MissionState.Aborted);

        _machine.OnTransitionCompleted(t =>
        {
            if (!Equals(t.Source, t.Destination))
                OnTransition?.Invoke(t.Source, t.Destination, t.Trigger);
        });
    }

    public bool CanFire(MissionTrigger trigger) => _machine.CanFire(trigger);
    public void Fire(MissionTrigger trigger) => _machine.Fire(trigger);

    /// <summary>상태도 DOT export — 문서/UI에 자동 상태도 제공 [ADR-010]</summary>
    public string ToDotGraph() => UmlDotGraph.Format(_machine.GetInfo());
}
