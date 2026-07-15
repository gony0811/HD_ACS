using HD.Acs.Core.Domain;

namespace HD.Acs.Core.Graph;

public sealed record GraphNode(string NodeId, string MapId, double X, double Y,
    double? Theta, double? AllowedDevXy, double? AllowedDevTheta, NodeType Type);

public sealed record GraphEdge(string EdgeId, string StartNodeId, string EndNodeId,
    bool Bidirectional, EdgeType Type, double? Length);

public sealed record PathStep(GraphNode Node, GraphEdge? ViaEdge);

/// <summary>
/// 인메모리 토폴로지 그래프 (기동 시 ref.node/ref.edge 로드, 맵 버전 단위 캐시).
/// 경로 계산은 층(map) 내부로 한정 — MANUAL_TRANSFER 엣지는 탐색에서 제외 [Q9].
/// NAMUGA_ACS PathManager의 "메모리 로드 + Dijkstra" 패턴 승계.
/// </summary>
public sealed class MapGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly Dictionary<string, List<(GraphEdge Edge, string ToNode)>> _adj = new();

    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;

    public void AddNode(GraphNode node)
    {
        _nodes[node.NodeId] = node;
        _adj.TryAdd(node.NodeId, new List<(GraphEdge, string)>());
    }

    public void AddEdge(GraphEdge edge)
    {
        if (edge.Type == EdgeType.ManualTransfer) return;  // 경로계산/Order 생성 항상 제외
        _adj[edge.StartNodeId].Add((edge, edge.EndNodeId));
        if (edge.Bidirectional)
            _adj[edge.EndNodeId].Add((edge, edge.StartNodeId));
    }

    /// <summary>Dijkstra 최단 경로. 비용 = edge.Length ?? 유클리드 거리.</summary>
    public IReadOnlyList<PathStep> FindPath(string fromNodeId, string toNodeId)
    {
        if (fromNodeId == toNodeId)
            return new[] { new PathStep(_nodes[fromNodeId], null) };

        var dist = new Dictionary<string, double> { [fromNodeId] = 0 };
        var prev = new Dictionary<string, (string Node, GraphEdge Edge)>();
        var pq = new PriorityQueue<string, double>();
        pq.Enqueue(fromNodeId, 0);
        var visited = new HashSet<string>();

        while (pq.TryDequeue(out var cur, out var d))
        {
            if (!visited.Add(cur)) continue;
            if (cur == toNodeId) break;

            foreach (var (edge, next) in _adj[cur])
            {
                if (visited.Contains(next)) continue;
                var cost = edge.Length ?? Euclid(_nodes[cur], _nodes[next]);
                var nd = d + cost;
                if (nd < dist.GetValueOrDefault(next, double.PositiveInfinity))
                {
                    dist[next] = nd;
                    prev[next] = (cur, edge);
                    pq.Enqueue(next, nd);
                }
            }
        }

        if (!prev.ContainsKey(toNodeId))
            throw new InvalidOperationException($"경로 없음: {fromNodeId} -> {toNodeId} (같은 층인지 확인)");

        var steps = new List<PathStep>();
        var walk = toNodeId;
        while (walk != fromNodeId)
        {
            var (p, e) = prev[walk];
            steps.Add(new PathStep(_nodes[walk], e));
            walk = p;
        }
        steps.Add(new PathStep(_nodes[fromNodeId], null));
        steps.Reverse();
        return steps;
    }

    private static double Euclid(GraphNode a, GraphNode b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
