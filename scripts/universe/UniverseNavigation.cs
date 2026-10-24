using Godot;
using System.Collections.Generic;

/// <summary>宇宙中的表面边界；后续轨道/登陆沿用天体 Id 与表面距离。</summary>
public static class UniverseNavigation
{
    public static Vector2 SpawnNear(UniverseGenerator.CelestialBody planet,
        IReadOnlyList<UniverseGenerator.CelestialBody> bodies, float shipRadius)
    {
        for (var i = 0; i < 16; i++)
        {
            var direction = Vector2.Left.Rotated(i * Mathf.Tau / 16);
            var candidate = planet.Position + direction * (planet.Radius + Mathf.Max(380, shipRadius * 3));
            var safe = true;
            foreach (var body in bodies)
                if (body.HasSurface && body.SurfaceDistance(candidate) < shipRadius + 32) safe = false;
            if (safe) return candidate;
        }
        return planet.Position + Vector2.Left * (planet.Radius + shipRadius + 32);
    }

    /// <summary>阻止舰体穿进星球；只消除朝内速度，保留沿表面的切向运动。
    /// 返回被消除的朝内速度大小（&gt;0 表示撞上表面，0 表示未接触）。</summary>
    public static float ResolveSurface(UniverseGenerator.CelestialBody body, float shipRadius,
        ref Vector2 position, ref Vector2 velocity)
    {
        if (!body.HasSurface) return 0;
        var offset = position - body.Position;
        var minimumDistance = body.Radius + shipRadius + 16;
        if (offset.LengthSquared() >= minimumDistance * minimumDistance) return 0;
        var normal = offset.IsZeroApprox() ? Vector2.Left : offset.Normalized();
        position = body.Position + normal * minimumDistance;
        var inwardSpeed = velocity.Dot(normal);
        if (inwardSpeed < 0) velocity -= normal * inwardSpeed;
        return inwardSpeed < 0 ? -inwardSpeed : 0;
    }
}
