// Claude Status Bar (.exe nativo).
// Rebanada 1: plomeria Win32 (tray + menu + loop).
// Rebanada 2: Clawd (PNG embebidos -> HICON via GDI+).
// Rebanada 3: estado real (state.json -> caminata, tooltip i18n + cronometro + uso).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

internal static unsafe partial class Program
{
    // --- Constantes Win32 ---
    const uint WM_DESTROY = 0x0002;
    const uint WM_COMMAND = 0x0111;
    const uint WM_TIMER = 0x0113;
    const uint WM_RBUTTONUP = 0x0205;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_APP = 0x8000;
    const uint WM_TRAYICON = WM_APP + 1;

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    // En v4 los clics llegan como NIN_SELECT (izq) y WM_CONTEXTMENU (der), no WM_*BUTTONUP.
    const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, WM_CONTEXTMENU = 0x007B;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_NOSOUND = 0x10;   // toast sin sonido del sistema
    const uint NOTIFYICON_VERSION_4 = 4;
    const uint MF_STRING = 0, MF_POPUP = 0x10, MF_CHECKED = 0x08, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 2;
    const int IDI_APPLICATION = 32512;
    const uint TIMER_ID = 1;
    static readonly nint HWND_MESSAGE = -3;

    // IDs de comandos del menu
    const uint CMD_QUIT = 1;
    const uint CMD_LANG_AUTO = 10, CMD_LANG_EN = 11, CMD_LANG_ES = 12, CMD_LANG_ZH = 13;
    const uint CMD_NOTIF_COMPLETE = 20, CMD_NOTIF_AWAY = 21, CMD_NOTIF_PERM = 22;
    const uint CMD_TIMER = 30, CMD_USAGE = 31;

    static nint _hwnd;
    static NOTIFYICONDATAW _nid;
    static nint[] _frames = Array.Empty<nint>();

    // --- Rutas / estado ---
    static string _statePath = "", _configPath = "", _usagePath = "";
    static int _frameIdx;
    static int _tickN;
    static string _lastTip = "";
    static StateJson _state = new();

    // --- i18n / config ---
    static Dictionary<string, Dictionary<string, string>> _strings = new();
    static string _lang = "en";
    static ConfigJson _config = new();

    // --- notificaciones ---
    static long _awaitingUserSince;
    static bool _awayPinged;
    static nint _chimePtr;
    static GCHandle _chimeHandle;
    static bool _testNotif;

    static bool _debugKeepOpen;

    [STAThread]
    static int Main(string[] args)
    {
        ComputePaths();

        // Sub-comandos (no abren el iconito): hook / statusline / install / uninstall
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            switch (args[0])
            {
                case "hook": return HookCommand(args.Length > 1 ? args[1] : "");
                case "statusline": return StatuslineCommand();
                case "install": return InstallCommand(args);
                case "uninstall": return UninstallCommand(args);
            }
        }

        // Modo iconito: una sola instancia por perfil (CLAUDE_CONFIG_DIR)
        _singleInstance = new Mutex(true, MutexName(), out bool createdNew);
        if (!createdNew) return 0;

        _debugKeepOpen = Array.IndexOf(args, "--panel") >= 0 || Array.IndexOf(args, "--panel-notif") >= 0 || Array.IndexOf(args, "--panel-info") >= 0;
        _testNotif = Array.IndexOf(args, "--test-notif") >= 0;

        LoadStrings();
        LoadConfig();
        CleanLegacyMarkers(); // migracion v0.1: markers de sesion sin extension
        SweepRequests();      // requests huerfanos de corridas anteriores

        nint hInstance = GetModuleHandleW(null);
        fixed (char* clsName = "ClaudeStatusBarWndClass")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = clsName,
            };
            if (RegisterClassExW(&wc) == 0) return 1;
        }
        _hwnd = CreateWindowExW(0, "ClaudeStatusBarWndClass", "Claude Status Bar",
            0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);
        if (_hwnd == 0) return 2;

        GdiPlusStartup();
        _frames = LoadCrabFrames();
        LoadAnims(); // Clawds por contexto (popup permisos/plan/preguntas + header)
        LoadChime();
        nint icon = _frames.Length > 0 ? _frames[0] : LoadIconW(0, IDI_APPLICATION);

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = icon,
        };
        fixed (NOTIFYICONDATAW* pn = &_nid) SetTip(pn, "Claude - " + T("idle"));
        Shell_NotifyIconW(NIM_ADD, ref _nid);
        // Modo moderno (v4): necesario para el tooltip estandar en Windows 10/11.
        _nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref _nid);

        RegisterPanel(hInstance);   // panel oscuro estilo Claude Code
        RegisterApprove(hInstance); // popup de aprobacion (Allow/Deny)
        if (_debugKeepOpen)
        {
            ShowPanel(); // modo debug: mostrar el panel al arrancar
            if (Array.IndexOf(args, "--panel-notif") >= 0) { _panelView = 1; ResizePanel(); }
            else if (Array.IndexOf(args, "--panel-info") >= 0) { _panelView = 2; ResizePanel(); }
        }

        // Latido de render/lectura (~80ms = 12.5 fps, igual que el GIF original)
        SetTimer(_hwnd, TIMER_ID, 80, 0);

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                Tick();
                return 0;

            case WM_TRAYICON:
                uint ev = (uint)(lParam & 0xFFFF);
                // Cualquier clic (izq o der, v4 o legacy) abre el panel oscuro.
                if (ev == NIN_SELECT || ev == NIN_KEYSELECT || ev == WM_CONTEXTMENU
                    || ev == WM_LBUTTONUP || ev == WM_RBUTTONUP) ShowPanel();
                return 0;

            case WM_COMMAND:
                HandleCommand((uint)(wParam & 0xFFFF));
                return 0;

            case WM_DESTROY:
                KillTimer(_hwnd, TIMER_ID);
                Shell_NotifyIconW(NIM_DELETE, ref _nid);
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---------------- Logica de estado (rebanada 3 + multi-sesion) ----------------
    static void Tick()
    {
        _tickN++;
        // state.json mergeado: canal del Shutdown + fallback cuando no hay sesiones.
        var st = ReadState();
        if (st != null) _state = st;
        st = _state;

        if (st.Shutdown) { DestroyWindow(_hwnd); return; }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_tickN % 4 == 0) RefreshSessions();
        if (_tickN % 3 == 0) ScanRequests(now);
        if (_tickN % 64 == 0) PruneDeadSessions(now);

        // Recorre sesiones (notificaciones incluidas) y agrega para icono/tooltip.
        var a = Aggregate(now);

        if (_testNotif) { _testNotif = false; ShowNotif(T("n_done_title"), T("n_done_body").Replace("{0}", "2m 10s")); PlayChime(); }

        if (!a.AnyWorking && a.AllIdle && _config.NotifyAway && _awaitingUserSince > 0 && !_awayPinged
            && (now - _awaitingUserSince) / 1000 >= _config.AwayAfterSeconds)
        {
            ShowNotif(T("n_away_title"), T("n_away_body"));
            _awayPinged = true;
        }

        // Animacion: caminar si CUALQUIER sesion trabaja, frame 0 en reposo.
        _frameIdx = a.AnyWorking && _frames.Length > 0 ? (_frameIdx + 1) % _frames.Length : 0;

        string tip = "Claude - " + a.Tip;
        if (_config.ShowTimer && a.TimerStart > 0 && a.AnyWorking)
        {
            string el = FormatElapsed(a.TimerStart);
            if (el.Length > 0) tip += " - " + el;
        }
        if (_config.ShowUsage)
        {
            string usage = ReadUsageString();
            if (usage.Length > 0) tip += "\n" + usage;
        }

        // Actualizar icono + tooltip.
        _nid.hIcon = _frames.Length > 0 ? _frames[_frameIdx] : _nid.hIcon;
        _nid.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        if (tip != _lastTip)
        {
            fixed (NOTIFYICONDATAW* pn = &_nid) SetTip(pn, tip);
            _lastTip = tip;
        }
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);

        RefreshPanelIfNeeded(); // refrescar estado/cronometro/sesiones en vivo
        if (_apprVisible) InvalidateRect(_apprHwnd, 0, 0); // countdown del popup
    }

    static StateJson? ReadState()
    {
        try
        {
            string s = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize(s, JsonCtx.Default.StateJson);
        }
        catch { return null; }
    }

    static bool TestInterrupted(long updatedAt, string? transcript)
    {
        if (updatedAt > 0 &&
            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - updatedAt) / 1000 > 900) return true;
        if (string.IsNullOrEmpty(transcript) || !File.Exists(transcript)) return false;
        try
        {
            using var fs = new FileStream(transcript!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int take = (int)Math.Min(8192, fs.Length);
            if (take <= 0) return false;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            fs.ReadExactly(buf, 0, take);
            string text = System.Text.Encoding.UTF8.GetString(buf);
            string last = "";
            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) last = line;
            return last.Contains("interrupted by user", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    static string ReadUsageString()
    {
        try
        {
            var u = JsonSerializer.Deserialize(File.ReadAllText(_usagePath), JsonCtx.Default.UsageJson);
            if (u == null) return "";
            var parts = new List<string>();
            if (u.CtxPct.HasValue) parts.Add($"ctx {u.CtxPct}%");
            if (u.FiveHourPct.HasValue) parts.Add($"5h {u.FiveHourPct}%");
            if (u.SevenDayPct.HasValue) parts.Add($"7d {u.SevenDayPct}%");
            return string.Join(" - ", parts);
        }
        catch { return ""; }
    }

    static string FormatElapsed(long startMs)
    {
        long elapsed = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs) / 1000;
        if (elapsed < 0) elapsed = 0;
        long m = elapsed / 60, s = elapsed % 60;
        return m > 0 ? $"{m}m {s:D2}s" : $"{s}s";
    }

    // ---------------- i18n / config ----------------
    static void LoadStrings()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var n in asm.GetManifestResourceNames())
            {
                if (!n.EndsWith("strings.json", StringComparison.Ordinal)) continue;
                using var st = asm.GetManifestResourceStream(n)!;
                using var sr = new StreamReader(st);
                _strings = JsonSerializer.Deserialize(sr.ReadToEnd(), JsonCtx.Default.DictionaryStringDictionaryStringString) ?? new();
                break;
            }
        }
        catch { _strings = new(); }
    }

    static void LoadConfig()
    {
        try { _config = JsonSerializer.Deserialize(File.ReadAllText(_configPath), JsonCtx.Default.ConfigJson) ?? new(); }
        catch { _config = new(); }
        ApplyLang();
    }

    static void ApplyLang()
    {
        string l = _config.Language ?? "auto";
        if (l == "auto" || l.Length == 0)
        {
            int prim = GetUserDefaultUILanguage() & 0x3FF;
            l = prim == 0x0A ? "es" : prim == 0x04 ? "zh" : "en";
        }
        _lang = _strings.ContainsKey(l) ? l : "en";
    }

    static void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonCtx.Default.ConfigJson));
        }
        catch { }
    }

    static string T(string key)
    {
        if (_strings.TryGetValue(_lang, out var tbl) && tbl.TryGetValue(key, out var v)) return v;
        if (_strings.TryGetValue("en", out var en) && en.TryGetValue(key, out var ve)) return ve;
        return key;
    }

    // T con idioma explicito: el popup de preguntas/plan habla el idioma DEL contenido
    // que escribio Claude, no el de la UI (pedido del usuario).
    static string TL(string lang, string key)
    {
        if (_strings.TryGetValue(lang, out var tbl) && tbl.TryGetValue(key, out var v)) return v;
        return T(key);
    }

    static readonly char[] EsChars = { '¿', '¡', 'á', 'é', 'í', 'ó', 'ú', 'ñ', 'Á', 'É', 'Í', 'Ó', 'Ú', 'Ñ' };
    static readonly string[] EsWords = { " el ", " la ", " los ", " las ", " qué ", " que ", " para ", " con ", " una ", " cómo ", " este ", " esta " };

    static string DetectLang(string? s)
    {
        if (string.IsNullOrEmpty(s)) return _lang;
        foreach (char c in s)
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3040 && c <= 0x30FF)) return "zh";
        if (s.IndexOfAny(EsChars) >= 0) return "es";
        string low = " " + s.ToLowerInvariant() + " ";
        int hits = 0;
        foreach (var w in EsWords) if (low.Contains(w)) hits++;
        return hits >= 2 ? "es" : "en";
    }

    // ---------------- Menu (funcional; panel estilo Claude Code = pulido visual posterior) ----------------
    static void MItem(nint menu, uint id, string text, bool chk)
    {
        fixed (char* p = text) AppendMenuW(menu, MF_STRING | (chk ? MF_CHECKED : 0), id, p);
    }
    static void MSub(nint menu, nint sub, string text)
    {
        fixed (char* p = text) AppendMenuW(menu, MF_POPUP, (nuint)sub, p);
    }
    static string LangName(string code)
        => _strings.TryGetValue("_lang", out var d) && d.TryGetValue(code, out var v) ? v : code;

    static void ShowMenu()
    {
        nint menu = CreatePopupMenu();

        nint lang = CreatePopupMenu();
        MItem(lang, CMD_LANG_AUTO, T("m_auto"), _config.Language is null or "auto");
        MItem(lang, CMD_LANG_EN, LangName("en"), _config.Language == "en");
        MItem(lang, CMD_LANG_ES, LangName("es"), _config.Language == "es");
        MItem(lang, CMD_LANG_ZH, LangName("zh"), _config.Language == "zh");
        MSub(menu, lang, T("m_language"));

        nint notif = CreatePopupMenu();
        MItem(notif, CMD_NOTIF_COMPLETE, T("m_notif_complete"), _config.NotifyOnComplete);
        MItem(notif, CMD_NOTIF_AWAY, T("m_notif_away"), _config.NotifyAway);
        MItem(notif, CMD_NOTIF_PERM, T("m_notif_permission"), _config.NotifyOnPermission);
        MSub(menu, notif, T("m_notifications"));

        MItem(menu, CMD_TIMER, T("m_timer"), _config.ShowTimer);
        MItem(menu, CMD_USAGE, T("m_usage"), _config.ShowUsage);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        MItem(menu, CMD_QUIT, T("m_quit"), false);

        POINT pt; GetCursorPos(out pt);
        SetForegroundWindow(_hwnd); // para que el menu se cierre al perder foco
        TrackPopupMenuEx(menu, TPM_RIGHTBUTTON, pt.x, pt.y, _hwnd, 0);
        DestroyMenu(menu);
    }

    static void HandleCommand(uint id)
    {
        switch (id)
        {
            case CMD_QUIT: DestroyWindow(_hwnd); return;
            case CMD_LANG_AUTO: _config.Language = "auto"; break;
            case CMD_LANG_EN: _config.Language = "en"; break;
            case CMD_LANG_ES: _config.Language = "es"; break;
            case CMD_LANG_ZH: _config.Language = "zh"; break;
            case CMD_NOTIF_COMPLETE: _config.NotifyOnComplete = !_config.NotifyOnComplete; break;
            case CMD_NOTIF_AWAY: _config.NotifyAway = !_config.NotifyAway; break;
            case CMD_NOTIF_PERM: _config.NotifyOnPermission = !_config.NotifyOnPermission; break;
            case CMD_TIMER: _config.ShowTimer = !_config.ShowTimer; break;
            case CMD_USAGE: _config.ShowUsage = !_config.ShowUsage; break;
            default: return;
        }
        SaveConfig();
        ApplyLang();
        _lastTip = ""; // forzar refresco del tooltip (idioma puede haber cambiado)
    }

    static void SetTip(NOTIFYICONDATAW* nid, string tip)
    {
        int n = Math.Min(tip.Length, 127);
        for (int i = 0; i < n; i++) nid->szTip[i] = tip[i];
        nid->szTip[n] = '\0';
    }

    // ---------------- Notificaciones ----------------
    static void LoadChime()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var n in asm.GetManifestResourceNames())
            {
                if (!n.EndsWith("chime.wav", StringComparison.Ordinal)) continue;
                using var st = asm.GetManifestResourceStream(n)!;
                var bytes = new byte[st.Length];
                st.ReadExactly(bytes, 0, bytes.Length);
                _chimeHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned); // fijo en memoria para SND_ASYNC
                _chimePtr = _chimeHandle.AddrOfPinnedObject();
                break;
            }
        }
        catch { }
    }

    static void PlayChime()
    {
        if (_chimePtr != 0) PlaySoundW((byte*)_chimePtr, 0, 0x0007); // SND_MEMORY|SND_ASYNC|SND_NODEFAULT
    }

    static void SetStr(char* dst, string s, int max)
    {
        int n = Math.Min(s.Length, max);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    static void ShowNotif(string title, string body)
    {
        // Prioridad del popup de aprobacion: mientras este visible (o haya requests
        // pendientes) no se muestra NINGUN toast — taparia los botones Allow/Deny.
        if (_apprVisible || _pendingReqs.Count > 0) return;
        fixed (NOTIFYICONDATAW* pn = &_nid)
        {
            SetStr(pn->szInfoTitle, title, 63);
            SetStr(pn->szInfo, body, 255);
        }
        _nid.dwInfoFlags = NIIF_NOSOUND; // silencioso; el chime se reproduce aparte
        _nid.uFlags = NIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
    }

    // ---------------- Carga de frames (GDI+) ----------------
    static void GdiPlusStartup()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        GdiplusStartup(out _, ref input, 0);
    }

    static nint[] LoadCrabFrames()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var found = new List<(int idx, string name)>();
        foreach (var n in asm.GetManifestResourceNames())
        {
            int p = n.IndexOf("frame-", StringComparison.Ordinal);
            if (p < 0 || !n.EndsWith(".png", StringComparison.Ordinal)) continue;
            int s = p + 6, e = s;
            while (e < n.Length && char.IsDigit(n[e])) e++;
            if (e > s) found.Add((int.Parse(n.Substring(s, e - s)), n));
        }
        found.Sort((a, b) => a.idx.CompareTo(b.idx));

        var icons = new List<nint>();
        foreach (var (_, name) in found)
        {
            using var st = asm.GetManifestResourceStream(name);
            if (st == null) continue;
            var bytes = new byte[st.Length];
            st.ReadExactly(bytes, 0, bytes.Length);
            nint h = HiconFromPng(bytes);
            if (h != 0) icons.Add(h);
        }
        return icons.ToArray();
    }

    static nint HiconFromPng(byte[] png)
    {
        fixed (byte* p = png)
        {
            nint stream = SHCreateMemStream(p, (uint)png.Length);
            if (stream == 0) return 0;
            try
            {
                if (GdipCreateBitmapFromStream(stream, out nint bmp) != 0 || bmp == 0) return 0;
                try { return GdipCreateHICONFromBitmap(bmp, out nint hicon) == 0 ? hicon : 0; }
                finally { GdipDisposeImage(bmp); }
            }
            finally { Marshal.Release(stream); }
        }
    }

    // ---------------- Modelos JSON (source-gen, AOT-safe) ----------------
    class StateJson
    {
        public string? Status { get; set; }
        public string? LabelKey { get; set; }
        public long TurnStartedAt { get; set; }
        public string? Transcript { get; set; }
        public long UpdatedAt { get; set; }
        public bool Shutdown { get; set; }
    }
    class ConfigJson
    {
        public string? Language { get; set; }
        public bool ShowTimer { get; set; } = true;
        public bool ShowUsage { get; set; } = true;
        public bool NotifyOnComplete { get; set; } = true;
        public int CompleteMinSeconds { get; set; } = 60;
        public bool NotifyAway { get; set; } = true;
        public int AwayAfterSeconds { get; set; } = 120;
        public bool NotifyOnPermission { get; set; } = true;
        public bool Sound { get; set; } = true;
        // v0.2: aprobaciones GUI
        public bool GuiApprovals { get; set; } = true;
        public bool GuiPlanReview { get; set; } = true;
        public int PermissionTimeoutSeconds { get; set; } = 60;   // 10..300
        public int PlanTimeoutSeconds { get; set; } = 300;        // 30..570
        public bool NotifyOnQuestion { get; set; } = true;
        public bool GuiQuestions { get; set; } = true;            // responder AskUserQuestion desde popup
        public int QuestionTimeoutSeconds { get; set; } = 120;    // 10..570
    }
    class UsageJson
    {
        public int? CtxPct { get; set; }
        public int? FiveHourPct { get; set; }
        public int? SevenDayPct { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(StateJson))]
    [JsonSerializable(typeof(SessionJson))]
    [JsonSerializable(typeof(ReqJson))]
    [JsonSerializable(typeof(DecisionJson))]
    [JsonSerializable(typeof(ConfigJson))]
    [JsonSerializable(typeof(UsageJson))]
    [JsonSerializable(typeof(HookInput))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(StatuslineInput))]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
    partial class JsonCtx : JsonSerializerContext { }

    // ---------------- Win32 / GDI+ interop ----------------
    [StructLayout(LayoutKind.Sequential)]
    struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [DllImport("gdiplus")] static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, nint output);
    [DllImport("gdiplus")] static extern int GdipCreateBitmapFromStream(nint stream, out nint bitmap);
    [DllImport("gdiplus")] static extern int GdipCreateHICONFromBitmap(nint bitmap, out nint hicon);
    [DllImport("gdiplus")] static extern int GdipDisposeImage(nint image);
    [DllImport("shlwapi", EntryPoint = "SHCreateMemStream")] static extern nint SHCreateMemStream(byte* pInit, uint cbInit);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]   // <-- clave: WCHAR, no ANSI
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;            // union con uTimeout
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public POINT pt; }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32")] static extern ushort GetUserDefaultUILanguage();
    [DllImport("user32")] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32")] static extern int GetMessageW(MSG* msg, nint h, uint min, uint max);
    [DllImport("user32")] static extern int TranslateMessage(MSG* msg);
    [DllImport("user32")] static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32")] static extern void PostQuitMessage(int code);
    [DllImport("user32")] static extern int DestroyWindow(nint h);
    [DllImport("user32")] static extern nint SetTimer(nint h, uint id, uint ms, nint proc);
    [DllImport("user32")] static extern int KillTimer(nint h, uint id);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint LoadIconW(nint inst, int id);
    [DllImport("user32")] static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int AppendMenuW(nint menu, uint flags, nuint id, char* item);
    [DllImport("user32")] static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint lptpm);
    [DllImport("user32")] static extern int DestroyMenu(nint menu);
    [DllImport("user32")] static extern int GetCursorPos(out POINT p);
    [DllImport("user32")] static extern int SetForegroundWindow(nint h);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("winmm", CharSet = CharSet.Unicode)] static extern int PlaySoundW(byte* data, nint hmod, uint flags);
}
