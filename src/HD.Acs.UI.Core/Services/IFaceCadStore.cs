using HD.Acs.UI.Models;

namespace HD.Acs.UI.Services;

/// <summary>
/// 면별 CAD(DXF) 등록 데이터의 세션 저장소(단일 원천). DB가 아닌 프로젝트 파일(.hdacs)에만 저장되므로
/// 새 프로젝트 등록(면별 파일 지정)~저장, 열기~면 뷰 표시 사이를 이 인메모리 저장소가 잇는다.
/// 키=벽면 코드(B/SL/PL/SM/PM/SU/PU/T/F/A).
/// </summary>
public interface IFaceCadStore
{
    /// <summary>등록된 전체 면 CAD (벽면 코드→스냅샷).</summary>
    IReadOnlyDictionary<string, FaceCadDoc> All { get; }

    /// <summary>해당 면의 CAD 스냅샷(없으면 null).</summary>
    FaceCadDoc? Get(string wallCode);

    /// <summary>면 CAD 등록/교체.</summary>
    void Set(FaceCadDoc doc);

    /// <summary>면 CAD 제거.</summary>
    void Remove(string wallCode);

    /// <summary>전체 비우기(새 프로젝트 시작 시).</summary>
    void Clear();

    /// <summary>프로젝트 열기 시 저장소를 문서 내용으로 교체(null=비움).</summary>
    void LoadFrom(IEnumerable<FaceCadDoc>? docs);
}

/// <summary>기본 구현 — 스레드 안전(간단 lock), 벽면 코드 키.</summary>
public sealed class FaceCadStore : IFaceCadStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FaceCadDoc> _map = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, FaceCadDoc> All
    {
        get { lock (_gate) return new Dictionary<string, FaceCadDoc>(_map, StringComparer.OrdinalIgnoreCase); }
    }

    public FaceCadDoc? Get(string wallCode)
    {
        lock (_gate) return _map.TryGetValue(wallCode, out var d) ? d : null;
    }

    public void Set(FaceCadDoc doc)
    {
        lock (_gate) _map[doc.WallCode] = doc;
    }

    public void Remove(string wallCode)
    {
        lock (_gate) _map.Remove(wallCode);
    }

    public void Clear()
    {
        lock (_gate) _map.Clear();
    }

    public void LoadFrom(IEnumerable<FaceCadDoc>? docs)
    {
        lock (_gate)
        {
            _map.Clear();
            if (docs is null) return;
            foreach (var d in docs) _map[d.WallCode] = d;
        }
    }
}
