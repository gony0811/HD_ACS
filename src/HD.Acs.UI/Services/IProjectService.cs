using HD.Acs.UI.Models;

namespace HD.Acs.UI.Services;

/// <summary>
/// 프로젝트 파일(.hdacs) 입출력 — 전용 이진 컨테이너로 현재 선창의 지오메트리·영역·작업 스냅샷을
/// 저장/복원한다. DB가 런타임 truth이며 파일은 스냅샷(내보내기/가져오기). 저장은 API로 현재 상태를
/// 조회해 기록하고, 열기는 파일을 역직렬화해 API로 DB에 재적재한다.
/// </summary>
public interface IProjectService
{
    /// <summary>마지막으로 저장/열기한 파일 경로 (없으면 null — 미저장 프로젝트).</summary>
    string? CurrentPath { get; }

    /// <summary>현재 선창(tankId)의 전체 상태를 조회해 지정 경로에 프로젝트 파일로 기록. CurrentPath 갱신.</summary>
    Task SaveAsync(string tankId, string path, CancellationToken ct = default);

    /// <summary>프로젝트 파일을 읽어 DB에 재적재(선창 등록 → 면 재생성 → 영역 → 작업). CurrentPath 갱신. 로드된 문서 반환.</summary>
    Task<ProjectDoc> OpenAsync(string path, CancellationToken ct = default);
}
