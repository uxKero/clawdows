// Panel oscuro estilo Claude Code (dibujado a mano con GDI).
// Reemplaza al menu nativo en el clic IZQUIERDO (el derecho deja el menu nativo
// como respaldo). Header con Clawd + estado + uso, idioma (cicla), toggles, salir.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe partial class Program
{
    // --- Metricas ---
    const int PANEL_W = 270;
    const int PAD = 14;
    const int HEADER_H = 80;
    const int ROW_H = 34;
    const int SEP_H = 11;
    const int ICON_SZ = 24;
    const int DESC_H = 92;
    const string VERSION = "v0.1";
    const string CREDIT = "By @uxKero";
    const string CREDIT_URL = "https://x.com/uxKero";

    // --- Colores (COLORREF = 0x00BBGGRR) ---
    const uint C_BG = 0x001E1E1E;
    const uint C_HOVER = 0x00322B27;
    const uint C_TEXT = 0x00E6E6E6;
    const uint C_MUTED = 0x009A9A9A;
    const uint C_ACCENT = 0x005777D9;  // naranja Anthropic #D97757
    const uint C_SEP = 0x00333333;
    const uint C_OFF = 0x00555555;

    // --- Items del panel ---
    const int K_SEP = 0, K_LANG = 1, K_COMPLETE = 2, K_AWAY = 3, K_PERM = 4, K_TIMER = 5, K_USAGE = 6,
              K_QUIT = 7, K_SOUND = 8, K_NOTIF = 9, K_BACK = 10, K_INFO = 11;
    static int _panelView; // 0 = principal, 1 = notificaciones, 2 = info
    static readonly int[] _itemsMain = { K_SEP, K_LANG, K_SEP, K_NOTIF, K_TIMER, K_USAGE, K_SEP, K_INFO, K_QUIT };
    static readonly int[] _itemsNotif = { K_BACK, K_SEP, K_COMPLETE, K_AWAY, K_PERM, K_SOUND };
    static readonly int[] _itemsInfo = { K_BACK };
    static int[] Items => _panelView switch { 1 => _itemsNotif, 2 => _itemsInfo, _ => _itemsMain };

    static nint _panelHwnd;
    static bool _panelVisible;
    static int _hoverItem = -1;
    static nint _fontTitle, _fontRow, _fontSmall;

    static int ItemsBottom()
    {
        int y = HEADER_H;
        foreach (var k in Items) y += (k == K_SEP) ? SEP_H : ROW_H;
        return y;
    }
    static int PanelHeight() => _panelView == 2
        ? (HEADER_H + ROW_H + SEP_H + DESC_H + 32 + 6)
        : (ItemsBottom() + 6);

    static void RegisterPanel(nint hInstance)
    {
        fixed (char* cls = "ClaudeStatusBarPanel")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0x00020000, // CS_DROPSHADOW
                lpfnWndProc = &PanelProc,
                hInstance = hInstance,
                lpszClassName = cls,
                hCursor = LoadCursorW(0, 32512), // IDC_ARROW
            };
            RegisterClassExW(&wc);
        }
        int h = PanelHeight();
        // WS_POPUP | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        _panelHwnd = CreateWindowExW(0x80 | 0x08, "ClaudeStatusBarPanel", "", 0x80000000,
            0, 0, PANEL_W, h, 0, 0, hInstance, 0);
        SetWindowRgn(_panelHwnd, CreateRoundRectRgn(0, 0, PANEL_W + 1, h + 1, 18, 18), 1);

        _fontTitle = CreateFontW(-16, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        _fontRow = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        _fontSmall = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
    }

    static void ShowPanel()
    {
        _hoverItem = -1;
        _panelView = 0; // siempre abre en la vista principal
        int h = PanelHeight();
        POINT pt; GetCursorPos(out pt);
        int x = pt.x - PANEL_W + 12;
        int y = pt.y - h - 12;
        RECT wa; SystemParametersInfoW(0x0030, 0, &wa, 0); // SPI_GETWORKAREA
        if (x < wa.left + 4) x = wa.left + 4;
        if (x + PANEL_W > wa.right - 4) x = wa.right - 4 - PANEL_W;
        if (y < wa.top + 4) y = pt.y + 14;
        if (y + h > wa.bottom - 4) y = wa.bottom - 4 - h;
        SetWindowPos(_panelHwnd, -1, x, y, PANEL_W, h, 0x40); // HWND_TOPMOST, SWP_SHOWWINDOW
        SetForegroundWindow(_panelHwnd);
        _panelVisible = true;
        InvalidateRect(_panelHwnd, 0, 0);
    }

    static void HidePanel()
    {
        _panelVisible = false;
        ShowWindow(_panelHwnd, 0); // SW_HIDE
    }

    static int HitItem(int my)
    {
        int y = HEADER_H;
        var items = Items;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == K_SEP) { y += SEP_H; continue; }
            if (my >= y && my < y + ROW_H) return i;
            y += ROW_H;
        }
        return -1;
    }

    static void ClickItem(int i)
    {
        if (i < 0) return;
        switch (Items[i])
        {
            case K_NOTIF: _panelView = 1; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return; // entrar notificaciones
            case K_INFO: _panelView = 2; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return;  // entrar info
            case K_BACK: _panelView = 0; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return;  // volver
            case K_LANG:
                string cur = _config.Language ?? "auto";
                _config.Language = cur switch { "auto" => "en", "en" => "es", "es" => "zh", _ => "auto" };
                break;
            case K_COMPLETE: _config.NotifyOnComplete = !_config.NotifyOnComplete; break;
            case K_AWAY: _config.NotifyAway = !_config.NotifyAway; break;
            case K_PERM: _config.NotifyOnPermission = !_config.NotifyOnPermission; break;
            case K_SOUND: _config.Sound = !_config.Sound; break;
            case K_TIMER: _config.ShowTimer = !_config.ShowTimer; break;
            case K_USAGE: _config.ShowUsage = !_config.ShowUsage; break;
            case K_QUIT: HidePanel(); DestroyWindow(_hwnd); return;
        }
        SaveConfig();
        ApplyLang();
        _lastTip = "";
        InvalidateRect(_panelHwnd, 0, 0);
    }

    // Redimensiona el panel al cambiar de vista, anclando el borde inferior (crece hacia arriba).
    static void ResizePanel()
    {
        _hoverItem = -1;
        int h = PanelHeight();
        GetWindowRect(_panelHwnd, out RECT r);
        int top = r.bottom - h;
        RECT wa; SystemParametersInfoW(0x0030, 0, &wa, 0);
        if (top < wa.top + 4) top = wa.top + 4;
        SetWindowRgn(_panelHwnd, CreateRoundRectRgn(0, 0, PANEL_W + 1, h + 1, 18, 18), 1);
        SetWindowPos(_panelHwnd, -1, r.left, top, PANEL_W, h, 0x10); // SWP_NOACTIVATE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint PanelProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case 0x0014: return 1;            // WM_ERASEBKGND -> sin parpadeo
            case 0x000F: PaintPanel(hWnd); return 0; // WM_PAINT
            case 0x0200:                      // WM_MOUSEMOVE
            {
                int my = (short)((lParam >> 16) & 0xFFFF);
                int hit = HitItem(my);
                if (hit != _hoverItem) { _hoverItem = hit; InvalidateRect(hWnd, 0, 0); }
                var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 0x00000002, hwndTrack = hWnd };
                TrackMouseEvent(ref tme);
                return 0;
            }
            case 0x02A3:                      // WM_MOUSELEAVE
                if (_hoverItem != -1) { _hoverItem = -1; InvalidateRect(hWnd, 0, 0); }
                return 0;
            case 0x0202:                      // WM_LBUTTONUP
            {
                int my = (short)((lParam >> 16) & 0xFFFF);
                if (_panelView == 2 && my >= HEADER_H + ROW_H + SEP_H + DESC_H) OpenUrl(CREDIT_URL); // credito -> X
                else ClickItem(HitItem(my));
                return 0;
            }
            case 0x0006:                      // WM_ACTIVATE
                if (!_debugKeepOpen && (wParam & 0xFFFF) == 0) HidePanel(); // WA_INACTIVE
                return 0;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static void PaintPanel(nint hWnd)
    {
        PAINTSTRUCT ps;
        nint hdc = BeginPaint(hWnd, out ps);
        RECT rc; GetClientRect(hWnd, out rc);
        int W = rc.right, H = rc.bottom;

        // doble buffer
        nint mem = CreateCompatibleDC(hdc);
        nint bmp = CreateCompatibleBitmap(hdc, W, H);
        nint oldBmp = SelectObject(mem, bmp);

        Fill(mem, 0, 0, W, H, C_BG);
        SetBkMode(mem, 1); // TRANSPARENT

        // --- Header ---
        if (_frames.Length > 0) DrawIconEx(mem, PAD, 16, _frames[0], ICON_SZ, ICON_SZ, 0, 0, 3);
        Txt(mem, "Claude Code", PAD + ICON_SZ + 8, 14, W - PAD, 14 + ICON_SZ, _fontTitle, C_TEXT, 0x24); // left+vcenter+single

        string status = _state.Status switch
        {
            "awaiting" => T("awaiting"),
            "thinking" or "tool" => T(string.IsNullOrEmpty(_state.LabelKey) ? "tool" : _state.LabelKey!),
            _ => T("idle"),
        };
        if ((_state.Status == "thinking" || _state.Status == "tool") && _state.TurnStartedAt > 0)
            status += "  -  " + FormatElapsed(_state.TurnStartedAt);
        Txt(mem, status, PAD, 44, W - PAD, 62, _fontSmall, C_MUTED, 0x24);

        string usage = ReadUsageString();
        if (usage.Length > 0) Txt(mem, usage, PAD, 60, W - PAD, 78, _fontSmall, C_ACCENT, 0x24);

        // --- Items ---
        int y = HEADER_H;
        var items = Items;
        for (int i = 0; i < items.Length; i++)
        {
            int k = items[i];
            if (k == K_SEP) { Fill(mem, PAD, y + 5, W - PAD, y + 6, C_SEP); y += SEP_H; continue; }

            if (i == _hoverItem) Fill(mem, 5, y, W - 5, y + ROW_H, C_HOVER);

            string label = k switch
            {
                K_LANG => T("m_language"),
                K_NOTIF => T("m_notifications"),
                K_INFO => T("m_info"),
                K_BACK => "<  " + (_panelView == 2 ? T("m_info") : T("m_notifications")),
                K_COMPLETE => T("m_notif_complete"),
                K_AWAY => T("m_notif_away"),
                K_PERM => T("m_notif_permission"),
                K_SOUND => T("m_sound"),
                K_TIMER => T("m_timer"),
                K_USAGE => T("m_usage"),
                K_QUIT => T("m_quit"),
                _ => "",
            };
            uint lc = k == K_QUIT ? C_MUTED : (k == K_BACK ? C_ACCENT : C_TEXT);
            Txt(mem, label, PAD, y, W - 70, y + ROW_H, _fontRow, lc, 0x24);

            if (k == K_LANG)
            {
                string lv = (_config.Language is null or "auto") ? T("m_auto") : LangName(_config.Language!);
                Txt(mem, lv + "  >", PAD, y, W - PAD, y + ROW_H, _fontRow, C_ACCENT, 0x26);
            }
            else if (k == K_NOTIF || k == K_INFO)
            {
                Txt(mem, ">", PAD, y, W - PAD, y + ROW_H, _fontRow, C_ACCENT, 0x26); // chevron entrar
            }
            else if (k != K_QUIT && k != K_BACK)
            {
                bool on = k switch
                {
                    K_COMPLETE => _config.NotifyOnComplete,
                    K_AWAY => _config.NotifyAway,
                    K_PERM => _config.NotifyOnPermission,
                    K_SOUND => _config.Sound,
                    K_TIMER => _config.ShowTimer,
                    K_USAGE => _config.ShowUsage,
                    _ => false,
                };
                Switch(mem, W - PAD - 18, y + ROW_H / 2, on);
            }
            y += ROW_H;
        }

        // Vista Info: descripcion + version + credito (clickeable -> X)
        if (_panelView == 2)
        {
            Fill(mem, PAD, y + 4, W - PAD, y + 5, C_SEP);
            int dy = y + SEP_H;
            TxtWrap(mem, T("info_desc"), PAD, dy, W - PAD, dy + DESC_H, _fontSmall, C_MUTED);
            int cy = dy + DESC_H;
            Txt(mem, VERSION + "   -   " + CREDIT, PAD, cy, W - PAD, cy + 28, _fontSmall, C_ACCENT, 0x05); // centrado
        }

        BitBlt(hdc, 0, 0, W, H, mem, 0, 0, 0x00CC0020); // SRCCOPY
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hWnd, ref ps);
    }

    // --- helpers de dibujo ---
    static void Fill(nint hdc, int l, int t, int r, int b, uint color)
    {
        RECT rc = new RECT { left = l, top = t, right = r, bottom = b };
        nint br = CreateSolidBrush(color);
        FillRect(hdc, ref rc, br);
        DeleteObject(br);
    }
    static void Txt(nint hdc, string s, int l, int t, int r, int b, nint font, uint color, uint flags)
    {
        if (string.IsNullOrEmpty(s)) return;
        SelectObject(hdc, font);
        SetTextColor(hdc, color);
        RECT rc = new RECT { left = l, top = t, right = r, bottom = b };
        fixed (char* p = s) DrawTextW(hdc, p, s.Length, ref rc, flags | 0x20); // | DT_SINGLELINE
    }
    static void TxtWrap(nint hdc, string s, int l, int t, int r, int b, nint font, uint color)
    {
        if (string.IsNullOrEmpty(s)) return;
        SelectObject(hdc, font);
        SetTextColor(hdc, color);
        RECT rc = new RECT { left = l, top = t, right = r, bottom = b };
        fixed (char* p = s) DrawTextW(hdc, p, s.Length, ref rc, 0x10); // DT_WORDBREAK
    }
    // Interruptor tipo switch con GDI+ (antialiasing) para que se vea suave.
    // Pista = 2 circulos + rectangulo (capsula). naranja=on / gris=off. Perilla blanca.
    static void Switch(nint hdc, int cx, int cy, bool on)
    {
        const float SW = 38, SH = 21;
        float l = cx - SW / 2, t = cy - SH / 2;
        uint track = on ? 0xFFD97757u : 0xFF555555u; // ARGB
        if (GdipCreateFromHDC(hdc, out nint g) != 0) return;
        GdipSetSmoothingMode(g, 2); // SmoothingModeAntiAlias

        GdipCreateSolidFill(track, out nint tb);
        GdipFillEllipse(g, tb, l, t, SH, SH);
        GdipFillEllipse(g, tb, l + SW - SH, t, SH, SH);
        GdipFillRectangle(g, tb, l + SH / 2, t, SW - SH, SH);

        float kd = SH - 6;
        float kx = on ? (l + SW - kd - 3) : (l + 3);
        GdipCreateSolidFill(0xFFFFFFFFu, out nint kb);
        GdipFillEllipse(g, kb, kx, t + 3, kd, kd);

        GdipDeleteBrush(kb);
        GdipDeleteBrush(tb);
        GdipDeleteGraphics(g);
    }

    // --- structs ---
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate; public fixed byte rgbReserved[32]; }
    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    // --- P/Invoke (GDI + ventana) ---
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint LoadCursorW(nint inst, int id);
    [DllImport("user32")] static extern int SetWindowRgn(nint h, nint rgn, int redraw);
    [DllImport("user32")] static extern int ShowWindow(nint h, int cmd);
    [DllImport("user32")] static extern int SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern int InvalidateRect(nint h, nint rc, int erase);
    [DllImport("user32")] static extern int SystemParametersInfoW(uint act, uint p, RECT* pv, uint w);
    [DllImport("user32")] static extern int TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32")] static extern nint BeginPaint(nint h, out PAINTSTRUCT ps);
    [DllImport("user32")] static extern int EndPaint(nint h, ref PAINTSTRUCT ps);
    [DllImport("user32")] static extern int GetClientRect(nint h, out RECT rc);
    [DllImport("user32")] static extern int GetWindowRect(nint h, out RECT rc);
    [DllImport("user32")] static extern int FillRect(nint hdc, ref RECT rc, nint br);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int DrawTextW(nint hdc, char* s, int len, ref RECT rc, uint fmt);
    [DllImport("user32")] static extern int DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [DllImport("gdi32")] static extern nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32")] static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32")] static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32")] static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32")] static extern int DeleteObject(nint obj);
    [DllImport("gdi32")] static extern int DeleteDC(nint hdc);
    [DllImport("gdi32")] static extern int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32")] static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32")] static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32")] static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32")] static extern nint GetStockObject(int obj);
    [DllImport("gdi32")] static extern int Ellipse(nint hdc, int l, int t, int r, int b);

    // GDI+ (antialiasing) para los switches
    [DllImport("gdiplus")] static extern int GdipCreateFromHDC(nint hdc, out nint graphics);
    [DllImport("gdiplus")] static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus")] static extern int GdipSetSmoothingMode(nint graphics, int mode);
    [DllImport("gdiplus")] static extern int GdipCreateSolidFill(uint argb, out nint brush);
    [DllImport("gdiplus")] static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus")] static extern int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);
    [DllImport("gdiplus")] static extern int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
