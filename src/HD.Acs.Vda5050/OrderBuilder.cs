using HD.Acs.Core.Abstractions;
using HD.Acs.Core.Graph;
using HD.Acs.Vda5050.Messages;

namespace HD.Acs.Vda5050;

/// <summary>
/// 시나리오(층 단위 계획) + 그래프 + 시작 노드 → VDA 5050 Order.
/// sequenceId: 노드=짝수(0,2,4…), 엣지=홀수(1,3,5…). 전체 Base 선릴리즈 [ADR-002].
/// </summary>
public sealed class OrderBuilder
{
    public Vda5050Order Build(string orderId, int orderUpdateId,
        string startNodeId, IReadOnlyList<PlannedPoint> points, MapGraph graph)
    {
        var order = new Vda5050Order { OrderId = orderId, OrderUpdateId = orderUpdateId };
        var seq = 0;
        var cursor = startNodeId;
        var firstStep = true;

        foreach (var point in points)
        {
            var path = graph.FindPath(cursor, point.NodeId);
            foreach (var step in path)
            {
                // 이전 구간의 마지막 노드 == 이번 구간의 첫 노드 → 중복 삽입 방지
                if (!firstStep && step.ViaEdge == null && step.Node.NodeId == cursor)
                {
                    // 같은 노드에서 연속 검사 지점인 경우 기존 노드에 액션만 추가
                    if (step.Node.NodeId == point.NodeId)
                        order.Nodes[^1].Actions.AddRange(point.Actions.Select(ToVdaAction));
                    continue;
                }

                if (step.ViaEdge != null)
                    order.Edges.Add(new OrderEdge
                    {
                        EdgeId = step.ViaEdge.EdgeId,
                        SequenceId = seq++,                       // 홀수 자리
                        StartNodeId = order.Nodes[^1].NodeId,
                        EndNodeId = step.Node.NodeId,
                        Released = true
                    });

                var orderNode = new OrderNode
                {
                    NodeId = step.Node.NodeId,
                    SequenceId = seq++,                           // 짝수 자리
                    Released = true,
                    NodePosition = new NodePosition
                    {
                        X = step.Node.X, Y = step.Node.Y, Theta = step.Node.Theta,
                        AllowedDeviationXY = step.Node.AllowedDevXy,
                        AllowedDeviationTheta = step.Node.AllowedDevTheta,
                        MapId = step.Node.MapId
                    }
                };

                if (step.Node.NodeId == point.NodeId)             // 검사 지점 도착 노드에 액션 부착
                    orderNode.Actions.AddRange(point.Actions.Select(ToVdaAction));

                order.Nodes.Add(orderNode);
                firstStep = false;
            }
            cursor = point.NodeId;
        }
        return order;
    }

    private static VdaAction ToVdaAction(PlannedAction a) => new()
    {
        ActionType = a.ActionType,
        ActionId = a.ActionId.ToString(),
        BlockingType = a.BlockingType,
        ActionParameters = a.Parameters
            .Select(kv => new ActionParameter { Key = kv.Key, Value = kv.Value }).ToList()
    };
}
