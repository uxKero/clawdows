// Rebanada 6 (v0.2): Clawds animados por contexto (GIFs embebidos).
// Cada GIF se decodifica al arrancar el tray: frames -> HICONs chicos (40px,
// nearest-neighbor para respetar el pixel art). El popup/panel repintan cada
// 80ms, asi que animar es solo elegir el frame segun el tick.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static unsafe partial class Program
{
    static nint[] _animPerm = Array.Empty<nint>();   // permiso: martillazo al boton
    static nint[] _animPlan = Array.Empty<nint>();   // plan: foquito
    static nint[] _animQuestion = Array.Empty<nint>(); // pregunta: nube/globo
    static nint[] _animWork = Array.Empty<nint>();   // header: usando herramientas
    static nint[] _animThink = Array.Empty<nint>();  // header: pensando
    static nint[] _animWait = Array.Empty<nint>();   // header: esperando permiso
    static nint[] _animIdle = Array.Empty<nint>();   // header: en reposo

    static Guid _frameDimTime = new("6aedbd6d-3fb5-418a-83a6-7f45229dc872");

    static void LoadAnims()
    {
        _animPerm = LoadGifFrames("clawdepush", 40);
        _animPlan = FirstNonEmpty(LoadGifFrames("clawdeidea", 40), LoadGifFrames("clawdetime", 40));
        _animQuestion = FirstNonEmpty(LoadGifFrames("clawdebubble", 40), LoadGifFrames("clawdethinking", 40));
        _animWork = LoadGifFrames("clawdework", 32);
        _animThink = LoadGifFrames("clawdethinking", 32);
        _animWait = LoadGifFrames("clawdetime", 32);
        _animIdle = LoadGifFrames("clawdemusic", 32);
    }

    static nint[] FirstNonEmpty(nint[] a, nint[] b) => a.Length > 0 ? a : b;

    // Frame actual segun el tick (~160ms por frame); fallback: Clawd quieto.
    static nint AnimFrame(nint[] frames) =>
        frames.Length == 0
            ? (_frames.Length > 0 ? _frames[0] : 0)
            : frames[(_tickN / 2) % frames.Length];

    // Icono del header del panel segun el estado global.
    static nint HeaderIcon() => AnimFrame(_state.Status switch
    {
        "awaiting" => _animWait,
        "thinking" => _animThink,
        "tool" => _animWork,
        _ => _animIdle,
    });

    static nint[] LoadGifFrames(string baseName, int px)
    {
        var icons = new List<nint>();
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string? res = null;
            foreach (var n in asm.GetManifestResourceNames())
                if (n.Contains(baseName, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
            if (res == null) return icons.ToArray();
            using var st = asm.GetManifestResourceStream(res)!;
            var bytes = new byte[st.Length];
            st.ReadExactly(bytes, 0, bytes.Length);
            fixed (byte* p = bytes)
            {
                nint stream = SHCreateMemStream(p, (uint)bytes.Length);
                if (stream == 0) return icons.ToArray();
                try
                {
                    if (GdipCreateBitmapFromStream(stream, out nint img) != 0 || img == 0) return icons.ToArray();
                    try
                    {
                        GdipImageGetFrameCount(img, ref _frameDimTime, out uint count);
                        if (count == 0) count = 1;
                        // Cap de frames para no gastar memoria con GIFs largos.
                        uint step = count > 30 ? (count + 29) / 30 : 1;

                        // Pasada 1: bounding box del personaje (union de todos los frames)
                        // — los GIFs traen mucho margen transparente y el Clawd quedaria chico.
                        GdipGetImageWidth(img, out uint iw);
                        GdipGetImageHeight(img, out uint ih);
                        int bx0 = (int)iw, by0 = (int)ih, bx1 = 0, by1 = 0;
                        for (uint i = 0; i < count; i += step)
                        {
                            GdipImageSelectActiveFrame(img, ref _frameDimTime, i);
                            UnionAlphaBounds(img, (int)iw, (int)ih, ref bx0, ref by0, ref bx1, ref by1);
                        }
                        if (bx1 <= bx0 || by1 <= by0) { bx0 = 0; by0 = 0; bx1 = (int)iw; by1 = (int)ih; }
                        // Margen del 4% y cuadrado (destino cuadrado, sin distorsion)
                        int m = (int)(Math.Max(iw, ih) * 0.04);
                        bx0 = Math.Max(0, bx0 - m); by0 = Math.Max(0, by0 - m);
                        bx1 = Math.Min((int)iw, bx1 + m); by1 = Math.Min((int)ih, by1 + m);
                        int bw = bx1 - bx0, bh = by1 - by0, side = Math.Max(bw, bh);
                        int sx = Math.Max(0, bx0 - (side - bw) / 2), sy = Math.Max(0, by0 - (side - bh) / 2);
                        if (sx + side > iw) sx = (int)iw - side; if (sx < 0) { sx = 0; side = (int)iw; }
                        if (sy + side > ih) sy = Math.Max(0, (int)ih - side);

                        // Pasada 2: render de cada frame recortado -> HICON chico
                        for (uint i = 0; i < count; i += step)
                        {
                            GdipImageSelectActiveFrame(img, ref _frameDimTime, i);
                            if (GdipCreateBitmapFromScan0(px, px, 0, 0x26200A /*32bppARGB*/, 0, out nint small) != 0) continue;
                            try
                            {
                                if (GdipGetImageGraphicsContext(small, out nint g) == 0)
                                {
                                    GdipSetInterpolationMode(g, 5); // NearestNeighbor (pixel art nitido)
                                    GdipSetPixelOffsetMode(g, 2);   // Half
                                    GdipDrawImageRectRectI(g, img, 0, 0, px, px, sx, sy, side, side, 2 /*UnitPixel*/, 0, 0, 0);
                                    GdipDeleteGraphics(g);
                                }
                                if (GdipCreateHICONFromBitmap(small, out nint hicon) == 0 && hicon != 0) icons.Add(hicon);
                            }
                            finally { GdipDisposeImage(small); }
                        }
                    }
                    finally { GdipDisposeImage(img); }
                }
                finally { Marshal.Release(stream); }
            }
        }
        catch { }
        return icons.ToArray();
    }

    // Escanea el canal alfa del frame activo y expande el bbox (LockBits, rapido).
    static void UnionAlphaBounds(nint img, int iw, int ih, ref int x0, ref int y0, ref int x1, ref int y1)
    {
        var rect = new GpRect { X = 0, Y = 0, Width = iw, Height = ih };
        var data = new BitmapData();
        if (GdipBitmapLockBits(img, ref rect, 1 /*read*/, 0x26200A, ref data) != 0) return;
        try
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < ih; y += 2)
            {
                byte* row = scan + y * data.Stride;
                for (int x = 0; x < iw; x += 2)
                {
                    if (row[x * 4 + 3] > 16) // alfa
                    {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
                }
            }
        }
        finally { GdipBitmapUnlockBits(img, ref data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpRect { public int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapData { public int Width, Height, Stride, PixelFormat; public nint Scan0; public nint Reserved; }

    [DllImport("gdiplus")] static extern int GdipBitmapLockBits(nint bmp, ref GpRect rect, uint flags, int format, ref BitmapData data);
    [DllImport("gdiplus")] static extern int GdipBitmapUnlockBits(nint bmp, ref BitmapData data);
    [DllImport("gdiplus")] static extern int GdipDrawImageRectRectI(nint g, nint image, int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh, int unit, nint attrs, nint cb, nint cbData);
    [DllImport("gdiplus")] static extern int GdipGetImageWidth(nint image, out uint w);
    [DllImport("gdiplus")] static extern int GdipGetImageHeight(nint image, out uint h);
    [DllImport("gdiplus")] static extern int GdipImageGetFrameCount(nint image, ref Guid dim, out uint count);
    [DllImport("gdiplus")] static extern int GdipImageSelectActiveFrame(nint image, ref Guid dim, uint frame);
    [DllImport("gdiplus")] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, nint scan0, out nint bmp);
    [DllImport("gdiplus")] static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [DllImport("gdiplus")] static extern int GdipDrawImageRectI(nint g, nint image, int x, int y, int w, int h);
    [DllImport("gdiplus")] static extern int GdipSetInterpolationMode(nint g, int mode);
    [DllImport("gdiplus")] static extern int GdipSetPixelOffsetMode(nint g, int mode);
}
