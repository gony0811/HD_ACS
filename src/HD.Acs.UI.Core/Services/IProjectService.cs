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

    /// <summary>현재 선창(tankId)의 전체 상태를 조회해 지정 경로에 프로젝트 파일로 기록. CurrentPath 갱신.
    /// <paramref name="progress"/>는 호출 스레드에서 동기로 불린다(null = 보고 안 함).</summary>
    Task SaveAsync(string tankId, string path, CancellationToken ct = default, IProgress<ProjectProgress>? progress = null);

    /// <summary>프로젝트 파일을 읽어 DB에 재적재(선창 등록 → 면 재생성 → 영역 → 작업). CurrentPath 갱신. 로드된 문서 반환.
    /// <paramref name="progress"/>는 호출 스레드에서 동기로 불린다(null = 보고 안 함).</summary>
    Task<ProjectDoc> OpenAsync(string path, CancellationToken ct = default, IProgress<ProjectProgress>? progress = null);

    /// <summary>프로젝트 파일을 읽기만 한다(DB 무변경) — 새 프로젝트 창이 베이스 파일 내용을 보여 줄 때 쓴다.</summary>
    Task<ProjectDoc> ReadAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>
    /// 베이스 프로젝트 파일의 형상·영역·작업·시나리오를 새 선창 ID 로 복제해 DB 에 등록한다.
    /// 영역 ID·taskId 는 새로 발급하고(베이스 선창이 DB 에 남아 있어도 겹치지 않게), 캘리브레이션은 복사하지 않는다.
    /// 새 선창 ID 가 서버에 이미 있으면 거부한다(가져오기는 그 선창의 영역을 통째로 바꾸므로).
    /// inspectionKind 를 주면 베이스 파일의 검사 종류 대신 그 값을 쓴다(용접검사 계획을 가스검사 선창의 바탕으로).
    /// CurrentPath 는 비운다 — 저장이 베이스 파일을 덮어쓰지 않게. 반환 = 새 선창 문서.
    /// </summary>
    Task<ProjectDoc> CreateFromBaseAsync(string basePath, string newTankId, CancellationToken ct = default,
        IProgress<ProjectProgress>? progress = null, string? inspectionKind = null) => throw new NotSupportedException();
}
