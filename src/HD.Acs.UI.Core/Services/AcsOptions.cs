namespace HD.Acs.UI.Services;

/// <summary>appsettings.json "Acs" 섹션 바인딩 — 관제 서버 접속 정보.</summary>
public sealed class AcsOptions
{
    public const string SectionName = "Acs";

    /// <summary>HD.Acs.App REST + SignalR 호스트 기본 주소.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5199";

    /// <summary>인증 미들웨어 도입 전까지 명령(존 변경/비상정지)에 사용할 운영자 ID.</summary>
    public string OperatorId { get; set; } = "operator";

    /// <summary>맨 아래층의 화면 표기 번호 — 0(기본) = L0·L1·L2·L3, 1 = L1·L2·L3·L4. 화면 표기만 바꾼다(맵 ID·지령은 그대로).</summary>
    public int LevelLabelBase { get; set; } = 0;
}
