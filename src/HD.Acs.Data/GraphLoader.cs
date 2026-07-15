using HD.Acs.Core.Domain;
using HD.Acs.Core.Graph;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.Data;

/// <summary>ref.node / ref.edge → 인메모리 MapGraph 로드 (층 단위)</summary>
public static class GraphLoader
{
    public static async Task<MapGraph> LoadAsync(AcsDbContext db, string mapId, CancellationToken ct = default)
    {
        var graph = new MapGraph();

        var nodes = await db.Nodes.AsNoTracking().Where(n => n.MapId == mapId).ToListAsync(ct);
        foreach (var n in nodes)
            graph.AddNode(new GraphNode(n.NodeId, n.MapId, n.X, n.Y, n.Theta,
                n.AllowedDevXy, n.AllowedDevTheta, ParseNodeType(n.NodeType)));

        var edges = await db.Edges.AsNoTracking().Where(e => e.MapId == mapId).ToListAsync(ct);
        foreach (var e in edges)
            graph.AddEdge(new GraphEdge(e.EdgeId, e.StartNodeId, e.EndNodeId,
                e.Bidirectional,
                e.EdgeType == "MANUAL_TRANSFER" ? EdgeType.ManualTransfer : EdgeType.Travel,
                e.Length));

        return graph;
    }

    private static NodeType ParseNodeType(string t) => t switch
    {
        "INSPECTION_STOP" => NodeType.InspectionStop,
        "ELEVATOR" => NodeType.Elevator,
        "CHARGING" => NodeType.Charging,
        "PARKING" => NodeType.Parking,
        _ => NodeType.Waypoint
    };
}
