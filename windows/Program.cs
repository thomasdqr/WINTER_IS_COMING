// WinterIsComing for Windows — snow on your desktop.
// Snow piles up on top of windows and on the taskbar, and flies off when you drag a window.
// Pure Win32 + a tiny software renderer: no WinForms, no extra packages.
//
// Run:     dotnet run -c Release
// Build:   dotnet publish -c Release -r win-x64   (or win-arm64) --self-contained -p:PublishSingleFile=true
// A ❄ icon appears in the system tray (bottom right): pause / snow amount / clear / quit.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinterIsComing;

// MARK: - Settings (feel free to tweak)

static class Cfg
{
    public static int TargetFlakes = 260;          // number of flakes falling at the same time
    public const float ColW = 3;                   // width of one column of the snow height map
    public const float WindowPileMax = 16;         // max snow height on top of a window
    public const float GroundPileMax = 34;         // max snow height at the bottom of the screen
    public const float TaskbarPileMax = 18;        // max snow height on the taskbar
    public const float TaskbarGain = 2.0f;         // snow on the taskbar builds up this much faster
    public const float Gravity = 900;              // gravity for snow sliding off
    public const float PollInterval = 1f / 30f;    // how often window positions are checked (s)
    public const int MaxClumps = 900;              // max number of falling snow clumps at once
    public const float FallSpeed = 1.5f;           // falling speed multiplier
    public const bool FlakesInFront = true;        // some of the smallest flakes fall in front of windows
    public const float InFrontFraction = 0.4f;     // share of the smallest layer that falls in front
    public const int FrameMs = 16;                 // timer interval (~60 fps)
}

// Three depth layers: far = small, faint, slow; near = big, bright, fast
readonly record struct Layer(float SizeMin, float SizeMax, float SpeedMin, float SpeedMax,
                             float AlphaMin, float AlphaMax, float SwayMin, float SwayMax, float Wind);

static class Rnd
{
    static readonly Random R = new();
    public static float F(float a, float b) => a + (float)R.NextDouble() * (b - a);
}

// MARK: - Flake

sealed class Flake
{
    public float X, Y, Vx, Vy;
    public float Size = 4, Alpha = 1;
    public float SwayAmp, SwayFreq = 1, Phase, WindFactor = 1;
    public int Layer;                // 0 far, 1 middle, 2 near
    public bool Clump;               // sliding off a surface (affected by gravity)
    public bool InFront;             // falls in front of windows
    public long IgnoreId = -1;       // don't land right back on the surface it came from
    public float IgnoreUntil;
}

// MARK: - Snow pile (a height map drawn as a smooth white mound). y grows downwards.

sealed class Pile
{
    public float MinX, TopY;          // TopY = the edge the snow sits on
    public readonly float Width, MaxH;
    public readonly float[] H, Caps;  // current height / max height per column
    public readonly bool[] Occ;       // column hidden behind a window in front
    public bool Dirty;

    public Pile(float minX, float topY, float width, float maxH)
    {
        MinX = minX; TopY = topY; Width = width; MaxH = maxH;
        int n = Math.Max(1, (int)MathF.Ceiling(width / Cfg.ColW));
        H = new float[n]; Caps = new float[n]; Occ = new bool[n];
        for (int i = 0; i < n; i++)
        {
            float edgeDist = MathF.Min(i + 0.5f, n - i - 0.5f) * Cfg.ColW;
            float t = MathF.Min(1, edgeDist / (maxH * 2.2f));
            Caps[i] = maxH * MathF.Sqrt(t);   // taper at both ends so it looks rounded
        }
    }

    public int Count => H.Length;
    public float Total { get { float s = 0; foreach (var h in H) s += h; return s; } }
    public int Col(float x) { int c = (int)MathF.Floor((x - MinX) / Cfg.ColW); return c >= 0 && c < Count ? c : -1; }
    public float ColX(int c) => MinX + (c + 0.5f) * Cfg.ColW;
    public float Surface(int c) => TopY - H[c];

    static readonly (int, float)[] Spread = { (0, 1), (-1, .75f), (1, .75f), (-2, .45f), (2, .45f), (-3, .2f), (3, .2f) };

    public void Add(int c, float amount)
    {
        foreach (var (o, w) in Spread)
        {
            int i = c + o;
            if (i < 0 || i >= Count || Occ[i]) continue;
            H[i] = MathF.Min(Caps[i], H[i] + amount * w);
        }
        Dirty = true;
    }

    // Let snow slump sideways where the slope is too steep
    public void Relax()
    {
        if (!Dirty) return;
        bool moved = false;
        for (int i = 0; i < Count - 1; i++)
        {
            if (Occ[i] || Occ[i + 1]) continue;
            float d = H[i] - H[i + 1];
            if (MathF.Abs(d) > 1f)
            {
                float m = (MathF.Abs(d) - 1f) * 0.3f * MathF.Sign(d);
                H[i] = Math.Clamp(H[i] - m, 0, Caps[i]);
                H[i + 1] = Math.Clamp(H[i + 1] + m, 0, Caps[i + 1]);
                if (MathF.Abs(m) > 0.02f) moved = true;
            }
        }
        Dirty = moved;
    }

    public void Clear() { Array.Clear(H); Dirty = true; }
}

record struct Win(long Id, float L, float T, float R, float B)
{
    public float Width => R - L;
    public bool Contains(float x, float y) => x >= L && x < R && y >= T && y < B;
}

// MARK: - The snow world (all coordinates in logical pixels, y down)

sealed class SnowWorld
{
    static readonly Layer[] Layers =
    {
        new(1.8f, 3.0f, 22, 38,  0.30f, 0.50f, 5, 10,  0.5f),
        new(3.0f, 4.8f, 40, 65,  0.60f, 0.80f, 9, 16,  0.8f),
        new(4.8f, 7.5f, 70, 105, 0.85f, 1.00f, 14, 24, 1.1f),
    };

    public float W, H;
    public readonly List<Flake> Flakes = new();
    readonly Stack<Flake> pool = new();
    public readonly Dictionary<long, Pile> Piles = new();
    readonly Dictionary<long, Win> lastRect = new();
    public List<Win> Order = new();              // windows from front to back
    public Pile Ground;
    public bool GroundIsTaskbar;
    float pollAcc, t;
    const long GroundId = -2;

    public SnowWorld(float w, float h)
    {
        W = w; H = h;
        ResetGround(h, false);
        for (int i = 0; i < Cfg.TargetFlakes; i++) SpawnFlake(Rnd.F(0, H));
    }

    public void Resize(float w, float h)
    {
        W = w; H = h;
        ResetGround(h, false);
    }

    void ResetGround(float top, bool taskbar)
    {
        if (Ground != null) Release(Ground, 0, 0, GroundId);
        GroundIsTaskbar = taskbar;
        Ground = new Pile(0, top, W, taskbar ? Cfg.TaskbarPileMax : Cfg.GroundPileMax);
    }

    public void ClearAll()
    {
        foreach (var p in Piles.Values) p.Clear();
        Ground.Clear();
    }

    // MARK: Flake pool

    Flake Obtain()
    {
        var f = pool.Count > 0 ? pool.Pop() : new Flake();
        Flakes.Add(f);
        return f;
    }

    void Recycle(int i)
    {
        pool.Push(Flakes[i]);
        Flakes[i] = Flakes[^1];
        Flakes.RemoveAt(Flakes.Count - 1);
    }

    void SpawnFlake(float? y = null)
    {
        var f = Obtain();
        float r = Rnd.F(0, 1);
        int li = r < 0.40f ? 0 : (r < 0.68f ? 1 : 2);   // 40% far, 28% middle, 32% near
        var L = Layers[li];
        f.Layer = li;
        f.Clump = false;
        f.InFront = Cfg.FlakesInFront && li == 0 && Rnd.F(0, 1) < Cfg.InFrontFraction;
        f.IgnoreId = -1;
        f.Size = Rnd.F(L.SizeMin, L.SizeMax);
        f.Vx = 0;
        f.Vy = Rnd.F(L.SpeedMin, L.SpeedMax) * Cfg.FallSpeed;
        f.SwayAmp = Rnd.F(L.SwayMin, L.SwayMax);
        f.SwayFreq = Rnd.F(0.6f, 1.6f);
        f.Phase = Rnd.F(0, 6.28f);
        f.WindFactor = L.Wind;
        f.Alpha = Rnd.F(L.AlphaMin, L.AlphaMax);
        f.X = Rnd.F(-40, W + 40);
        f.Y = y ?? -Rnd.F(2, 30);
    }

    void SpawnClump(float x, float y, float vx, float vy, long sourceId)
    {
        var f = Obtain();
        f.Clump = true;
        f.InFront = false;
        f.Layer = 2;
        f.Size = Rnd.F(2.5f, 5);
        f.Alpha = 0.95f;
        f.Vx = vx; f.Vy = vy;
        f.IgnoreId = sourceId;
        f.IgnoreUntil = t + 0.35f;
        f.X = x; f.Y = y;
    }

    // Turn a pile into falling clumps; (vx, vy) is the window's velocity, so faster drags fling snow farther
    void Release(Pile p, float vx, float vy, long id)
    {
        float total = p.Total;
        if (total > 0.5f)
        {
            float budget = Math.Max(0, Cfg.MaxClumps - Flakes.Count + Cfg.TargetFlakes);
            float k = MathF.Min(1, budget / MathF.Max(1, total / 3.5f));
            float fx = Math.Clamp(vx * 0.5f, -900, 900);
            float fy = Math.Clamp(vy * 0.4f, -500, 300);
            for (int c = 0; c < p.Count; c++)
            {
                if (p.H[c] <= 0.6f) continue;
                float amount = p.H[c] / 3.5f * k;
                while (amount > 0)
                {
                    if (amount < 1 && Rnd.F(0, 1) > amount) break;
                    SpawnClump(p.ColX(c) + Rnd.F(-1.5f, 1.5f), p.TopY - Rnd.F(0, p.H[c]),
                               fx + Rnd.F(-40, 40), fy - Rnd.F(10, 80), id);
                    amount -= 1;
                }
            }
        }
        p.Clear();
    }

    // MARK: Per-frame update

    public void Update(float dt, Func<List<Win>> readWindows, Func<float?> readTaskbarTop)
    {
        t += dt;
        pollAcc += dt;
        if (pollAcc >= Cfg.PollInterval)
        {
            PollWindows(pollAcc, readWindows(), readTaskbarTop());
            pollAcc = 0;
        }
        StepFlakes(dt);
    }

    void PollWindows(float dt, List<Win> wins, float? taskbarTop)
    {
        // Taskbar at the bottom: the snow on the "ground" sits on top of it
        float groundTop = taskbarTop ?? H;
        bool taskbar = taskbarTop.HasValue;
        if (MathF.Abs(groundTop - Ground.TopY) > 1 || taskbar != GroundIsTaskbar) ResetGround(groundTop, taskbar);

        // Closed / minimized windows: their snow just falls
        var alive = new HashSet<long>();
        foreach (var w in wins) alive.Add(w.Id);
        var gone = new List<long>();
        foreach (var kv in Piles) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            Release(Piles[id], 0, 0, id);
            Piles.Remove(id);
            lastRect.Remove(id);
        }

        foreach (var r in wins)
        {
            if (Piles.TryGetValue(r.Id, out var pile) && lastRect.TryGetValue(r.Id, out var old))
            {
                if (MathF.Abs(old.Width - r.Width) > 0.5f)
                {
                    // Width changed: drop the snow and start a new pile
                    Release(pile, 0, 0, r.Id);
                    Piles[r.Id] = new Pile(r.L, r.T, r.Width, Cfg.WindowPileMax);
                }
                else if (MathF.Abs(old.L - r.L) > 0.3f || MathF.Abs(old.T - r.T) > 0.3f)
                {
                    // Window dragged: fling the snow off
                    float vx = (r.L - old.L) / dt, vy = (r.T - old.T) / dt;
                    pile.MinX = r.L;
                    pile.TopY = r.T;
                    Release(pile, vx, vy, r.Id);
                }
            }
            else
            {
                Piles[r.Id] = new Pile(r.L, r.T, r.Width, Cfg.WindowPileMax);
            }
            lastRect[r.Id] = r;
        }
        Order = wins;

        ComputeOcclusion();
        foreach (var p in Piles.Values) p.Relax();
        Ground.Relax();
    }

    // Parts of a window top hidden behind a front window get no snow and aren't drawn
    void ComputeOcclusion()
    {
        for (int i = 0; i < Order.Count; i++)
        {
            if (!Piles.TryGetValue(Order[i].Id, out var p)) continue;
            for (int c = 0; c < p.Count; c++)
            {
                float x = p.ColX(c), y = p.TopY - 1;
                bool o = x < 0 || x > W || y < 0;
                if (!o) for (int j = 0; j < i; j++) if (Order[j].Contains(x, y)) { o = true; break; }
                if (o != p.Occ[c]) { p.Occ[c] = o; if (o) p.H[c] = 0; p.Dirty = true; }
            }
        }
        // The taskbar is always in front of windows; the plain screen bottom is not
        for (int c = 0; c < Ground.Count; c++)
        {
            bool o = false;
            if (!GroundIsTaskbar)
            {
                float x = Ground.ColX(c), y = Ground.TopY - 1;
                foreach (var w in Order) if (w.Contains(x, y)) { o = true; break; }
            }
            if (o != Ground.Occ[c]) { Ground.Occ[c] = o; if (o) Ground.H[c] = 0; Ground.Dirty = true; }
        }
    }

    void StepFlakes(float dt)
    {
        // A few sines at different frequencies add up to gusty wind
        float wind = 14 * MathF.Sin(t * 0.11f) + 9 * MathF.Sin(t * 0.37f + 1.3f) + 6 * MathF.Sin(t * 0.9f + 0.4f);
        int normal = 0;
        for (int i = Flakes.Count - 1; i >= 0; i--)
        {
            var f = Flakes[i];
            float prevY = f.Y;
            if (f.Clump)
            {
                f.Vy += Cfg.Gravity * dt;
                f.Vx *= MathF.Max(0, 1 - 1.2f * dt);
                f.X += f.Vx * dt;
                f.Y += f.Vy * dt;
            }
            else
            {
                float sway = MathF.Sin(t * f.SwayFreq + f.Phase) * f.SwayAmp;
                f.X += (wind * f.WindFactor + sway) * dt;
                f.Y += f.Vy * dt;
                if (f.X < -40) f.X += W + 80; else if (f.X > W + 40) f.X -= W + 80;
            }

            bool gone = false;
            if (f.Y > prevY)
            {
                // Window tops, front to back (flakes falling in front of windows skip them)
                if (!f.InFront)
                {
                    foreach (var w in Order)
                    {
                        if (!Piles.TryGetValue(w.Id, out var pile)) continue;
                        if (f.Clump && w.Id == f.IgnoreId && t < f.IgnoreUntil) continue;
                        int c = pile.Col(f.X);
                        if (c < 0 || pile.Occ[c]) continue;
                        float s = pile.Surface(c);
                        if (prevY <= s && f.Y > s)
                        {
                            pile.Add(c, f.Clump ? 1.6f : f.Size * 0.16f);
                            gone = true;
                            break;
                        }
                    }
                }
                // Then the taskbar / bottom of the screen
                if (!gone)
                {
                    int c = Ground.Col(f.X);
                    if (c >= 0 && f.Y > Ground.Surface(c))
                    {
                        if (!Ground.Occ[c])
                        {
                            float gain = GroundIsTaskbar ? Cfg.TaskbarGain : 1;
                            Ground.Add(c, (f.Clump ? 1.6f : f.Size * 0.12f) * gain);
                        }
                        gone = true;
                    }
                }
            }
            if (!gone)
            {
                // Flakes that fall "behind" a window disappear
                if (!f.InFront) foreach (var w in Order) if (w.Contains(f.X, f.Y)) { gone = true; break; }
                if (f.Y > H + 20) gone = true;
                if (f.Clump && (f.X < -80 || f.X > W + 80)) gone = true;
            }

            if (gone) Recycle(i);
            else if (!f.Clump) normal++;
        }

        // Top up flakes (max 6 per frame so they don't appear in a row)
        int deficit = Math.Min(6, Cfg.TargetFlakes - normal);
        for (int k = 0; k < deficit; k++) SpawnFlake();
    }
}

// MARK: - Software renderer into a premultiplied BGRA buffer

unsafe static class Render
{
    static void Blend(uint* p, float a, float r, float g, float b)
    {
        if (a <= 0.003f) return;
        if (a > 1) a = 1;
        uint d = *p;
        float inv = 1 - a;
        uint nb = (uint)MathF.Min(255, b * a * 255 + (d & 0xFF) * inv + 0.5f);
        uint ng = (uint)MathF.Min(255, g * a * 255 + ((d >> 8) & 0xFF) * inv + 0.5f);
        uint nr = (uint)MathF.Min(255, r * a * 255 + ((d >> 16) & 0xFF) * inv + 0.5f);
        uint na = (uint)MathF.Min(255, a * 255 + (d >> 24) * inv + 0.5f);
        *p = (na << 24) | (nr << 16) | (ng << 8) | nb;
    }

    // Soft round flake: solid center, fading edge
    public static void Disc(uint* buf, int bw, int bh, float cx, float cy, float radius, float alpha)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(cx - radius)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - radius)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + radius));
        float inv = 1 / radius;
        for (int y = y0; y <= y1; y++)
        {
            float dy = (y + 0.5f - cy) * inv;
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * inv;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= 1) continue;
                float a = d < 0.45f ? 1 - 0.4f * (d / 0.45f) : 0.6f * (1 - (d - 0.45f) / 0.55f);
                Blend(row + x, a * alpha, 1, 1, 1);
            }
        }
    }

    // A pile: white mound above its edge with a thin blue-grey shadow just below it
    public static void Pile(uint* buf, int bw, int bh, Pile p, float s)
    {
        int px0 = Math.Max(0, (int)MathF.Floor(p.MinX * s)), px1 = Math.Min(bw - 1, (int)MathF.Ceiling((p.MinX + p.Width) * s));
        float baseY = p.TopY * s;
        for (int px = px0; px <= px1; px++)
        {
            // Interpolate the height between column centres for a smooth outline
            float u = ((px + 0.5f) / s - p.MinX) / Cfg.ColW - 0.5f;
            int c0 = (int)MathF.Floor(u);
            float fr = u - c0;
            int ca = Math.Clamp(c0, 0, p.Count - 1), cb = Math.Clamp(c0 + 1, 0, p.Count - 1);
            if (p.Occ[fr < 0.5f ? ca : cb]) continue;
            float h = (p.H[ca] * (1 - fr) + p.H[cb] * fr) * s;
            if (h < 0.3f * s) continue;

            float top = baseY - h;
            // shadow: 1.5 logical px below the edge
            float shEnd = baseY + 1.5f * s;
            for (int y = Math.Max(0, (int)MathF.Floor(baseY)); y < Math.Min(bh, (int)MathF.Ceiling(shEnd)); y++)
            {
                float cov = MathF.Min(1, shEnd - y);
                Blend(buf + (long)y * bw + px, 0.9f * cov, 0.70f, 0.78f, 0.92f);
            }
            // body
            int yTop = (int)MathF.Floor(top);
            for (int y = Math.Max(0, yTop); y < Math.Min(bh, (int)MathF.Ceiling(baseY)); y++)
            {
                float cov = y == yTop ? 1 - (top - yTop) : 1;   // anti-aliased top edge
                Blend(buf + (long)y * bw + px, 0.97f * cov, 1, 1, 1);
            }
        }
    }

    public static void World(uint* buf, int bw, int bh, SnowWorld w, float s)
    {
        new Span<uint>(buf, bw * bh).Clear();
        // far + middle flakes, then piles, then near flakes and clumps on top
        foreach (var f in w.Flakes) if (!f.Clump && f.Layer < 2) Disc(buf, bw, bh, f.X * s, f.Y * s, f.Size * 1.1f * s, f.Alpha);
        foreach (var p in w.Piles.Values) Pile(buf, bw, bh, p, s);
        Pile(buf, bw, bh, w.Ground, s);
        foreach (var f in w.Flakes) if (f.Clump || f.Layer == 2) Disc(buf, bw, bh, f.X * s, f.Y * s, f.Size * 1.1f * s, f.Alpha);
    }

    // 32×32 tray icon: white six-armed snowflake on a blue circle (straight alpha, as icons expect)
    public static void TrayIcon(uint* buf, int n)
    {
        float c = n / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float px = x + 0.5f - c, py = y + 0.5f - c;
                float r = MathF.Sqrt(px * px + py * py);
                float bgA = Math.Clamp(c - 0.5f - r, 0, 1);
                float dist = 99;
                for (int k = 0; k < 6; k++)
                {
                    float ang = k * MathF.PI / 3, ex = MathF.Cos(ang), ey = MathF.Sin(ang);
                    float tt = Math.Clamp(px * ex + py * ey, 0, c * 0.72f);
                    float qx = px - ex * tt, qy = py - ey * tt;
                    dist = MathF.Min(dist, MathF.Sqrt(qx * qx + qy * qy));
                }
                float flake = Math.Clamp(1.7f - dist, 0, 1) * bgA;
                float rr = 0.25f + 0.75f * flake, gg = 0.47f + 0.53f * flake, bb = 0.85f + 0.15f * flake;
                uint a = (uint)(bgA * 255);
                buf[y * n + x] = (a << 24) | ((uint)(rr * 255) << 16) | ((uint)(gg * 255) << 8) | (uint)(bb * 255);
            }
    }
}

// MARK: - Win32 plumbing

static unsafe class Program
{
    // --- constants
    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
               WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_EXSTYLE = -20;
    const uint WM_DESTROY = 0x2, WM_TIMER = 0x113, WM_DISPLAYCHANGE = 0x7E, WM_NULL = 0,
               WM_APP = 0x8000, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
    const uint WM_TRAY = WM_APP + 1;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint ULW_ALPHA = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const int CMD_PAUSE = 1, CMD_CLEAR = 2, CMD_QUIT = 3, CMD_LIGHT = 10, CMD_MEDIUM = 11, CMD_HEAVY = 12;

    // --- structs
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    delegate IntPtr WndProcFn(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // --- imports
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                         IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                           ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    // --- state
    static WndProcFn wndProc;                 // kept alive so the GC doesn't collect the callback
    static EnumWindowsProc enumProc;
    static IntPtr overlay, tray, screenDC, memDC, dib, bits, trayIcon;
    static int bw, bh;                        // buffer size in physical pixels
    static float scale = 1;                   // physical pixels per logical pixel (DPI / 96)
    static SnowWorld world;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double lastT;
    static bool paused;
    static readonly List<Win> found = new();
    static readonly StringBuilder clsName = new(64);

    [STAThread]
    static void Main()
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4));   // per-monitor DPI aware v2: real pixel coordinates
        IntPtr inst = GetModuleHandleW(null);

        wndProc = WndProc;
        enumProc = EnumProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = inst,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = "WinterIsComingWnd",
        };
        RegisterClassExW(ref wc);

        bw = GetSystemMetrics(0);   // SM_CXSCREEN (primary monitor)
        bh = GetSystemMetrics(1);   // SM_CYSCREEN
        // Transparent, click-through, always on top, never takes focus, not in Alt+Tab
        overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                                  "WinterIsComingWnd", "WinterIsComing", WS_POPUP, 0, 0, bw, bh,
                                  IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        // A hidden window that owns the tray icon and its menu
        tray = CreateWindowExW(WS_EX_TOOLWINDOW, "WinterIsComingWnd", "WinterIsComing tray", WS_POPUP, 0, 0, 0, 0,
                               IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        uint dpi = GetDpiForWindow(overlay);
        scale = dpi > 0 ? dpi / 96f : 1;
        screenDC = GetDC(IntPtr.Zero);
        CreateSurface();
        world = new SnowWorld(bw / scale, bh / scale);

        ShowWindow(overlay, SW_SHOWNOACTIVATE);
        AddTrayIcon();
        SetTimer(overlay, (UIntPtr)1, Cfg.FrameMs, IntPtr.Zero);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    static void CreateSurface()
    {
        if (memDC == IntPtr.Zero) memDC = CreateCompatibleDC(IntPtr.Zero);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = bw, biHeight = -bh,   // negative = top-down rows
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            }
        };
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memDC, dib);
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                if (hWnd == overlay) Tick();
                return IntPtr.Zero;
            case WM_DISPLAYCHANGE:
                if (hWnd == overlay)
                {
                    bw = GetSystemMetrics(0);
                    bh = GetSystemMetrics(1);
                    CreateSurface();
                    world.Resize(bw / scale, bh / scale);
                }
                return IntPtr.Zero;
            case WM_TRAY:
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP) ShowMenu();
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - lastT, 1.0 / 20);
        lastT = now;
        if (paused) return;

        world.Update(dt, ReadWindows, ReadTaskbarTop);
        Render.World((uint*)bits, bw, bh, world, scale);

        var dst = new POINT();
        var src = new POINT();
        var size = new SIZE { cx = bw, cy = bh };
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };   // AC_SRC_ALPHA
        UpdateLayeredWindow(overlay, screenDC, ref dst, ref size, memDC, ref src, 0, ref blend, ULW_ALPHA);
    }

    // MARK: Windows on screen

    static List<Win> ReadWindows()
    {
        found.Clear();
        EnumWindows(enumProc, IntPtr.Zero);       // top-level windows, front to back
        return new List<Win>(found);
    }

    static bool EnumProc(IntPtr h, IntPtr _)
    {
        if (h == overlay || h == tray) return true;
        if (!IsWindowVisible(h) || IsIconic(h)) return true;
        long ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return true;                       // tool windows, popups
        if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true;  // other overlays
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true; // other desktops, hidden UWP
        if (GetWindowTextLengthW(h) == 0) return true;
        clsName.Clear();
        GetClassNameW(h, clsName, clsName.Capacity);
        string cls = clsName.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;

        // The visible frame (GetWindowRect includes invisible resize borders on Windows 10/11)
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rc, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(h, out rc);
        float l = rc.Left / scale, t = rc.Top / scale, r = rc.Right / scale, b = rc.Bottom / scale;
        if (r - l < 80 || b - t < 40) return true;
        found.Add(new Win(h.ToInt64(), l, t, r, b));
        return true;
    }

    // Top edge of the taskbar when it sits at the bottom and is visible; otherwise null
    static float? ReadTaskbarTop()
    {
        IntPtr tb = FindWindowW("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !IsWindowVisible(tb) || !GetWindowRect(tb, out RECT rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= h || rc.Top < bh / 2 || rc.Top >= bh - 4) return null;   // not at the bottom, or auto-hidden
        return rc.Top / scale;
    }

    // MARK: Tray icon and menu

    static void AddTrayIcon()
    {
        const int n = 32;
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = n, biHeight = -n, biPlanes = 1, biBitCount = 32,
            }
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr px, IntPtr.Zero, 0);
        Render.TrayIcon((uint*)px, n);
        IntPtr mask = CreateBitmap(n, n, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        trayIcon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);

        var nid = TrayData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    static NOTIFYICONDATAW TrayData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = tray, uID = 1, uCallbackMessage = WM_TRAY, hIcon = trayIcon,
        szTip = "WinterIsComing ❄", szInfo = "", szInfoTitle = "",
    };

    static void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu(), amount = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_PAUSE, paused ? "Resume snow" : "Pause snow");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 120 ? MF_CHECKED : 0), (UIntPtr)CMD_LIGHT, "Light");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 260 ? MF_CHECKED : 0), (UIntPtr)CMD_MEDIUM, "Medium");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 500 ? MF_CHECKED : 0), (UIntPtr)CMD_HEAVY, "Heavy");
        AppendMenuW(menu, MF_POPUP, (UIntPtr)(ulong)amount.ToInt64(), "Snow amount");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_CLEAR, "Clear snow");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_QUIT, "Quit");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(tray);   // so the menu closes when you click elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, tray, IntPtr.Zero);
        PostMessageW(tray, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);   // also destroys the submenu

        switch (cmd)
        {
            case CMD_PAUSE:
                paused = !paused;
                ShowWindow(overlay, paused ? SW_HIDE : SW_SHOWNOACTIVATE);
                break;
            case CMD_LIGHT: Cfg.TargetFlakes = 120; break;
            case CMD_MEDIUM: Cfg.TargetFlakes = 260; break;
            case CMD_HEAVY: Cfg.TargetFlakes = 500; break;
            case CMD_CLEAR: world.ClearAll(); break;
            case CMD_QUIT: Quit(); break;
        }
    }

    static void Quit()
    {
        KillTimer(overlay, (UIntPtr)1);
        var nid = TrayData();
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        DestroyWindow(overlay);
        DestroyWindow(tray);
        PostQuitMessage(0);
    }
}
