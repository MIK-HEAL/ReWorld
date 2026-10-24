using Godot;
using System;
using System.Collections.Generic;

/// <summary>Plans continuous world-space flight around solid bodies, with rounded turns.</summary>
public static class WarpRoutePlanner
{
    public readonly record struct Obstacle(Vector2 Center, float Radius);
    public static List<Obstacle> Obstacles(Vector2 start, Vector2 finish, float shipRadius)
    {
        var first = UniverseGenerator.WorldToSystemCell(start);
        var last = UniverseGenerator.WorldToSystemCell(finish);
        var min = new Vector2I(Math.Min(first.X,last.X)-2, Math.Min(first.Y,last.Y)-2);
        var max = new Vector2I(Math.Max(first.X,last.X)+2, Math.Max(first.Y,last.Y)+2);
        var result = new List<Obstacle>();
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
            foreach (var body in UniverseGenerator.GenerateSystem(new Vector2I(x,y)).Bodies)
                if (body.HasSurface || body.Kind == UniverseGenerator.CelestialKind.Station)
                    result.Add(new Obstacle(body.Position, body.Radius + shipRadius + (body.HasSurface ? 120 : 32)));
        return result;
    }
    public static Vector2[]? Plan(Vector2 start, Vector2 finish, float shipRadius)
    {
        if (start.DistanceSquaredTo(finish) < 1) return null;
        var obstacles = Obstacles(start, finish, shipRadius);
        if (ClearSegment(start, finish, obstacles)) return new[] { start, finish };
        if (!ClearSegment(start, start, obstacles) || !ClearSegment(finish, finish, obstacles)) return null;

        // A visibility graph can route around both departure and arrival bodies;
        // a single bowed arc cannot handle opposite obstacles at the two ends.
        var bounds = new Rect2(start, Vector2.Zero).Expand(finish).Grow(12000);
        var relevant = obstacles.FindAll(o => bounds.Grow(o.Radius).HasPoint(o.Center));
        var nodes = new List<Vector2> { start, finish };
        foreach (var obstacle in relevant)
            for (var i = 0; i < 16; i++)
            {
                var point = obstacle.Center + Vector2.Right.Rotated(i*Mathf.Tau/16)*obstacle.Radius*1.25f;
                if (bounds.HasPoint(point) && ClearSegment(point, point, relevant)) nodes.Add(point);
            }
        var costs = new float[nodes.Count];
        Array.Fill(costs, float.PositiveInfinity);
        var previous = new int[nodes.Count];
        Array.Fill(previous, -1);
        var visited = new bool[nodes.Count];
        var pending = new PriorityQueue<int, float>();
        costs[0] = 0;
        pending.Enqueue(0, start.DistanceTo(finish));
        while (pending.TryDequeue(out var current, out _))
        {
            if (visited[current]) continue;
            if (current == 1)
            {
                var path = new List<Vector2>();
                for (var index = 1; index >= 0; index = previous[index]) path.Add(nodes[index]);
                path.Reverse();
                return RoundCorners(path, obstacles);
            }
            visited[current] = true;
            for (var next = 1; next < nodes.Count; next++)
            {
                if (visited[next]) continue;
                var cost = costs[current] + nodes[current].DistanceTo(nodes[next]);
                if (cost >= costs[next] || !ClearSegment(nodes[current], nodes[next], relevant)) continue;
                costs[next] = cost;
                previous[next] = current;
                pending.Enqueue(next, cost + nodes[next].DistanceTo(finish));
            }
        }
        return null;
    }

    private static Vector2[]? RoundCorners(List<Vector2> path, IReadOnlyList<Obstacle> obstacles)
    {
        var points = new List<Vector2> { path[0] };
        for (var i = 1; i < path.Count-1; i++)
        {
            var corner = path[i];
            var incoming = corner-path[i-1];
            var outgoing = path[i+1]-corner;
            var inset = Math.Min(6000, Math.Min(incoming.Length(), outgoing.Length())*0.4f);
            var curve = new Vector2[33];
            while (true)
            {
                var entry = corner-incoming.Normalized()*inset;
                var exit = corner+outgoing.Normalized()*inset;
                var safe = ClearSegment(points[^1], entry, obstacles);
                var last = curve[0] = entry;
                for (var j = 1; j < curve.Length && safe; j++)
                {
                    var t = j/(float)(curve.Length-1);
                    var point = curve[j] = entry*((1-t)*(1-t)) + corner*(2*t*(1-t)) + exit*(t*t);
                    safe = ClearSegment(last, point, obstacles);
                    last = point;
                }
                if (safe) { points.AddRange(curve); break; }
                inset *= 0.5f;
                if (inset < 1) return null;
            }
        }
        points.Add(path[^1]);
        for (var i = 1; i < points.Count; i++)
            if (!ClearSegment(points[i-1], points[i], obstacles)) return null;
        return points.ToArray();
    }
    public static bool ClearSegment(Vector2 from, Vector2 to, IReadOnlyList<Obstacle> obstacles)
    {
        var direction = to-from;
        var lengthSquared = direction.LengthSquared();
        for (var i = 0; i < obstacles.Count; i++)
        {
            var obstacle = obstacles[i];
            var t = lengthSquared > 0 ? Mathf.Clamp((obstacle.Center-from).Dot(direction)/lengthSquared,0,1) : 0;
            if ((from+direction*t).DistanceSquaredTo(obstacle.Center) < obstacle.Radius*obstacle.Radius) return false;
        }
        return true;
    }
}
