// Rebanada 5 (v0.2): popup de aprobacion (Allow / Deny / responder en terminal).
// El tray observa requests\*.req.json (escritos por `hook permission`), muestra el
// popup sin robar foco, y responde escribiendo <id>.decision.json. El hook borra
// sus archivos al salir; aca solo se limpian huerfanos (hook muerto / vencidos).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static unsafe partial class Program
{
    const int APR_W = 380;
    const int APR_BTN_H = 32;
    const int PLAN_W = 560, PLAN_H = 640;
    const int PLAN_HEAD = 64, PLAN_FOOT = 74;

    class ReqView { public ReqJson R = new(); public string Path = ""; }
    static readonly List<ReqView> _pendingReqs = new();
    static int _apprIdx;
    static nint _apprHwnd;
    static bool _apprVisible;
    static int _apprHover = -1; // 0=allow 1=deny 2=terminal
    static RECT _rAllow, _rDeny, _rTerm;
    static nint _fontH1, _fontH2, _fontH3, _fontMono;
    // Modo pregunta (AskUserQuestion): respuestas acumuladas del request actual.
    static string? _qForId;
    static readonly List<string> _qAnswers = new();
    static readonly HashSet<int> _qSel = new(); // seleccion multiSelect en curso
    static readonly RECT[] _qOptRects = new RECT[4];
    static int _qOptCount;
    static int _planScroll, _planContentH;
    static readonly List<(int kind, string text)> _planBlocks = new(); // 0 parr,1-3 h,4 bullet,5 code
    static string? _planParsedFor;

    static ReqJson? CurrentReq => _apprIdx >= 0 && _apprIdx < _pendingReqs.Count ? _pendingReqs[_apprIdx].R : null;
    static bool CurrentIsPlan => CurrentReq?.Kind == "plan";

    // Markdown-lite: headings, bullets, fences; strip de ** y ` inline.
    static void ParsePlan(string plan)
    {
        if (_planParsedFor == plan) return;
        _planParsedFor = plan;
        _planBlocks.Clear();
        _planScroll = 0;
        bool code = false;
        foreach (var raw in plan.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw;
            string t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (code) { _planBlocks.Add((5, line.Length == 0 ? " " : line)); continue; }
            if (t.Length == 0) { _planBlocks.Add((0, "")); continue; }
            string Strip(string s) => s.Replace("**", "").Replace("`", "");
            if (t.StartsWith("### ", StringComparison.Ordinal)) _planBlocks.Add((3, Strip(t.Substring(4))));
            else if (t.StartsWith("## ", StringComparison.Ordinal)) _planBlocks.Add((2, Strip(t.Substring(3))));
            else if (t.StartsWith("# ", StringComparison.Ordinal)) _planBlocks.Add((1, Strip(t.Substring(2))));
            else if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
                _planBlocks.Add((4, Strip(t.Substring(2))));
            else if (t.Length > 2 && char.IsDigit(t[0]) && (t[1] == '.' || t[1] == ')'))
                _planBlocks.Add((4, Strip(t.Substring(2).TrimStart())));
            else _planBlocks.Add((0, Strip(t)));
        }
    }

    static string DecisionPathFor(string reqPath) =>
        reqPath.EndsWith(".req.json", StringComparison.OrdinalIgnoreCase)
            ? reqPath.Substring(0, reqPath.Length - 9) + ".decision.json"
            : reqPath + ".decision.json";

    // Limpieza al arrancar el tray: requests viejos de sesiones muertas.
    static void SweepRequests()
    {
        try
        {
            if (!Directory.Exists(RequestsDir)) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var f in Directory.GetFiles(RequestsDir))
            {
                bool stale = f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (!stale)
                    try { stale = (now - new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds()) > 10 * 60_000; }
                    catch { }
                if (stale) { try { File.Delete(f); } catch { } }
            }
        }
        catch { }
    }

    // Corre cada ~240ms en Tick: alta de requests nuevos, baja de resueltos/huerfanos.
    static void ScanRequests(long now)
    {
        try
        {
            var found = new HashSet<string>();
            if (Directory.Exists(RequestsDir))
                foreach (var f in Directory.GetFiles(RequestsDir, "*.req.json"))
                {
                    found.Add(f);
                    if (_pendingReqs.Exists(v => v.Path == f)) continue;
                    try
                    {
                        var r = JsonSerializer.Deserialize(File.ReadAllText(f), JsonCtx.Default.ReqJson);
                        if (r == null) continue;
                        _pendingReqs.Add(new ReqView { R = r, Path = f });
                        OnNewRequest(r);
                    }
                    catch { }
                }

            bool changed = false;
            for (int i = _pendingReqs.Count - 1; i >= 0; i--)
            {
                var v = _pendingReqs[i];
                bool gone = !found.Contains(v.Path);                        // el hook lo resolvio
                bool dead = !gone && v.R.HookPid > 0 && !PidAlive((uint)v.R.HookPid); // Esc / kill
                bool expired = !gone && v.R.ExpiresAt > 0 && now > v.R.ExpiresAt + 2000;
                if (gone || dead || expired)
                {
                    if (!gone)
                    {
                        try { File.Delete(v.Path); } catch { }
                        try { File.Delete(DecisionPathFor(v.Path)); } catch { }
                    }
                    _pendingReqs.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed) SyncApprove();
        }
        catch { }
    }

    // El popup ES el aviso: nada de toast encima (el usuario lo marco). Solo chime.
    static void OnNewRequest(ReqJson r)
    {
        if (_config.Sound) PlayChime();
        SyncApprove();
    }

    static void SyncApprove()
    {
        if (_pendingReqs.Count == 0) { HideApprove(); return; }
        if (_apprIdx >= _pendingReqs.Count) _apprIdx = _pendingReqs.Count - 1;
        if (_apprIdx < 0) _apprIdx = 0;
        ShowApprove(false);
    }

    static bool HasPendingFor(string? sid) =>
        sid != null && _pendingReqs.Exists(v => v.R.SessionId == sid);

    static void OpenApproveFor(string? sid)
    {
        int i = sid == null ? -1 : _pendingReqs.FindIndex(v => v.R.SessionId == sid);
        if (i < 0) return;
        _apprIdx = i;
        ShowApprove(true);
    }

    // Decide el request visible y avanza al siguiente (el hook borra los archivos).
    static void DecideCurrent(string behavior)
    {
        if (_apprIdx < 0 || _apprIdx >= _pendingReqs.Count) return;
        var v = _pendingReqs[_apprIdx];
        try
        {
            WriteAtomic(DecisionPathFor(v.Path), JsonSerializer.Serialize(
                new DecisionJson { Behavior = behavior, DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                JsonCtx.Default.DecisionJson));
        }
        catch { }
        if (behavior == "passthrough") JumpToSession(v.R.SessionId);
        _pendingReqs.RemoveAt(_apprIdx);
        SyncApprove();
    }

    static void JumpToSession(string? sid)
    {
        if (sid == null) return;
        foreach (var v in _sessViews.Values)
            if (v.S.SessionId == sid) { JumpToTerminal(v.S); return; }
    }

    // ---------------- Ventana ----------------
    static int ApproveHeight(ReqJson r)
    {
        int lines = Math.Max(r.Summary?.Length ?? 0, 1);
        return 78 + lines * 18 + 12 + APR_BTN_H + 26 + 12;
    }

    static void RegisterApprove(nint hInstance)
    {
        fixed (char* cls = "ClaudeStatusBarApprove")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0x00020000, // CS_DROPSHADOW
                lpfnWndProc = &ApproveProc,
                hInstance = hInstance,
                lpszClassName = cls,
                hCursor = LoadCursorW(0, 32512),
            };
            RegisterClassExW(&wc);
        }
        // WS_POPUP; WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE
        _apprHwnd = CreateWindowExW(0x80 | 0x08 | 0x08000000, "ClaudeStatusBarApprove", "", 0x80000000,
            0, 0, APR_W, 200, 0, 0, hInstance, 0);

        _fontH1 = CreateFontW(-20, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        _fontH2 = CreateFontW(-17, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        _fontH3 = CreateFontW(-15, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        _fontMono = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Consolas");
    }

    static void ShowApprove(bool activate)
    {
        if (_apprIdx < 0 || _apprIdx >= _pendingReqs.Count) return;
        var r = _pendingReqs[_apprIdx].R;
        RECT wa; SystemParametersInfoW(0x0030, 0, &wa, 0);
        int w, h, x, y;
        if (r.Kind == "plan")
        {
            ParsePlan(r.Plan ?? "");
            w = Math.Min(PLAN_W, wa.right - wa.left - 40);
            h = Math.Min(PLAN_H, wa.bottom - wa.top - 40);
            x = wa.right - w - 16; y = wa.bottom - h - 16; // misma esquina, mas grande
        }
        else if (r.Kind == "question")
        {
            if (r.Id != _qForId) { _qForId = r.Id; _qAnswers.Clear(); _qSel.Clear(); }
            w = APR_W; h = QuestionHeight(r);
            x = wa.right - w - 12; y = wa.bottom - h - 12;
        }
        else
        {
            w = APR_W; h = ApproveHeight(r);
            x = wa.right - w - 12; y = wa.bottom - h - 12;
        }
        SetWindowRgn(_apprHwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, 14, 14), 1);
        // HWND_TOPMOST; SWP_SHOWWINDOW (0x40) + SWP_NOACTIVATE (0x10) si no es click del usuario
        SetWindowPos(_apprHwnd, -1, x, y, w, h, activate ? 0x40u : 0x50u);
        if (activate) SetForegroundWindow(_apprHwnd);
        _apprVisible = true;
        InvalidateRect(_apprHwnd, 0, 0);
    }

    static void HideApprove()
    {
        if (!_apprVisible) return;
        _apprVisible = false;
        _apprHover = -1;
        ShowWindow(_apprHwnd, 0);
    }

    static bool InRect(in RECT r, int x, int y) => x >= r.left && x < r.right && y >= r.top && y < r.bottom;

    static int ApproveHit(int mx, int my)
    {
        if (CurrentReq?.Kind == "question")
        {
            for (int i = 0; i < _qOptCount; i++) if (InRect(_qOptRects[i], mx, my)) return 100 + i;
            if (InRect(_rAllow, mx, my)) return 0; // boton Confirmar (multiSelect)
            return InRect(_rTerm, mx, my) ? 2 : -1;
        }
        if (InRect(_rAllow, mx, my)) return 0;
        if (InRect(_rDeny, mx, my)) return 1;
        if (InRect(_rTerm, mx, my)) return 2;
        return -1;
    }

    static QuestionJson? CurrentQuestion(ReqJson r) =>
        r.Questions is { Length: > 0 } qs ? qs[Math.Min(_qAnswers.Count, qs.Length - 1)] : null;

    // Click en una opcion: single-select responde; multiSelect togglea la seleccion.
    static void AnswerOption(int i)
    {
        var r = CurrentReq;
        var q = r != null ? CurrentQuestion(r) : null;
        if (q == null) return;
        if (q.MultiSelect)
        {
            if (!_qSel.Add(i)) _qSel.Remove(i);
            InvalidateRect(_apprHwnd, 0, 0);
            return;
        }
        CommitAnswer(q.Options != null && i < q.Options.Length ? q.Options[i] : "");
    }

    static void ConfirmMulti()
    {
        var r = CurrentReq;
        var q = r != null ? CurrentQuestion(r) : null;
        if (q is not { MultiSelect: true } || _qSel.Count == 0 || q.Options == null) return;
        var picked = new List<string>();
        for (int i = 0; i < q.Options.Length; i++) if (_qSel.Contains(i)) picked.Add(q.Options[i]);
        CommitAnswer(string.Join(", ", picked));
    }

    static void CommitAnswer(string ans)
    {
        var r = CurrentReq;
        if (r?.Questions is not { Length: > 0 } qs) return;
        _qAnswers.Add(ans);
        _qSel.Clear();
        if (_qAnswers.Count >= qs.Length)
        {
            var v = _pendingReqs[_apprIdx];
            try
            {
                WriteAtomic(DecisionPathFor(v.Path), JsonSerializer.Serialize(
                    new DecisionJson { Behavior = "answer", Answers = _qAnswers.ToArray(), DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    JsonCtx.Default.DecisionJson));
            }
            catch { }
            _qForId = null;
            _pendingReqs.RemoveAt(_apprIdx);
            SyncApprove();
        }
        else ShowApprove(true); // siguiente pregunta (recalcula altura)
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint ApproveProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case 0x0014: return 1;                 // WM_ERASEBKGND
            case 0x000F: PaintApprove(hWnd); return 0; // WM_PAINT
            case 0x0021: return CurrentIsPlan ? 1 : 3; // WM_MOUSEACTIVATE: plan se activa (teclado/drag), popup no roba foco
            case 0x0084:                           // WM_NCHITTEST: banda superior del plan = arrastrable
            {
                if (!CurrentIsPlan) break;
                nint def = DefWindowProcW(hWnd, msg, wParam, lParam);
                if (def != 1) return def;          // HTCLIENT
                GetWindowRect(hWnd, out RECT wr);
                int sy = (short)((lParam >> 16) & 0xFFFF);
                return sy - wr.top < PLAN_HEAD - 20 ? 2 : 1; // HTCAPTION : HTCLIENT
            }
            case 0x0100:                           // WM_KEYDOWN
                if ((int)wParam == 0x1B && _apprVisible) DecideCurrent("passthrough"); // Esc
                return 0;
            case 0x020A:                           // WM_MOUSEWHEEL
            {
                if (!CurrentIsPlan) return 0;
                int delta = (short)((wParam >> 16) & 0xFFFF);
                GetClientRect(hWnd, out RECT crc);
                int view = crc.bottom - PLAN_HEAD - PLAN_FOOT;
                _planScroll = Math.Clamp(_planScroll - delta / 120 * 48, 0, Math.Max(0, _planContentH - view));
                InvalidateRect(hWnd, 0, 0);
                return 0;
            }
            case 0x0200:                           // WM_MOUSEMOVE
            {
                int mx = (short)(lParam & 0xFFFF), my = (short)((lParam >> 16) & 0xFFFF);
                int hit = ApproveHit(mx, my);
                if (hit != _apprHover) { _apprHover = hit; InvalidateRect(hWnd, 0, 0); }
                var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 0x00000002, hwndTrack = hWnd };
                TrackMouseEvent(ref tme);
                return 0;
            }
            case 0x02A3:                           // WM_MOUSELEAVE
                if (_apprHover != -1) { _apprHover = -1; InvalidateRect(hWnd, 0, 0); }
                return 0;
            case 0x0202:                           // WM_LBUTTONUP
            {
                int mx = (short)(lParam & 0xFFFF), my = (short)((lParam >> 16) & 0xFFFF);
                int hit = ApproveHit(mx, my);
                if (hit >= 100) AnswerOption(hit - 100);
                else if (hit == 0 && CurrentReq?.Kind == "question") ConfirmMulti();
                else switch (hit)
                {
                    case 0: DecideCurrent("allow"); break;
                    case 1: DecideCurrent("deny"); break;
                    case 2: DecideCurrent("passthrough"); break;
                }
                return 0;
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static void PaintApprove(nint hWnd)
    {
        PAINTSTRUCT ps;
        nint hdc = BeginPaint(hWnd, out ps);
        RECT rc; GetClientRect(hWnd, out rc);
        int W = rc.right, H = rc.bottom;

        nint mem = CreateCompatibleDC(hdc);
        nint bmp = CreateCompatibleBitmap(hdc, W, H);
        nint oldBmp = SelectObject(mem, bmp);

        Fill(mem, 0, 0, W, H, C_BG);
        SetBkMode(mem, 1);

        if (_apprIdx >= 0 && _apprIdx < _pendingReqs.Count && _pendingReqs[_apprIdx].R.Kind == "plan")
        {
            PaintPlan(mem, W, H, _pendingReqs[_apprIdx].R);
        }
        else if (_apprIdx >= 0 && _apprIdx < _pendingReqs.Count && _pendingReqs[_apprIdx].R.Kind == "question")
        {
            PaintQuestion(mem, W, H, _pendingReqs[_apprIdx].R);
        }
        else if (_apprIdx >= 0 && _apprIdx < _pendingReqs.Count)
        {
            var r = _pendingReqs[_apprIdx].R;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Header: Clawd animado (martillazo) + titulo + "n / m"
            nint ic = AnimFrame(_animPerm);
            if (ic != 0) DrawIconEx(mem, PAD, 8, ic, 40, 40, 0, 0, 3);
            string title = r.Kind == "plan" ? T("plan_title") : T("apr_title");
            Txt(mem, title, PAD + 48, 10, W - 60, 34, _fontTitle, C_TEXT, 0x24);
            if (_pendingReqs.Count > 1)
                Txt(mem, (_apprIdx + 1) + " / " + _pendingReqs.Count, PAD, 10, W - PAD, 34, _fontSmall, C_MUTED, 0x26);

            // Proyecto + tool
            string proj = r.Project ?? "";
            Txt(mem, proj, PAD + 48, 32, W - PAD, 50, _fontSmall, C_MUTED, 0x24);
            Txt(mem, r.Tool ?? "", PAD, 54, W - PAD, 74, _fontRow, C_ACCENT, 0x24);

            // Summary
            int y = 78;
            if (r.Summary != null)
                foreach (var line in r.Summary)
                {
                    Txt(mem, line, PAD, y, W - PAD, y + 18, _fontSmall, C_TEXT, 0x24);
                    y += 18;
                }
            y += 12;

            // Botones Allow / Deny
            int bw = (W - PAD * 3) / 2;
            _rAllow = new RECT { left = PAD, top = y, right = PAD + bw, bottom = y + APR_BTN_H };
            _rDeny = new RECT { left = PAD * 2 + bw, top = y, right = PAD * 2 + bw * 2, bottom = y + APR_BTN_H };
            RoundFill(mem, _rAllow.left, _rAllow.top, _rAllow.right, _rAllow.bottom, 8, _apprHover == 0 ? 0xFFC96347u : 0xFFD97757u);
            Txt(mem, r.Kind == "plan" ? T("plan_approve") : T("apr_allow"),
                _rAllow.left, _rAllow.top, _rAllow.right, _rAllow.bottom, _fontRow, 0x00FFFFFF, 0x25);
            RoundFill(mem, _rDeny.left, _rDeny.top, _rDeny.right, _rDeny.bottom, 8, _apprHover == 1 ? 0xFF322B27u : 0xFF2A2A2Au);
            Txt(mem, r.Kind == "plan" ? T("plan_reject") : T("apr_deny"),
                _rDeny.left, _rDeny.top, _rDeny.right, _rDeny.bottom, _fontRow, C_TEXT, 0x25);

            // Link "responder en terminal (Ns)"
            int ty = y + APR_BTN_H + 4;
            _rTerm = new RECT { left = PAD, top = ty, right = W - PAD, bottom = ty + 22 };
            long remain = Math.Max(0, (r.ExpiresAt - now) / 1000);
            Txt(mem, T("apr_terminal").Replace("{0}", remain.ToString()),
                _rTerm.left, _rTerm.top, _rTerm.right, _rTerm.bottom, _fontSmall,
                _apprHover == 2 ? C_ACCENT : C_MUTED, 0x25);
        }

        BitBlt(hdc, 0, 0, W, H, mem, 0, 0, 0x00CC0020);
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hWnd, ref ps);
    }

    // ---------------- Modo pregunta (AskUserQuestion) ----------------
    static int QuestionLines(QuestionJson? q) =>
        Math.Min(4, (q?.Text?.Length ?? 0) / 42 + 1);

    static int QuestionHeight(ReqJson r)
    {
        var q = CurrentQuestion(r);
        int opts = Math.Max(q?.Options?.Length ?? 0, 1);
        return 54 + QuestionLines(q) * 19 + 8 + Math.Min(opts, 4) * 38
             + (q?.MultiSelect == true ? 38 : 0) + 24 + 12;
    }

    static void PaintQuestion(nint mem, int W, int H, ReqJson r)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var q = CurrentQuestion(r);
        var qs = r.Questions ?? Array.Empty<QuestionJson>();
        int qi = Math.Min(_qAnswers.Count, Math.Max(qs.Length - 1, 0));

        string ql = DetectLang(q?.Text); // el popup habla el idioma de la pregunta
        nint qic = AnimFrame(_animQuestion);
        if (qic != 0) DrawIconEx(mem, PAD, 6, qic, 40, 40, 0, 0, 3);
        Txt(mem, TL(ql, "n_q_title"), PAD + 48, 10, W - 80, 34, _fontTitle, C_TEXT, 0x24);
        string cnt = qs.Length > 1 ? (qi + 1) + " / " + qs.Length : "";
        if (cnt.Length > 0) Txt(mem, cnt, PAD, 10, W - PAD, 34, _fontSmall, C_MUTED, 0x26);
        Txt(mem, r.Project ?? "", PAD + 48, 32, W - PAD, 50, _fontSmall, C_MUTED, 0x24);

        int y = 54;
        if (q?.Text is { Length: > 0 } qt)
        {
            int lines = QuestionLines(q);
            TxtWrap(mem, qt, PAD, y, W - PAD, y + lines * 19, _fontRow, C_TEXT);
            y += lines * 19 + 8;
        }

        _qOptCount = 0;
        var opts = q?.Options ?? Array.Empty<string>();
        bool multi = q?.MultiSelect == true;
        for (int i = 0; i < opts.Length && i < 4; i++)
        {
            var rc = new RECT { left = PAD, top = y, right = W - PAD, bottom = y + 32 };
            _qOptRects[i] = rc; _qOptCount++;
            bool sel = multi && _qSel.Contains(i);
            RoundFill(mem, rc.left, rc.top, rc.right, rc.bottom, 8,
                sel ? 0xFF453325u : _apprHover == 100 + i ? 0xFF322B27u : 0xFF2A2A2Au);
            int tx = rc.left + 10;
            if (multi)
            {
                // checkbox redondeado: relleno acento si esta seleccionado
                RoundFill(mem, rc.left + 10, rc.top + 10, rc.left + 22, rc.top + 22, 3, sel ? 0xFFD97757u : 0xFF484848u);
                if (!sel) RoundFill(mem, rc.left + 12, rc.top + 12, rc.left + 20, rc.top + 20, 2, 0xFF2A2A2Au);
                tx = rc.left + 32;
            }
            Txt(mem, opts[i], tx, rc.top, rc.right - 10, rc.bottom, _fontRow,
                sel ? C_TEXT : _apprHover == 100 + i ? C_TEXT : 0x00D0D0D0, 0x24 | 0x8000); // DT_END_ELLIPSIS
            y += 38;
        }

        if (multi)
        {
            _rAllow = new RECT { left = PAD, top = y, right = W - PAD, bottom = y + 32 };
            bool ok = _qSel.Count > 0;
            RoundFill(mem, _rAllow.left, _rAllow.top, _rAllow.right, _rAllow.bottom, 8,
                !ok ? 0xFF242424u : _apprHover == 0 ? 0xFFC96347u : 0xFFD97757u);
            Txt(mem, TL(ql, "q_confirm") + (ok ? "  (" + _qSel.Count + ")" : ""),
                _rAllow.left, _rAllow.top, _rAllow.right, _rAllow.bottom, _fontRow,
                ok ? 0x00FFFFFF : C_OFF, 0x25);
            y += 38;
        }
        else _rAllow = default;
        _rDeny = default;

        _rTerm = new RECT { left = PAD, top = y, right = W - PAD, bottom = y + 22 };
        long remain = Math.Max(0, (r.ExpiresAt - now) / 1000);
        Txt(mem, TL(ql, "apr_terminal").Replace("{0}", remain.ToString()),
            _rTerm.left, _rTerm.top, _rTerm.right, _rTerm.bottom, _fontSmall,
            _apprHover == 2 ? C_ACCENT : C_MUTED, 0x25);
    }

    // ---------------- Modo plan (markdown-lite con scroll) ----------------
    static void PaintPlan(nint mem, int W, int H, ReqJson r)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ParsePlan(r.Plan ?? "");

        // Header (banda arrastrable): Clawd con el foquito. Habla el idioma del plan.
        string pl = DetectLang(r.Plan);
        nint pic = AnimFrame(_animPlan);
        if (pic != 0) DrawIconEx(mem, PAD, 10, pic, 40, 40, 0, 0, 3);
        Txt(mem, TL(pl, "plan_title"), PAD + 48, 12, W - 70, 36, _fontTitle, C_TEXT, 0x24);
        if (_pendingReqs.Count > 1)
            Txt(mem, (_apprIdx + 1) + " / " + _pendingReqs.Count, PAD, 12, W - PAD, 36, _fontSmall, C_MUTED, 0x26);
        Txt(mem, r.Project ?? "", PAD + 48, 36, W - PAD, 54, _fontSmall, C_MUTED, 0x24);
        Fill(mem, PAD, PLAN_HEAD - 5, W - PAD, PLAN_HEAD - 4, C_SEP);

        // Cuerpo con clip + scroll manual
        int viewTop = PLAN_HEAD, viewBot = H - PLAN_FOOT;
        int view = viewBot - viewTop;
        IntersectClipRect(mem, 0, viewTop, W, viewBot);
        int x0 = PAD + 2, x1 = W - PAD - 12;
        int y = viewTop + 6 - _planScroll;
        foreach (var (kind, text) in _planBlocks)
        {
            int indent = kind == 4 ? 18 : 0;
            int bh = MeasureBlock(mem, kind, text, x1 - x0 - indent);
            if (y + bh > viewTop && y < viewBot) DrawBlock(mem, kind, text, x0, y, x1, bh);
            y += bh + (kind == 5 ? 0 : 5);
        }
        _planContentH = y + _planScroll - viewTop + 6;
        SelectClipRgn(mem, 0);

        // Scrollbar fino
        if (_planContentH > view)
        {
            int track = view - 8;
            int th = Math.Max(24, (int)((long)track * view / _planContentH));
            int to = (int)((long)(track - th) * _planScroll / Math.Max(1, _planContentH - view));
            Fill(mem, W - 7, viewTop + 4 + to, W - 3, viewTop + 4 + to + th, C_OFF);
        }

        // Footer: Aprobar / Rechazar / Abrir en terminal (countdown)
        Fill(mem, PAD, viewBot + 4, W - PAD, viewBot + 5, C_SEP);
        int by = viewBot + 14;
        int bw = (W - PAD * 4) / 3;
        _rAllow = new RECT { left = PAD, top = by, right = PAD + bw, bottom = by + APR_BTN_H };
        _rDeny = new RECT { left = PAD * 2 + bw, top = by, right = PAD * 2 + bw * 2, bottom = by + APR_BTN_H };
        _rTerm = new RECT { left = PAD * 3 + bw * 2, top = by, right = PAD * 3 + bw * 3, bottom = by + APR_BTN_H };
        RoundFill(mem, _rAllow.left, _rAllow.top, _rAllow.right, _rAllow.bottom, 8, _apprHover == 0 ? 0xFFC96347u : 0xFFD97757u);
        Txt(mem, TL(pl, "plan_approve"), _rAllow.left, _rAllow.top, _rAllow.right, _rAllow.bottom, _fontRow, 0x00FFFFFF, 0x25);
        RoundFill(mem, _rDeny.left, _rDeny.top, _rDeny.right, _rDeny.bottom, 8, _apprHover == 1 ? 0xFF322B27u : 0xFF2A2A2Au);
        Txt(mem, TL(pl, "plan_reject"), _rDeny.left, _rDeny.top, _rDeny.right, _rDeny.bottom, _fontRow, C_TEXT, 0x25);
        RoundFill(mem, _rTerm.left, _rTerm.top, _rTerm.right, _rTerm.bottom, 8, _apprHover == 2 ? 0xFF322B27u : 0xFF232323u);
        long remain = Math.Max(0, (r.ExpiresAt - now) / 1000);
        Txt(mem, TL(pl, "plan_open") + " (" + remain + "s)", _rTerm.left, _rTerm.top, _rTerm.right, _rTerm.bottom, _fontSmall, C_MUTED, 0x25);
    }

    static int MeasureBlock(nint hdc, int kind, string text, int width)
    {
        if (text.Length == 0) return 8; // linea en blanco = espaciador
        nint f = kind switch { 1 => _fontH1, 2 => _fontH2, 3 => _fontH3, 5 => _fontMono, _ => _fontRow };
        SelectObject(hdc, f);
        RECT rc = new RECT { left = 0, top = 0, right = width, bottom = 0 };
        uint flags = 0x400u | (kind == 5 ? 0u : 0x10u); // DT_CALCRECT | DT_WORDBREAK
        fixed (char* pch = text) DrawTextW(hdc, pch, text.Length, ref rc, flags);
        return Math.Max(rc.bottom + (kind == 5 ? 6 : 0), 16);
    }

    static void DrawBlock(nint hdc, int kind, string text, int x0, int y, int x1, int bh)
    {
        if (text.Length == 0) return;
        if (kind == 5) // codigo: fondo mas oscuro, mono, sin wrap
        {
            Fill(hdc, x0 - 2, y, x1 + 6, y + bh, 0x00161616);
            SelectObject(hdc, _fontMono);
            SetTextColor(hdc, 0x00A8D8B0); // verde suave
            RECT rc = new RECT { left = x0 + 6, top = y + 3, right = x1, bottom = y + bh };
            fixed (char* pch = text) DrawTextW(hdc, pch, text.Length, ref rc, 0);
            return;
        }
        if (kind == 4) { Dot(hdc, x0 + 5, y + 9, 5, 0xFFD97757); x0 += 18; }
        nint f = kind switch { 1 => _fontH1, 2 => _fontH2, 3 => _fontH3, _ => _fontRow };
        uint col = kind is 1 or 2 or 3 ? C_TEXT : 0x00C4C4C4;
        SelectObject(hdc, f);
        SetTextColor(hdc, col);
        RECT rc2 = new RECT { left = x0, top = y, right = x1, bottom = y + bh };
        fixed (char* pch = text) DrawTextW(hdc, pch, text.Length, ref rc2, 0x10); // DT_WORDBREAK
    }

    [DllImport("gdi32")] static extern int IntersectClipRect(nint hdc, int l, int t, int r, int b);
    [DllImport("gdi32")] static extern int SelectClipRgn(nint hdc, nint rgn);
}
