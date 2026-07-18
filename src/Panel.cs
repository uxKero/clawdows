// Panel oscuro estilo Claude Code (dibujado a mano con GDI).
// v0.2: layout-list (filas de altura variable) con lista de sesiones arriba:
// proyecto + estado + cronometro, click = saltar a la terminal/IDE de esa sesion.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe partial class Program
{
    // --- Metricas ---
    const int PANEL_W = 300;
    const int PAD = 14;
    const int HEADER_H = 80;
    const int ROW_H = 34;
    const int SESS_H = 44;
    const int SEP_H = 11;
    const int ICON_SZ = 24;
    const int DESC_H = 92;
    const string VERSION = "v1.0";
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

    // --- Kinds de item ---
    const int K_SEP = 0, K_LANG = 1, K_COMPLETE = 2, K_AWAY = 3, K_PERM = 4, K_TIMER = 5, K_USAGE = 6,
              K_QUIT = 7, K_SOUND = 8, K_NOTIF = 9, K_BACK = 10, K_INFO = 11,
              K_SESSION = 12, K_MORE = 13, K_NONE = 14, K_DESC = 15, K_CREDIT = 16,
              K_APPROVALS = 17, K_GUIAPPR = 18, K_PLANREV = 19, K_QUESTION = 20, K_GUIQ = 21;

    struct PItem { public int Kind; public int Top, Bottom; public int Arg; }

    static int _panelView; // 0 = principal, 1 = notificaciones, 2 = info, 3 = aprobaciones
    static readonly List<PItem> _layout = new();
    static List<SessView> _panelSessions = new();
    static int _layoutH;

    static nint _panelHwnd;
    static bool _panelVisible;
    static int _hoverItem = -1;
    static nint _fontTitle, _fontRow, _fontSmall;

    static bool Clickable(int kind) => kind is not (K_SEP or K_DESC or K_NONE or K_MORE);

    // Un solo constructor de layout para pintar, hit-test y altura.
    static void BuildLayout()
    {
        _layout.Clear();
        int y = HEADER_H;
        void Add(int kind, int h, int arg = 0)
        { _layout.Add(new PItem { Kind = kind, Top = y, Bottom = y + h, Arg = arg }); y += h; }

        switch (_panelView)
        {
            case 1: // notificaciones
                Add(K_BACK, ROW_H); Add(K_SEP, SEP_H);
                Add(K_COMPLETE, ROW_H); Add(K_AWAY, ROW_H); Add(K_PERM, ROW_H); Add(K_SOUND, ROW_H);
                break;
            case 2: // info
                Add(K_BACK, ROW_H); Add(K_SEP, SEP_H);
                Add(K_DESC, DESC_H); Add(K_CREDIT, 32);
                break;
            case 3: // aprobaciones
                Add(K_BACK, ROW_H); Add(K_SEP, SEP_H);
                Add(K_GUIAPPR, ROW_H); Add(K_PLANREV, ROW_H); Add(K_GUIQ, ROW_H); Add(K_QUESTION, ROW_H);
                break;
            default: // principal
                Add(K_SEP, SEP_H);
                _panelSessions = SessionsForPanel();
                if (_panelSessions.Count == 0) Add(K_NONE, ROW_H);
                else
                {
                    int shown = Math.Min(_panelSessions.Count, 5);
                    for (int i = 0; i < shown; i++)
                        Add(K_SESSION, SESS_H + (_panelSessions[i].S.Question != null ? 16 : 0), i);
                    if (_panelSessions.Count > shown) Add(K_MORE, 22, _panelSessions.Count - shown);
                }
                Add(K_SEP, SEP_H);
                Add(K_LANG, ROW_H);
                Add(K_SEP, SEP_H);
                Add(K_NOTIF, ROW_H); Add(K_APPROVALS, ROW_H); Add(K_TIMER, ROW_H); Add(K_USAGE, ROW_H);
                Add(K_SEP, SEP_H);
                Add(K_INFO, ROW_H); Add(K_QUIT, ROW_H);
                break;
        }
        _layoutH = y + 6;
    }

    // Sesiones ordenadas: trabajando primero, luego por proyecto.
    static List<SessView> SessionsForPanel()
    {
        var list = new List<SessView>(_sessViews.Values);
        list.Sort((x, y) =>
        {
            static int Rank(SessView v) => v.S.Status switch
            {
                "awaiting" => 0,
                "thinking" or "tool" => v.Interrupted ? 2 : 1,
                _ => 2,
            };
            int r = Rank(x).CompareTo(Rank(y));
            return r != 0 ? r : string.Compare(x.S.Project, y.S.Project, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    static int PanelHeight() { BuildLayout(); return _layoutH; }

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
        // La region redondeada DEBE acompanar la altura actual (si no, recorta el panel
        // cuando la cantidad de sesiones cambio desde el arranque).
        SetWindowRgn(_panelHwnd, CreateRoundRectRgn(0, 0, PANEL_W + 1, h + 1, 18, 18), 1);
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
        for (int i = 0; i < _layout.Count; i++)
            if (Clickable(_layout[i].Kind) && my >= _layout[i].Top && my < _layout[i].Bottom) return i;
        return -1;
    }

    static void ClickItem(int i)
    {
        if (i < 0 || i >= _layout.Count) return;
        var it = _layout[i];
        switch (it.Kind)
        {
            case K_NOTIF: _panelView = 1; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return;
            case K_INFO: _panelView = 2; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return;
            case K_APPROVALS: _panelView = 3; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return;
            case K_BACK: _panelView = 0; ResizePanel(); InvalidateRect(_panelHwnd, 0, 0); return;
            case K_CREDIT: OpenUrl(CREDIT_URL); return;
            case K_SESSION:
                if (it.Arg < _panelSessions.Count)
                {
                    var s = _panelSessions[it.Arg].S;
                    HidePanel();
                    if (HasPendingFor(s.SessionId)) OpenApproveFor(s.SessionId);
                    else JumpToTerminal(s);
                }
                return;
            case K_LANG:
                string cur = _config.Language ?? "auto";
                _config.Language = cur switch { "auto" => "en", "en" => "es", "es" => "zh", _ => "auto" };
                break;
            case K_COMPLETE: _config.NotifyOnComplete = !_config.NotifyOnComplete; break;
            case K_AWAY: _config.NotifyAway = !_config.NotifyAway; break;
            case K_PERM: _config.NotifyOnPermission = !_config.NotifyOnPermission; break;
            case K_SOUND: _config.Sound = !_config.Sound; break;
            case K_GUIAPPR: _config.GuiApprovals = !_config.GuiApprovals; break;
            case K_PLANREV: _config.GuiPlanReview = !_config.GuiPlanReview; break;
            case K_GUIQ: _config.GuiQuestions = !_config.GuiQuestions; break;
            case K_QUESTION: _config.NotifyOnQuestion = !_config.NotifyOnQuestion; break;
            case K_TIMER: _config.ShowTimer = !_config.ShowTimer; break;
            case K_USAGE: _config.ShowUsage = !_config.ShowUsage; break;
            case K_QUIT: HidePanel(); DestroyWindow(_hwnd); return;
        }
        SaveConfig();
        ApplyLang();
        _lastTip = "";
        InvalidateRect(_panelHwnd, 0, 0);
    }

    // Redimensiona el panel (cambio de vista o de cantidad de sesiones),
    // anclando el borde inferior (crece hacia arriba).
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

    // Llamado desde Tick cuando el panel esta visible: si cambio la altura
    // (aparecio/desaparecio una sesion), reacomodar.
    static void RefreshPanelIfNeeded()
    {
        if (!_panelVisible) return;
        GetWindowRect(_panelHwnd, out RECT r);
        if (PanelHeight() != r.bottom - r.top) ResizePanel();
        InvalidateRect(_panelHwnd, 0, 0);
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
                ClickItem(HitItem(my));
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

        BuildLayout(); // layout fresco (sesiones pueden haber cambiado)

        // doble buffer
        nint mem = CreateCompatibleDC(hdc);
        nint bmp = CreateCompatibleBitmap(hdc, W, H);
        nint oldBmp = SelectObject(mem, bmp);

        Fill(mem, 0, 0, W, H, C_BG);
        SetBkMode(mem, 1); // TRANSPARENT

        // --- Header: Clawd animado segun estado (work/think/wait/music) ---
        nint hic = HeaderIcon();
        if (hic != 0) DrawIconEx(mem, PAD, 12, hic, 32, 32, 0, 0, 3);
        Txt(mem, "Claude Code", PAD + 40, 14, W - PAD, 14 + ICON_SZ, _fontTitle, C_TEXT, 0x24);

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
        for (int i = 0; i < _layout.Count; i++)
        {
            var it = _layout[i];
            int y = it.Top, yb = it.Bottom;

            if (it.Kind == K_SEP) { Fill(mem, PAD, y + 5, W - PAD, y + 6, C_SEP); continue; }
            if (i == _hoverItem && Clickable(it.Kind)) RoundFill(mem, 6, y + 1, W - 6, yb - 1, 8, 0xFF322B27);

            switch (it.Kind)
            {
                case K_SESSION: PaintSession(mem, W, y, yb, _panelSessions[it.Arg]); continue;
                case K_NONE: Txt(mem, T("s_none"), PAD, y, W - PAD, yb, _fontRow, C_MUTED, 0x24); continue;
                case K_MORE: Txt(mem, T("s_more").Replace("{0}", it.Arg.ToString()), PAD, y, W - PAD, yb, _fontSmall, C_MUTED, 0x24); continue;
                case K_DESC: TxtWrap(mem, T("info_desc"), PAD, y, W - PAD, yb, _fontSmall, C_MUTED); continue;
                case K_CREDIT: Txt(mem, VERSION + "   -   " + CREDIT, PAD, y, W - PAD, yb, _fontSmall, C_ACCENT, 0x05); continue;
            }

            string label = it.Kind switch
            {
                K_LANG => T("m_language"),
                K_NOTIF => T("m_notifications"),
                K_INFO => T("m_info"),
                K_APPROVALS => T("m_approvals"),
                K_BACK => _panelView switch { 2 => T("m_info"), 3 => T("m_approvals"), _ => T("m_notifications") },
                K_COMPLETE => T("m_notif_complete"),
                K_AWAY => T("m_notif_away"),
                K_PERM => T("m_notif_permission"),
                K_SOUND => T("m_sound"),
                K_GUIAPPR => T("m_gui_approvals"),
                K_PLANREV => T("m_plan_review"),
                K_GUIQ => T("m_gui_questions"),
                K_QUESTION => T("m_notif_question"),
                K_TIMER => T("m_timer"),
                K_USAGE => T("m_usage"),
                K_QUIT => T("m_quit"),
                _ => "",
            };
            uint lc = it.Kind == K_QUIT ? C_MUTED : (it.Kind == K_BACK ? C_ACCENT : C_TEXT);
            int lx = it.Kind == K_BACK ? PAD + 14 : PAD;
            Txt(mem, label, lx, y, W - 70, yb, _fontRow, lc, 0x24);

            if (it.Kind == K_BACK)
            {
                Chevron(mem, PAD + 4, (y + yb) / 2f, -1, 0xFFD97757);
            }
            else if (it.Kind == K_LANG)
            {
                string lv = (_config.Language is null or "auto") ? T("m_auto") : LangName(_config.Language!);
                Txt(mem, lv, PAD, y, W - PAD - 16, yb, _fontRow, C_ACCENT, 0x26);
                Chevron(mem, W - PAD - 5, (y + yb) / 2f, 1, 0xFFD97757);
            }
            else if (it.Kind is K_NOTIF or K_INFO or K_APPROVALS)
            {
                Chevron(mem, W - PAD - 5, (y + yb) / 2f, 1, 0xFFD97757);
            }
            else if (it.Kind is not (K_QUIT or K_BACK))
            {
                bool on = it.Kind switch
                {
                    K_COMPLETE => _config.NotifyOnComplete,
                    K_AWAY => _config.NotifyAway,
                    K_PERM => _config.NotifyOnPermission,
                    K_SOUND => _config.Sound,
                    K_GUIAPPR => _config.GuiApprovals,
                    K_PLANREV => _config.GuiPlanReview,
                    K_GUIQ => _config.GuiQuestions,
                    K_QUESTION => _config.NotifyOnQuestion,
                    K_TIMER => _config.ShowTimer,
                    K_USAGE => _config.ShowUsage,
                    _ => false,
                };
                Switch(mem, W - PAD - 18, y + ROW_H / 2, on);
            }
        }

        BitBlt(hdc, 0, 0, W, H, mem, 0, 0, 0x00CC0020); // SRCCOPY
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hWnd, ref ps);
    }

    // Fila de sesion: dot de estado + proyecto / estado + cronometro; ">" = jump.
    static void PaintSession(nint hdc, int W, int y, int yb, SessView v)
    {
        var s = v.S;
        bool working = (s.Status == "thinking" || s.Status == "tool") && !v.Interrupted;

        uint dot = s.Status == "awaiting" ? 0xFFE6B450u : working ? 0xFFD97757u : 0xFF555555u; // ARGB
        Dot(hdc, PAD + 4, y + 13, 8, dot);

        string proj = string.IsNullOrEmpty(s.Project) ? "Claude" : s.Project!;
        Txt(hdc, proj, PAD + 16, y + 3, W - 40, y + 23, _fontRow, C_TEXT, 0x24);

        string st = s.Status switch
        {
            "awaiting" => T("awaiting"),
            "thinking" or "tool" => working
                ? T(string.IsNullOrEmpty(s.LabelKey) ? "tool" : s.LabelKey!)
                : T("idle"),
            _ => T("idle"),
        };
        if (working && s.TurnStartedAt > 0) st += "  -  " + FormatElapsed(s.TurnStartedAt);
        Txt(hdc, st, PAD + 16, y + 22, W - 40, y + 40, _fontSmall, C_MUTED, 0x24);

        if (s.Question is { Text.Length: > 0 } q)
            Txt(hdc, q.Text, PAD + 16, yb - 18, W - 30, yb - 2, _fontSmall, 0x0050B4E6, 0x24);

        if (HasPendingFor(s.SessionId))
            Txt(hdc, T("s_review"), PAD, y, W - PAD - 4, yb, _fontSmall, 0x0050B4E6, 0x26); // amarillo #E6B450
        else if (s.TermHwnd != 0)
            Chevron(hdc, W - PAD - 5, (y + yb) / 2f, 1, 0xFFD97757);
    }

    static void Dot(nint hdc, int cx, int cy, int d, uint argb)
    {
        if (GdipCreateFromHDC(hdc, out nint g) != 0) return;
        GdipSetSmoothingMode(g, 2);
        GdipCreateSolidFill(argb, out nint b);
        GdipFillEllipse(g, b, cx - d / 2f, cy - d / 2f, d, d);
        GdipDeleteBrush(b);
        GdipDeleteGraphics(g);
    }

    // Rectangulo redondeado con antialiasing (hover, botones) — ARGB.
    static void RoundFill(nint hdc, float l, float t, float r, float b, float rad, uint argb)
    {
        if (GdipCreateFromHDC(hdc, out nint g) != 0) return;
        GdipSetSmoothingMode(g, 2);
        if (GdipCreatePath(0, out nint path) == 0)
        {
            float d = rad * 2;
            GdipAddPathArc(path, l, t, d, d, 180, 90);
            GdipAddPathArc(path, r - d, t, d, d, 270, 90);
            GdipAddPathArc(path, r - d, b - d, d, d, 0, 90);
            GdipAddPathArc(path, l, b - d, d, d, 90, 90);
            GdipClosePathFigure(path);
            GdipCreateSolidFill(argb, out nint br);
            GdipFillPath(g, br, path);
            GdipDeleteBrush(br);
            GdipDeletePath(path);
        }
        GdipDeleteGraphics(g);
    }

    // Chevron dibujado (nada de caracteres ">"): dir 1 = derecha, -1 = izquierda.
    static void Chevron(nint hdc, float cx, float cy, int dir, uint argb)
    {
        if (GdipCreateFromHDC(hdc, out nint g) != 0) return;
        GdipSetSmoothingMode(g, 2);
        if (GdipCreatePen1(argb, 1.7f, 2 /*UnitPixel*/, out nint pen) == 0)
        {
            GdipSetPenStartCap(pen, 2); // LineCapRound
            GdipSetPenEndCap(pen, 2);
            float w = 2.6f * dir, s = 4.2f;
            GdipDrawLine(g, pen, cx - w / 2, cy - s, cx + w / 2, cy);
            GdipDrawLine(g, pen, cx + w / 2, cy, cx - w / 2, cy + s);
            GdipDeletePen(pen);
        }
        GdipDeleteGraphics(g);
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

    // GDI+ (antialiasing) para switches y dots
    [DllImport("gdiplus")] static extern int GdipCreateFromHDC(nint hdc, out nint graphics);
    [DllImport("gdiplus")] static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus")] static extern int GdipSetSmoothingMode(nint graphics, int mode);
    [DllImport("gdiplus")] static extern int GdipCreateSolidFill(uint argb, out nint brush);
    [DllImport("gdiplus")] static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus")] static extern int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);
    [DllImport("gdiplus")] static extern int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);
    [DllImport("gdiplus")] static extern int GdipCreatePath(int fillMode, out nint path);
    [DllImport("gdiplus")] static extern int GdipDeletePath(nint path);
    [DllImport("gdiplus")] static extern int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [DllImport("gdiplus")] static extern int GdipClosePathFigure(nint path);
    [DllImport("gdiplus")] static extern int GdipFillPath(nint graphics, nint brush, nint path);
    [DllImport("gdiplus")] static extern int GdipCreatePen1(uint argb, float width, int unit, out nint pen);
    [DllImport("gdiplus")] static extern int GdipDeletePen(nint pen);
    [DllImport("gdiplus")] static extern int GdipSetPenStartCap(nint pen, int cap);
    [DllImport("gdiplus")] static extern int GdipSetPenEndCap(nint pen, int cap);
    [DllImport("gdiplus")] static extern int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
