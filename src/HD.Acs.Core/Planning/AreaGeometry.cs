namespace HD.Acs.Core.Planning;

/// <summary>영역(Area) 순수 기하 [PHASE2 개정]. 디폴트 정차 pose·경계 판정. 도면 좌표(m).</summary>
public static class AreaGeometry
{
    /// <summary>디폴트 정차 pose — 영역 중앙 + 벽면(−법선) 방향. 오버라이드 미지정 시 사용.</summary>
    public static (double X, double Y, double Theta) DefaultStationPose(
        double minX, double minY, double maxX, double maxY, double nx, double ny)
    {
        double cx = (minX + maxX) / 2.0;
        double cy = (minY + maxY) / 2.0;
        double theta = Math.Atan2(-ny, -nx);   // 벽면을 바라봄(법선 반대)
        return (cx, cy, theta);
    }

    /// <summary>점 (x,y)가 영역 경계 내부(포함)인지.</summary>
    public static bool InBounds(double x, double y, double minX, double minY, double maxX, double maxY) =>
        x >= minX && x <= maxX && y >= minY && y <= maxY;
}
