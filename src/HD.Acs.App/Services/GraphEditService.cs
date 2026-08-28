using HD.Acs.Core.Geometry;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 2D 평면도에서 등록하는 네비게이션 그래프(ref.node/ref.edge) 편집.
/// 노드 좌표는 맵 프레임(VDA nodePosition)으로 저장하며, 평면도 도면 좌표 ↔ 맵 좌표는 층 유효 T_W_D로 변환한다
/// (캘리브레이션 없으면 항등 — 도면≈맵 placeholder, 기존 goto/마커와 동일). 엣지는 두 노드를 연결한다.
/// </summary>
public sealed class GraphEditService
{
    private readonly AcsDbContext _db;
    public GraphEditService(AcsDbContext db) => _db = db;

    public sealed record NodeView(string NodeId, string MapId, int Level, string NodeType,
        double X, double Y, double? Theta, double DrawingX, double DrawingY);
    public sealed record EdgeView(string EdgeId, string MapId, string StartNodeId, string EndNodeId,
        bool Bidirectional, string EdgeType);

    /// <summary>층 유효 T_W_D — 캘리브레이션 맵버전이 현재 맵버전과 일치하면 적용, 아니면 항등.</summary>
    private async Task<DrawingTransform> ResolveTwdAsync(string mapId, CancellationToken ct)
    {
        var map = await _db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.MapId == mapId, ct);
        if (map is null) return new DrawingTransform(0, 0, 0);
        var cal = await _db.MapCalibrations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.MapId == mapId && c.MapVersion == map.Version, ct);
        return cal is null ? new DrawingTransform(0, 0, 0) : new DrawingTransform(cal.Tx, cal.Ty, cal.YawRad);
    }

    private async Task EnsureMapAsync(string tankId, int level, string mapId, CancellationToken ct)
    {
        if (!await _db.Maps.AnyAsync(m => m.MapId == mapId, ct))
            _db.Maps.Add(new MapEntity { MapId = mapId, TankId = tankId, Level = level, Name = mapId, Version = 1, IsActive = true });
    }

    /// <summary>노드 생성 — 도면 좌표(평면도 클릭)를 맵 좌표로 변환해 저장. ref.map 없으면 자동 생성.</summary>
    public async Task<NodeView> CreateNodeAsync(string tankId, int level, double drawingX, double drawingY,
        double? theta, string? nodeType, CancellationToken ct = default)
    {
        var mapId = $"{tankId}-L{level}";
        await EnsureMapAsync(tankId, level, mapId, ct);
        var t = await ResolveTwdAsync(mapId, ct);
        var (mx, my) = t.DrawingToMap(drawingX, drawingY);
        var type = string.IsNullOrWhiteSpace(nodeType) ? "WAYPOINT" : nodeType!;
        var nodeId = $"{mapId}-N-{Guid.NewGuid().ToString()[..8]}";
        _db.Nodes.Add(new NodeEntity { NodeId = nodeId, MapId = mapId, X = mx, Y = my, Theta = theta, NodeType = type });
        await _db.SaveChangesAsync(ct);
        return new NodeView(nodeId, mapId, level, type, mx, my, theta, drawingX, drawingY);
    }

    public async Task<IReadOnlyList<NodeView>> GetNodesAsync(string tankId, int? level, CancellationToken ct = default)
    {
        var q = _db.Nodes.AsNoTracking();
        q = level is int lv ? q.Where(n => n.MapId == $"{tankId}-L{lv}") : q.Where(n => n.MapId.StartsWith($"{tankId}-L"));
        var nodes = await q.ToListAsync(ct);
        var result = new List<NodeView>(nodes.Count);
        var twdCache = new Dictionary<string, DrawingTransform>();
        foreach (var n in nodes)
        {
            if (!twdCache.TryGetValue(n.MapId, out var t)) twdCache[n.MapId] = t = await ResolveTwdAsync(n.MapId, ct);
            var (dx, dy) = t.MapToDrawing(n.X, n.Y);
            result.Add(new NodeView(n.NodeId, n.MapId, LevelOf(n.MapId), n.NodeType, n.X, n.Y, n.Theta, dx, dy));
        }
        return result;
    }

    /// <summary>노드 삭제 — 인접 엣지(start/end 참조)를 먼저 제거(정리) 후 노드 삭제.</summary>
    public async Task<bool> DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var node = await _db.Nodes.FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null) return false;
        var incident = await _db.Edges.Where(e => e.StartNodeId == nodeId || e.EndNodeId == nodeId).ToListAsync(ct);
        _db.Edges.RemoveRange(incident);
        _db.Nodes.Remove(node);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>엣지 생성 — 두 노드를 연결(같은 맵). 노드 없음/다른 맵/자기연결/중복은 예외.</summary>
    public async Task<EdgeView> CreateEdgeAsync(string startNodeId, string endNodeId,
        bool bidirectional, string? edgeType, CancellationToken ct = default)
    {
        if (startNodeId == endNodeId) throw new InvalidOperationException("시작/끝 노드가 같습니다.");
        var s = await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.NodeId == startNodeId, ct)
                ?? throw new InvalidOperationException($"노드 없음: {startNodeId}");
        var e = await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.NodeId == endNodeId, ct)
                ?? throw new InvalidOperationException($"노드 없음: {endNodeId}");
        if (s.MapId != e.MapId) throw new InvalidOperationException("서로 다른 층의 노드는 연결할 수 없습니다.");

        bool dup = await _db.Edges.AnyAsync(x =>
            (x.StartNodeId == startNodeId && x.EndNodeId == endNodeId) ||
            (x.Bidirectional && x.StartNodeId == endNodeId && x.EndNodeId == startNodeId), ct);
        if (dup) throw new InvalidOperationException("이미 연결된 두 노드입니다.");

        var type = string.IsNullOrWhiteSpace(edgeType) ? "TRAVEL" : edgeType!;
        var edgeId = $"{s.MapId}-E-{Guid.NewGuid().ToString()[..8]}";
        _db.Edges.Add(new EdgeEntity
        {
            EdgeId = edgeId, MapId = s.MapId, StartNodeId = startNodeId, EndNodeId = endNodeId,
            Bidirectional = bidirectional, EdgeType = type
        });
        await _db.SaveChangesAsync(ct);
        return new EdgeView(edgeId, s.MapId, startNodeId, endNodeId, bidirectional, type);
    }

    public async Task<IReadOnlyList<EdgeView>> GetEdgesAsync(string tankId, int? level, CancellationToken ct = default)
    {
        var q = _db.Edges.AsNoTracking();
        q = level is int lv ? q.Where(e => e.MapId == $"{tankId}-L{lv}") : q.Where(e => e.MapId.StartsWith($"{tankId}-L"));
        var edges = await q.ToListAsync(ct);
        return edges.Select(e => new EdgeView(e.EdgeId, e.MapId, e.StartNodeId, e.EndNodeId, e.Bidirectional, e.EdgeType)).ToList();
    }

    public async Task<bool> DeleteEdgeAsync(string edgeId, CancellationToken ct = default)
    {
        var edge = await _db.Edges.FirstOrDefaultAsync(e => e.EdgeId == edgeId, ct);
        if (edge is null) return false;
        _db.Edges.Remove(edge);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static int LevelOf(string mapId)
    {
        int i = mapId.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        return i > 0 && int.TryParse(mapId[(i + 2)..], out var lv) ? lv : 0;
    }
}
