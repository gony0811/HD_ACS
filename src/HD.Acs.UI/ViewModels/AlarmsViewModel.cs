using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 활성 알람 목록. 백엔드에 알람 REST/AlarmRaised 발화가 아직 없으므로(주석에만 정의) 현재는 빈 상태 안내를 표시하고,
/// AlarmRaised 푸시를 미리 구독해 백엔드 확장 시 즉시 반영되도록 한다.
/// </summary>
public sealed partial class AlarmsViewModel : ObservableObject
{
    public ObservableCollection<AlarmDto> Alarms { get; } = new();

    [ObservableProperty] private string _emptyMessage =
        "활성 알람 없음. (백엔드 알람 API/푸시 도입 전 — 도입 시 자동 연결)";

    public bool HasAlarms => Alarms.Count > 0;

    public AlarmsViewModel(IMonitoringClient monitoring)
    {
        monitoring.AlarmRaised += OnAlarmRaised;
        Alarms.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAlarms));
    }

    private void OnAlarmRaised(object? sender, AlarmDto alarm)
    {
        // 동일 알람(AlarmId) 갱신 또는 신규 추가
        for (int i = 0; i < Alarms.Count; i++)
        {
            if (Alarms[i].AlarmId != alarm.AlarmId) continue;
            Alarms[i] = alarm;
            return;
        }
        Alarms.Insert(0, alarm);
    }
}
