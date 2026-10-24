using Godot;
using System.Collections.Generic;
using System.Linq;

public sealed partial class ModularShip
{
    /// <summary>Destroy the actual block, then split every component separated from a live command core.</summary>
    public List<ModularShip> DamageBlock(Block block, float damage)
    {
        var debris = new List<ModularShip>();
        if (damage <= 0 || !_blocks.Contains(block) || !block.Enabled) return debris;
        block.Health = Mathf.Max(0, block.Health - damage);
        if (block.Health > 0) return debris;
        _blocks.Remove(block);
        block.GeometryChanged = null;
        foreach (var component in GetConnectedComponents())
        {
            if (component.Any(b => b.Spec.IsCore && b.Health > 0)) continue;
            var fragment = new ModularShip();
            foreach (var detached in component)
            {
                var copy = fragment.AddBlock(detached.ShapeId, detached.Position, detached.Angle);
                copy.Health = detached.Health;
                detached.GeometryChanged = null;
                _blocks.Remove(detached);
            }
            debris.Add(fragment);
        }
        InvalidateGeometry();
        RefreshConnections();
        Changed?.Invoke();
        return debris;
    }
}
