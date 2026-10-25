using Godot;

// 读取截图并输出像素统计，验证背景渲染结果。
public partial class ShotStats : Node
{
    public override void _Ready()
    {
        foreach (var path in new[] { "D:/MyProject/hackson/ReWorld/.godot/bg-far.png", "D:/MyProject/hackson/ReWorld/.godot/bg-near.png" })
        {
            var img = Image.LoadFromFile(path);
            if (img is null) { GD.Print($"SHOTSTATS {path} LOAD_FAIL"); continue; }
            img.Convert(Image.Format.Rgb8);
            int w = img.GetWidth(), h = img.GetHeight();
            long r = 0, g = 0, b = 0; int n = 0, dark = 0, colored = 0, mx = 0, bright = 0;
            for (int y = 0; y < h; y += 8)
            for (int x = 0; x < w; x += 8)
            {
                var c = img.GetPixel(x, y);
                int ri = (int)(c.R * 255), gi = (int)(c.G * 255), bi = (int)(c.B * 255);
                r += ri; g += gi; b += bi; n++;
                int hi = Mathf.Max(ri, Mathf.Max(gi, bi));
                int lo = Mathf.Min(ri, Mathf.Min(gi, bi));
                if (hi > mx) mx = hi;
                if (hi > 150) bright++;
                if (hi < 12) dark++;
                if (hi - lo > 15 && hi > 25) colored++;
            }
            GD.Print($"SHOTSTATS {path.GetFile()} {w}x{h} avgRGB=({r / n},{g / n},{b / n}) max={mx} black={100.0 * dark / n:0.0}% colored={100.0 * colored / n:0.0}% brightPx(>150)={bright}");
        }
        GetTree().Quit();
    }
}
