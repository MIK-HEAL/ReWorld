using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public class CombatVessel
{
    public required string Name;
    public required ModularShip Ship;
    public int FactionId = FactionRelations.Player;
    public Vector2 Position, PreviousPosition, Velocity;
    public float Heading, PreviousHeading, AngularVelocity;
    public bool Mineral;
    /// <summary>Static combat bodies (stations/outposts) block moving ships but do not receive impulse motion.</summary>
    public bool CollisionStatic;
    /// <summary>Set when the vessel died from ramming (mutual collision damage), so it is not counted as an unprovoked player attack.</summary>
    public bool KilledByCollision;
    /// <summary>碰撞伤害冷却：一次碰撞事件触发后进入冷却，持续接触期间不再每帧叠加伤害（reassembly 碰撞以反弹为主）。</summary>
    public float CollisionDamageCooldown;
    public string DepositId = "";
    public float Energy, Shield;
    /// <summary>本帧累积的表面撞击速度与法向，由物理帧清零并施放为碰撞伤害。</summary>
    public float SurfaceImpact;
    public Vector2 SurfaceNormal;
    public float EnergyCapacity, Generation, ShieldCapacity, ShieldRadius, ShieldRegen;
    public CombatVessel? CombatTarget;
    public readonly Dictionary<int, float> Cooldowns = new();
    public ModularShip.Block[] Active = Array.Empty<ModularShip.Block>();
    public bool HasTractor;
    private ulong _revision = ulong.MaxValue;
    private bool _initialized;
    public bool Alive => Mineral ? Ship.Blocks.Count > 0 : Ship.HasLiveCore;
    public void Refresh()
    {
        if (_revision == Ship.Revision) return;
        Rebuild();
    }
    private void Rebuild()
    {
        Active = Ship.HasLiveCore ? Ship.GetMainComponent().Where(b => b.Enabled && b.Health > 0).ToArray() : Array.Empty<ModularShip.Block>();
        HasTractor = Active.Any(b => (b.Features & BlockFeatures.Tractor) != 0);
        _revision = Ship.Revision;
        EnergyCapacity = 60 + Active.Sum(b => b.Spec.PowerCapacity);
        Generation = 8 + Active.Sum(b => b.Spec.Generation);
        ShieldCapacity = Active.Where(b => (b.Features & BlockFeatures.Shield) != 0).Sum(b => b.Spec.ShieldStrength);
        ShieldRadius = Active.Where(b => (b.Features & BlockFeatures.Shield) != 0)
            .Select(b => b.Spec.ShieldRadius + b.Position.DistanceTo(Ship.CenterOfMass())).DefaultIfEmpty(0).Max();
        ShieldRegen = Active.Where(b => (b.Features & BlockFeatures.Shield) != 0).Sum(b => b.Spec.ShieldRegen);
        Energy = _initialized ? Math.Min(Energy, EnergyCapacity) : EnergyCapacity;
        Shield = _initialized ? Math.Min(Shield, ShieldCapacity) : ShieldCapacity;
        _initialized = true;
        var liveIds = Active.Select(b => b.Id).ToHashSet();
        foreach (var id in Cooldowns.Keys.Where(id => !liveIds.Contains(id)).ToArray()) Cooldowns.Remove(id);
    }
}

/// <summary>Shared manual/AI weapons, power, shields, swept polygon hits, local damage and physical debris.</summary>
public sealed class CombatWorld
{
    public sealed class Projectile
    {
        public required CombatVessel Owner;
        public Vector2 Position, Previous, Velocity;
        public float Damage, Life, Splash, DroneTimer;
        public WeaponKind Kind;
        public int Faction;
        public CombatVessel? Target;
    }
    public sealed class Resource
    {
        public Vector2 Position;
        public int Amount;
        public string DepositId = "";
    }
    public sealed class Debris
    {
        public required ModularShip Ship;
        public Vector2 Position, Velocity;
        public float Heading, Life = 25;
    }
    public sealed class Beam
    {
        public Vector2 Start, End;
        public bool Healing;
        public float Life = 0.12f;
    }
    public readonly List<Projectile> Projectiles = new();
    public readonly List<Resource> Resources = new();
    public readonly List<Debris> Fragments = new();
    public readonly List<Beam> Beams = new();
    public event Action<string>? DepositCollected;

    /// <summary>World-space hull vertices for one actor, pooled across ticks so steady-state physics allocates nothing.</summary>
    private sealed class CollisionGeometry
    {
        public readonly List<Vector2[]> Hulls = new();
        public readonly List<ModularShip.Block> Blocks = new();
        public int Count;
        public float Radius;
    }
    private static readonly List<CollisionGeometry> _geometryPool = new();
    private static readonly List<bool> _displaced = new();
    private static readonly CollisionGeometry _queryGeometry = new(), _otherQueryGeometry = new();

    private static void RefreshGeometry(CollisionGeometry geometry, CombatVessel actor)
    {
        var hulls = geometry.Hulls;
        var blocks = geometry.Blocks;
        var count = 0;
        var radius = 0.0f;
        var center = actor.Ship.CenterOfMass();
        // Index Blocks instead of foreach: the IReadOnlyList enumerator boxes on every tick.
        var shipBlocks = actor.Ship.Blocks;
        for (var b = 0; b < shipBlocks.Count; b++)
        {
            var block = shipBlocks[b];
            if (!block.Enabled || block.Health <= 0) continue;
            var local = block.Spec.Vertices();
            while (hulls.Count <= count) hulls.Add(Array.Empty<Vector2>());
            while (blocks.Count <= count) blocks.Add(null!);
            if (hulls[count].Length != local.Length) hulls[count] = new Vector2[local.Length];
            blocks[count] = block;
            var hull = hulls[count];
            var offset = actor.Position + (block.Position - center).Rotated(actor.Heading);
            var rotation = block.Angle + actor.Heading;
            var reach = 0.0f;
            for (var v = 0; v < local.Length; v++)
            {
                hull[v] = offset + local[v].Rotated(rotation);
                var distance = hull[v].DistanceTo(actor.Position);
                if (distance > reach) reach = distance;
            }
            if (reach > radius) radius = reach;
            count++;
        }
        geometry.Count = count;
        geometry.Radius = radius;
    }

    /// <summary>Resolve contact using the actual convex polygon of each live module.</summary>
    public static void ResolveCollisions(IReadOnlyList<CombatVessel> actors)
    {
        var count = actors.Count;
        while (_geometryPool.Count < count) _geometryPool.Add(new CollisionGeometry());
        while (_displaced.Count < count) _displaced.Add(false);
        for (var i = 0; i < count; i++)
        {
            var actor = actors[i];
            if (actor.Alive && !actor.Mineral) RefreshGeometry(_geometryPool[i], actor);
            else _geometryPool[i].Count = 0;
            _displaced[i] = false;
        }
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            var geometryA = _geometryPool[i];
            if (geometryA.Count == 0) continue;
            for (var j = i + 1; j < count; j++)
            {
                var b = actors[j];
                var geometryB = _geometryPool[j];
                if (geometryB.Count == 0 || a.CollisionStatic && b.CollisionStatic) continue;
                // A station is non-convex as a whole: escaping one module can still touch a neighboring module.
                // Keep resolving the minimum module contact until the compound hull is clear.
                for (var pass = 0; pass < 64; pass++)
                {
                    if (_displaced[i]) { RefreshGeometry(geometryA, a); _displaced[i] = false; }
                    if (_displaced[j]) { RefreshGeometry(geometryB, b); _displaced[j] = false; }
                    if (a.Position.DistanceTo(b.Position) > geometryA.Radius + geometryB.Radius) break;
                    if (!TryGetContact(geometryA, geometryB, a, b, out var normal, out var penetration, out _, out _)) break;
                    if (a.CollisionStatic)
                    {
                        b.Position += normal * (penetration + 0.1f);
                        RemoveInwardVelocity(b, normal);
                    }
                    else if (b.CollisionStatic)
                    {
                        a.Position -= normal * (penetration + 0.1f);
                        RemoveInwardVelocity(a, -normal);
                    }
                    else
                    {
                        var half = (penetration + 0.1f) * 0.5f;
                        a.Position -= normal * half;
                        b.Position += normal * half;
                        RemoveInwardVelocity(a, -normal);
                        RemoveInwardVelocity(b, normal);
                    }
                    a.PreviousPosition = a.Position;
                    b.PreviousPosition = b.Position;
                    _displaced[i] = _displaced[j] = true;
                }
            }
        }

        static void RemoveInwardVelocity(CombatVessel vessel, Vector2 outward)
        {
            var inward = vessel.Velocity.Dot(outward);
            if (inward < 0) vessel.Velocity -= outward * inward;
        }
    }

    public static bool Collides(CombatVessel a, CombatVessel b)
    {
        if (!a.Alive || a.Mineral || !b.Alive || b.Mineral) return false;
        RefreshGeometry(_queryGeometry, a);
        RefreshGeometry(_otherQueryGeometry, b);
        return TryGetContact(_queryGeometry, _otherQueryGeometry, a, b, out _, out _, out _, out _);
    }

    private static bool TryGetContact(CollisionGeometry geometryA, CollisionGeometry geometryB,
        CombatVessel a, CombatVessel b, out Vector2 normal, out float penetration,
        out ModularShip.Block? blockA, out ModularShip.Block? blockB)
    {
        normal = Vector2.Zero;
        penetration = float.MaxValue;
        blockA = null;
        blockB = null;
        var hullsA = geometryA.Hulls;
        var hullsB = geometryB.Hulls;
        var blocksA = geometryA.Blocks;
        var blocksB = geometryB.Blocks;
        for (var i = 0; i < geometryA.Count; i++)
        {
            var verticesA = hullsA[i];
            for (var j = 0; j < geometryB.Count; j++)
            {
                var verticesB = hullsB[j];
                if (!TryOverlap(verticesA, verticesB, out var axis, out var depth) || depth >= penetration) continue;
                if (axis.Dot(b.Position - a.Position) < 0) axis = -axis;
                penetration = depth;
                normal = axis;
                blockA = blocksA[i];
                blockB = blocksB[j];
            }
        }
        return penetration < float.MaxValue;
    }

    private static bool TryOverlap(Vector2[] a, Vector2[] b, out Vector2 minimumAxis, out float minimumDepth)
    {
        minimumAxis = Vector2.Zero;
        minimumDepth = float.MaxValue;
        return OverlapsOnAxes(a, b, ref minimumAxis, ref minimumDepth)
            && OverlapsOnAxes(b, a, ref minimumAxis, ref minimumDepth);
    }

    private static bool OverlapsOnAxes(Vector2[] hull, Vector2[] other, ref Vector2 minimumAxis, ref float minimumDepth)
    {
        for (var i = 0; i < hull.Length; i++)
        {
            var edge = hull[(i + 1) % hull.Length] - hull[i];
            var axis = new Vector2(-edge.Y, edge.X).Normalized();
            Project(hull, axis, out var minA, out var maxA);
            Project(other, axis, out var minB, out var maxB);
            var depth = Math.Min(maxA, maxB) - Math.Max(minA, minB);
            if (depth <= 0) return false;
            if (depth < minimumDepth) { minimumDepth = depth; minimumAxis = axis; }
        }
        return true;
    }

    private static void Project(Vector2[] polygon, Vector2 axis, out float min, out float max)
    {
        min = max = polygon[0].Dot(axis);
        for (var i = 1; i < polygon.Length; i++)
        {
            var projection = polygon[i].Dot(axis);
            min = Math.Min(min, projection);
            max = Math.Max(max, projection);
        }
    }
    public CombatVessel? NearestHostile(CombatVessel vessel, IReadOnlyList<CombatVessel> actors, FactionRelations relations, float range = 4500)
    {
        CombatVessel? nearest = null;
        var distance = range * range;
        for (var i = 0; i < actors.Count; i++)
        {
            var other = actors[i];
            if (!other.Alive || other.Mineral || !relations.Hostile(vessel.FactionId, other.FactionId)) continue;
            var d = other.Position.DistanceSquaredTo(vessel.Position);
            if (d < distance) { nearest = other; distance = d; }
        }
        return nearest;
    }

    public void Step(float dt, IReadOnlyList<CombatVessel> actors, FactionRelations relations,
        CombatVessel? player = null, bool firing = false, Vector2? aim = null)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        ResolveCollisionDamage(actors, dt);
        ResolveCollisions(actors);
        foreach (var beam in Beams) beam.Life -= dt;
        Beams.RemoveAll(b => b.Life <= 0);
        foreach (var fragment in Fragments) { fragment.Position += fragment.Velocity * dt; fragment.Heading += 0.2f * dt; fragment.Life -= dt; }
        Fragments.RemoveAll(f => f.Life <= 0);
        for (var actorIndex = 0; actorIndex < actors.Count; actorIndex++)
        {
            var actor = actors[actorIndex];
            if (!actor.Alive || actor.Mineral) continue;
            actor.Refresh();
            actor.Energy = Math.Min(actor.EnergyCapacity, actor.Energy + actor.Generation * dt);
            var regen = Math.Min(actor.ShieldCapacity - actor.Shield, Math.Min(actor.ShieldRegen * dt, actor.Energy));
            actor.Shield += regen;
            actor.Energy -= regen;
            foreach (var activeBlock in actor.Active)
                if (actor.Cooldowns.TryGetValue(activeBlock.Id, out var cooldown)) actor.Cooldowns[activeBlock.Id] = Math.Max(0, cooldown - dt);
            actor.CombatTarget = NearestHostile(actor, actors, relations);
            foreach (var block in actor.Active)
            {
                if (!block.Spec.IsWeapon || actor.Cooldowns.GetValueOrDefault(block.Id) > 0) continue;
                var spec = block.Spec;
                var kind = spec.WeaponKind;
                var origin = actor.Position + (block.Position - actor.Ship.CenterOfMass()).Rotated(actor.Heading);
                if (kind == WeaponKind.PointDefense)
                {
                    Projectile? missile = null;
                    var nearest = spec.Range * spec.Range;
                    foreach (var p in Projectiles)
                    {
                        var distance = p.Position.DistanceSquaredTo(origin);
                        if (p.Life > 0 && distance < nearest && relations.Hostile(actor.FactionId, p.Faction)) { missile = p; nearest = distance; }
                    }
                    if (missile is not null && actor.Energy >= spec.FireEnergy)
                    {
                        missile.Life = 0; SpendShot(actor, block);
                        Beams.Add(new Beam { Start = origin, End = missile.Position });
                        continue;
                    }
                }
                var target = actor.CombatTarget;
                if (kind == WeaponKind.Healer)
                    target = FindHealingTarget(actor, actors, relations, origin, spec.Range);
                var manual = actor == player && firing && kind != WeaponKind.Healer;
                if (!manual && target is null) continue;
                var destination = manual && aim.HasValue ? aim.Value : target!.Position;
                var offset = destination - origin;
                if (!manual && offset.Length() > spec.Range) continue;
                var direction = offset.Normalized();
                var forward = spec.WeaponDirection.Rotated(actor.Heading + block.Angle);
                var turret = (block.Features & BlockFeatures.Turret) != 0 || spec.Id is "cannon" or "laser" or "beam" or "point_defense";
                if (!turret && forward.Dot(direction) < Mathf.Cos(Mathf.DegToRad(15))) continue;
                if (actor.Energy < spec.FireEnergy) continue;
                SpendShot(actor, block);
                if (kind == WeaponKind.Healer && target is not null)
                {
                    ModularShip.Block? damaged = null;
                    foreach (var candidate in target.Active)
                        if (damaged is null || candidate.Health / candidate.Spec.Hull < damaged.Health / damaged.Spec.Hull) damaged = candidate;
                    if (damaged is not null) damaged.Health = Math.Min(damaged.Spec.Hull, damaged.Health + spec.Damage);
                    Beams.Add(new Beam { Start = origin, End = target.Position, Healing = true });
                }
                else if (kind == WeaponKind.Beam)
                {
                    var end = origin + direction * spec.Range;
                    var hit = FindHit(origin, end, actor, actors, relations);
                    if (hit.Actor is not null)
                    {
                        end = origin.Lerp(end, hit.Fraction);
                        ApplyDamage(hit.Actor, hit.Block, spec.Damage, direction);
                    }
                    Beams.Add(new Beam { Start = origin, End = end });
                }
                else
                    Projectiles.Add(new Projectile { Owner = actor, Faction = actor.FactionId, Kind = kind,
                        Position = origin + direction * (block.Spec.Radius + 2), Previous = origin,
                        Velocity = actor.Velocity + direction * spec.ProjectileSpeed, Damage = spec.Damage,
                        Life = Math.Max(0.2f, spec.Range / spec.ProjectileSpeed), Splash = spec.SplashRadius, Target = target });
            }
        }
        // Snapshot because drones may emit new projectiles.
        if (Projectiles.Count > 0)
            foreach (var projectile in Projectiles.ToArray())
            {
            if (projectile.Life <= 0) continue;
            projectile.Life -= dt;
            if (projectile.Target is { } target && (!target.Alive || !relations.Hostile(projectile.Faction, target.FactionId))) projectile.Target = null;
            if (projectile.Kind is WeaponKind.Missile or WeaponKind.Drone && projectile.Target is { } homing)
            {
                var speed = projectile.Velocity.Length();
                var desired = (homing.Position - projectile.Position).Normalized() * speed;
                projectile.Velocity = projectile.Velocity.Lerp(desired, Math.Min(1, dt * 4)).Normalized() * speed;
            }
            projectile.Previous = projectile.Position;
            projectile.Position += projectile.Velocity * dt;
            var hit = FindHit(projectile.Previous, projectile.Position, projectile.Owner, actors, relations);
            if (hit.Actor is null) continue;
            projectile.Position = projectile.Previous.Lerp(projectile.Position, hit.Fraction);
            projectile.Life = 0;
            ApplyDamage(hit.Actor, hit.Block, projectile.Damage, projectile.Velocity.Normalized());
            if (projectile.Splash > 0)
                for (var victimIndex = 0; victimIndex < actors.Count; victimIndex++)
                {
                    var victim = actors[victimIndex];
                    if (victim == hit.Actor || !victim.Alive || victim.Mineral || !relations.Hostile(victim.FactionId, projectile.Faction)) continue;
                    foreach (var block in victim.Ship.Blocks.ToArray())
                    {
                        var pos = victim.Position + (block.Position - victim.Ship.CenterOfMass()).Rotated(victim.Heading);
                        if (pos.DistanceTo(projectile.Position) < projectile.Splash) ApplyDamage(victim, block, projectile.Damage * 0.5f, projectile.Velocity.Normalized());
                    }
                }
        }
        Projectiles.RemoveAll(p => p.Life <= 0);
        ResolveCollisions(actors);
    }

    private static void SpendShot(CombatVessel actor, ModularShip.Block block)
    {
        actor.Energy -= block.Spec.FireEnergy;
        actor.Cooldowns[block.Id] = block.Spec.FireInterval;
    }
    private static CombatVessel? FindHealingTarget(CombatVessel actor, IReadOnlyList<CombatVessel> actors, FactionRelations relations, Vector2 origin, float range)
    {
        CombatVessel? target = null;
        var nearest = range * range;
        for (var i = 0; i < actors.Count; i++)
        {
            var candidate = actors[i];
            if (candidate == actor || !candidate.Alive || candidate.Mineral || !relations.Friendly(actor.FactionId, candidate.FactionId)) continue;
            var distance = candidate.Position.DistanceSquaredTo(origin);
            if (distance >= nearest) continue;
            foreach (var block in candidate.Ship.Blocks)
                if (block.Enabled && block.Health < block.Spec.Hull) { target = candidate; nearest = distance; break; }
        }
        return target;
    }

    public readonly record struct Hit(CombatVessel? Actor, ModularShip.Block? Block, float Fraction);
    public static Hit FindHit(Vector2 start, Vector2 end, CombatVessel owner, IReadOnlyList<CombatVessel> actors, FactionRelations relations)
    {
        var nearest = new Hit(null, null, 1);
        for (var actorIndex = 0; actorIndex < actors.Count; actorIndex++)
        {
            var actor = actors[actorIndex];
            if (actor == owner || !actor.Alive || !actor.Mineral && !relations.Hostile(owner.FactionId, actor.FactionId)) continue;
            if (actor.Shield > 0)
            {
                var t = CircleFraction(start - actor.Position, end - actor.Position, actor.ShieldRadius);
                if (t < nearest.Fraction) nearest = new Hit(actor, null, t);
                continue;
            }
            var center = actor.Ship.CenterOfMass();
            var a = (start - actor.Position).Rotated(-actor.Heading) + center;
            var b = (end - actor.Position).Rotated(-actor.Heading) + center;
            foreach (var block in actor.Ship.Blocks)
            {
                if (!block.Enabled || block.Health <= 0) continue;
                var p = (a - block.Position).Rotated(-block.Angle);
                var q = (b - block.Position).Rotated(-block.Angle);
                var vertices = block.Spec.Vertices();
                var t = Geometry2D.IsPointInPolygon(p, vertices) ? 0 : 2f;
                for (var i = 0; i < vertices.Length; i++)
                    t = Math.Min(t, SegmentFraction(p, q, vertices[i], vertices[(i + 1) % vertices.Length]));
                if (t < nearest.Fraction) nearest = new Hit(actor, block, t);
            }
        }
        return nearest;
    }
    private static float SegmentFraction(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var r = b - a; var s = d - c;
        var cross = r.Cross(s);
        if (Mathf.Abs(cross) < 0.00001f) return 2;
        var t = (c - a).Cross(s) / cross; var u = (c - a).Cross(r) / cross;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : 2;
    }
    private static float CircleFraction(Vector2 a, Vector2 b, float radius)
    {
        if (a.LengthSquared() <= radius * radius) return 0;
        var d = b - a; var aa = d.LengthSquared();
        if (aa < 0.00001f) return 2;
        var bb = 2 * a.Dot(d); var cc = a.LengthSquared() - radius * radius;
        var disc = bb * bb - 4 * aa * cc;
        if (disc < 0) return 2;
        var t = (-bb - Mathf.Sqrt(disc)) / (2 * aa);
        return t >= 0 && t <= 1 ? t : 2;
    }
    public const float SurfaceDamageBase = 15f;
    public const float SurfaceDamagePerSpeed = 0.6f;
    // NPC 巡航速度上限约 150，对向擦撞相对速度可达 300；阈值提到 200 让普通巡航擦撞不致伤，
    // 只有真正高速冲撞（玩家加速、被武器打飞撞墙）才触发伤害。
    public const float CollisionDamageThreshold = 200f;
    public const float CollisionDamagePerSpeed = 0.4f;
    // 一次碰撞伤害后的冷却：持续接触按事件节流（每 0.35 秒至多一次），而非每帧叠加致死。
    public const float CollisionDamageInterval = 0.35f;

    /// <summary>恒星/行星表面碰撞：碰到就有基础伤害，按撞击速度加成；护盾先吸收。</summary>
    public void SurfaceImpact(CombatVessel actor, float impactSpeed, Vector2 normal)
    {
        // normal points from the body toward the ship; the contact face faces the body, i.e. -normal.
        actor.Refresh();
        var damage = SurfaceDamageBase + impactSpeed * SurfaceDamagePerSpeed;
        ApplyDamage(actor, ContactBlock(actor, -normal), damage, -normal);
    }

    /// <summary>舰船间高速碰撞：相对速度超过阈值后，对 SAT 检测到的真实接触方块互伤。
    /// 一次接触事件触发后进入冷却，持续接触期间不再每帧叠加伤害——reassembly 的碰撞以反弹为主，只有剧烈撞击才伤。</summary>
    private void ResolveCollisionDamage(IReadOnlyList<CombatVessel> actors, float dt)
    {
        var count = actors.Count;
        while (_geometryPool.Count < count) _geometryPool.Add(new CollisionGeometry());
        while (_displaced.Count < count) _displaced.Add(false);
        // Refresh the shared per-actor pool once: each actor keeps its own hull arrays, so steady state allocates nothing.
        for (var i = 0; i < count; i++)
        {
            var actor = actors[i];
            // Tick down collision damage cooldown so a fresh contact can hurt again after the interval.
            if (actor.CollisionDamageCooldown > 0) actor.CollisionDamageCooldown = Math.Max(0, actor.CollisionDamageCooldown - dt);
            if (actor.Alive && !actor.Mineral) RefreshGeometry(_geometryPool[i], actor);
            else _geometryPool[i].Count = 0;
            _displaced[i] = false;
        }
        for (var i = 0; i < count; i++)
        {
            var a = actors[i];
            var geometryA = _geometryPool[i];
            if (geometryA.Count == 0) continue;
            for (var j = i + 1; j < count; j++)
            {
                var b = actors[j];
                var geometryB = _geometryPool[j];
                if (geometryB.Count == 0 || a.CollisionStatic && b.CollisionStatic) continue;
                if (a.Position.DistanceTo(b.Position) > geometryA.Radius + geometryB.Radius) continue;
                if (!TryGetContact(geometryA, geometryB, a, b, out var normal, out _, out var blockA, out var blockB)) continue;
                var relative = a.Velocity - b.Velocity;
                var speed = relative.Length();
                if (speed <= CollisionDamageThreshold) continue;
                // One damage pulse per contact event: while either body is still cooling from a prior pulse,
                // keep resolving physics (rebound) but skip stacking another damage tick this frame.
                if (a.CollisionDamageCooldown > 0 || b.CollisionDamageCooldown > 0) continue;
                // Damage the blocks that actually touched, along the contact normal.
                var damage = (speed - CollisionDamageThreshold) * CollisionDamagePerSpeed;
                // Static structures (stations, outposts) are immune to ramming — only weapons can breach them.
                // Ramming kills are mutual collisions, not unprovoked attacks, so they must not trigger a wanted status.
                if (blockA is { } hitA && !a.CollisionStatic)
                {
                    var wasAliveA = a.Alive;
                    ApplyDamage(a, hitA, damage, normal);
                    if (wasAliveA && !a.Alive) a.KilledByCollision = true;
                }
                if (blockB is { } hitB && !b.CollisionStatic)
                {
                    var wasAliveB = b.Alive;
                    ApplyDamage(b, hitB, damage, -normal);
                    if (wasAliveB && !b.Alive) b.KilledByCollision = true;
                }
                a.CollisionDamageCooldown = CollisionDamageInterval;
                b.CollisionDamageCooldown = CollisionDamageInterval;
            }
        }
    }

    private static ModularShip.Block? ContactBlock(CombatVessel actor, Vector2 toward)
    {
        if (actor.Active.Length == 0) return null;
        ModularShip.Block? best = null;
        var bestDot = float.MinValue;
        var center = actor.Ship.CenterOfMass();
        for (var i = 0; i < actor.Active.Length; i++)
        {
            var block = actor.Active[i];
            var local = (block.Position - center).Rotated(actor.Heading);
            var dot = local.Dot(toward);
            if (dot > bestDot) { bestDot = dot; best = block; }
        }
        return best;
    }
    public void ApplyDamage(CombatVessel actor, ModularShip.Block? block, float damage, Vector2 direction)
    {
        if (damage <= 0) return;
        if (actor.Shield > 0) { actor.Shield = Math.Max(0, actor.Shield - damage); return; }
        if (block is null || !actor.Ship.Blocks.Contains(block)) return;
        var center = actor.Ship.CenterOfMass();
        var at = actor.Position + (block.Position - center).Rotated(actor.Heading);
        var fragments = actor.Ship.DamageBlock(block, damage);
        foreach (var fragment in fragments)
            Fragments.Add(new Debris { Ship = fragment, Position = actor.Position + (fragment.CenterOfMass() - center).Rotated(actor.Heading),
                Heading = actor.Heading, Velocity = actor.Velocity + direction * 30 });
        actor.Position += (actor.Ship.CenterOfMass() - center).Rotated(actor.Heading);
        actor.PreviousPosition = actor.Position;
        if (block.Health <= 0)
            Resources.Add(new Resource { Position = at, Amount = actor.Mineral ? 90 : Math.Max(5, (int)(block.Spec.Mass * 4)), DepositId = actor.DepositId });
    }
    public int Collect(Vector2 position, float radius)
    {
        var total = 0;
        for (var i = Resources.Count - 1; i >= 0; i--)
            if (Resources[i].Position.DistanceSquaredTo(position) <= radius * radius)
            { total += Resources[i].Amount; DepositCollected?.Invoke(Resources[i].DepositId); Resources.RemoveAt(i); }
        return total;
    }
}
