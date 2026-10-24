using Godot;
using System;

/// <summary>Godot retains these draw commands between frames; camera movement only updates the transform.</summary>
public partial class RetainedBodyVisual : Node2D
{
    public Action<Node2D>? Paint;
    public int DrawCount { get; private set; }
    public override void _Draw() { DrawCount++; Paint?.Invoke(this); }
}
