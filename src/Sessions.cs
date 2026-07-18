// Rebanada 4 (v0.2): estado por sesion + descubrimiento de terminal.
// Cada sesion de Claude Code vive en sessions.d\<sid>.json (el state.json mergeado
// se mantiene por compatibilidad). El hook descubre la ventana de la terminal/IDE
// caminando su propia cadena de procesos padre — sin whitelist de terminales.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static unsafe partial class Program
{
    // ---------------- Modelos ----------------
    class SessionJson
    {
        public string? SessionId { get; set; }
        public string? Status { get; set; }      // idle | thinking | tool | awaiting
        public string? LabelKey { get; set; }
        public long TurnStartedAt { get; set; }
        public string? Transcript { get; set; }
        public string? Cwd { get; set; }
        public string? Project { get; set; }
        public int ClaudePid { get; set; }
        public int TermPid { get; set; }
        public long TermHwnd { get; set; }
        public string? TermExe { get; set; }
        public long UpdatedAt { get; set; }
        public QuestionJson? Question { get; set; }
    }
    class QuestionJson
    {
        public string? Text { get; set; }
        public string[]? Options { get; set; }
        public bool MultiSelect { get; set; }
        public long AskedAt { get; set; }
    }

    // ---------------- Rutas / CRUD ----------------
    static string SessionsDir => Path.Combine(_sbDir, "sessions.d");
    static string SessionPath(string sid) => Path.Combine(SessionsDir, Sanitize(sid) + ".json");

    static SessionJson? ReadSession(string sid)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(SessionPath(sid)), JsonCtx.Default.SessionJson); }
        catch { return null; }
    }
    static void WriteSession(SessionJson s) =>
        WriteAtomic(SessionPath(s.SessionId ?? ""), JsonSerializer.Serialize(s, JsonCtx.Default.SessionJson));
    static void DeleteSession(string sid)
    {
        try { File.Delete(SessionPath(sid)); } catch { }
    }
    static int CountSessions()
    {
        try { return Directory.Exists(SessionsDir) ? Directory.GetFiles(SessionsDir, "*.json").Length : 0; }
        catch { return 0; }
    }

    // Migracion v0.1: los markers eran archivos sin extension.
    static void CleanLegacyMarkers()
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) return;
            foreach (var f in Directory.GetFiles(SessionsDir))
                if (!f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
        }
        catch { }
    }

    static string ProjectName(string cwd)
    {
        try
        {
            string t = cwd.TrimEnd('\\', '/');
            string name = Path.GetFileName(t);
            return name.Length > 0 ? name : t;
        }
        catch { return cwd; }
    }

    // ---------------- Descubrimiento de terminal (lado hook) ----------------
    // El hook corre como: nuestro exe <- bash <- claude/node <- shell <- [helpers sin
    // ventana] <- terminal o IDE. El ancestro mas cercano con ventana visible top-level
    // es el objetivo del jump; si el proceso tiene varias ventanas (IDE con 2 ventanas),
    // se prefiere la que menciona el proyecto en el titulo.
    static Dictionary<uint, List<(nint hwnd, string title)>>? _enumWins;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int EnumWinCb(nint hwnd, nint lp)
    {
        var map = _enumWins;
        if (map == null) return 0;
        if (IsWindowVisible(hwnd) == 0) return 1;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!map.TryGetValue(pid, out var list)) return 1;      // solo pids de la cadena
        if (((long)GetWindowLongPtrW(hwnd, -20) & 0x80) != 0) return 1; // WS_EX_TOOLWINDOW
        int len = GetWindowTextLengthW(hwnd);
        if (len <= 0 || len > 512) return 1;
        char* buf = stackalloc char[len + 1];
        int got = GetWindowTextW(hwnd, buf, len + 1);
        list.Add((hwnd, new string(buf, 0, Math.Max(got, 0))));
        return 1;
    }

    static void DiscoverTerminal(SessionJson s)
    {
        try
        {
            // 1) Snapshot de procesos: pid -> (ppid, exe)
            var map = new Dictionary<uint, (uint ppid, string exe)>();
            nint snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == 0 || snap == -1) return;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
                if (Process32FirstW(snap, ref e) != 0)
                    do
                    {
                        map[e.th32ProcessID] = (e.th32ParentProcessID, new string(e.szExeFile));
                    } while (Process32NextW(snap, ref e) != 0);
            }
            finally { CloseHandle(snap); }

            // 2) Cadena de ancestros desde el hook (guarda anti-ciclos por reuso de PID)
            var chain = new List<uint>();
            var seen = new HashSet<uint>();
            uint cur = (uint)Environment.ProcessId;
            for (int i = 0; i < 20 && cur != 0 && seen.Add(cur) && map.TryGetValue(cur, out var pe); i++)
            {
                chain.Add(cur);
                if (s.ClaudePid == 0 &&
                    (pe.exe.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
                     || pe.exe.Equals("node.exe", StringComparison.OrdinalIgnoreCase)))
                    s.ClaudePid = (int)cur;
                cur = pe.ppid;
            }

            // 3) Ventanas top-level visibles de esos ancestros
            var wins = new Dictionary<uint, List<(nint, string)>>();
            foreach (var pid in chain) wins[pid] = new();
            _enumWins = wins;
            EnumWindows(&EnumWinCb, 0);
            _enumWins = null;

            // 4) Ancestro mas cercano con ventana; desempate por titulo con el proyecto
            foreach (var pid in chain)
            {
                var list = wins[pid];
                if (list.Count == 0) continue;
                var pick = list[0];
                if (!string.IsNullOrEmpty(s.Project))
                    foreach (var w in list)
                        if (w.Item2.Contains(s.Project!, StringComparison.OrdinalIgnoreCase)) { pick = w; break; }
                s.TermPid = (int)pid;
                s.TermHwnd = pick.Item1;
                s.TermExe = map.TryGetValue(pid, out var pe2) ? pe2.exe : "";
                break;
            }
        }
        catch { }
    }

    // ---------------- Cache del tray + agregacion ----------------
    class SessView
    {
        public SessionJson S = new();
        public DateTime Mtime;
        public bool Interrupted;
    }
    class NotifState
    {
        public bool PrevWorking;
        public string PrevStatus = "idle";
        public long LastTurnStart;
        public long LastQuestionAt;
    }
    struct Agg
    {
        public bool AnyWorking, AllIdle, AnyAwaiting;
        public int Working, Total;
        public long TimerStart;
        public string Tip;
    }

    static readonly Dictionary<string, SessView> _sessViews = new(); // key: ruta del archivo
    static readonly Dictionary<string, NotifState> _notif = new();   // key: sessionId
    static readonly SessView _legacyView = new();
    static int _interruptRR;

    // Relee solo los archivos de sesion que cambiaron (mtime) — corre cada 4 ticks.
    static void RefreshSessions()
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) { _sessViews.Clear(); return; }
            var found = new HashSet<string>();
            foreach (var f in Directory.GetFiles(SessionsDir, "*.json"))
            {
                found.Add(f);
                var mt = File.GetLastWriteTimeUtc(f);
                _sessViews.TryGetValue(f, out var v);
                if (v != null && v.Mtime == mt) continue;
                try
                {
                    var s = JsonSerializer.Deserialize(File.ReadAllText(f), JsonCtx.Default.SessionJson);
                    if (s == null) continue;
                    if (v == null) _sessViews[f] = v = new SessView();
                    v.S = s; v.Mtime = mt;
                }
                catch { }
            }
            List<string>? gone = null;
            foreach (var k in _sessViews.Keys)
                if (!found.Contains(k)) (gone ??= new()).Add(k);
            if (gone != null)
                foreach (var k in gone)
                {
                    _notif.Remove(_sessViews[k].S.SessionId ?? "");
                    _sessViews.Remove(k);
                }
        }
        catch { }
    }

    // Sesiones muertas: claude ya no corre (crash / kill) o estado viejisimo.
    static void PruneDeadSessions(long now)
    {
        List<string>? gone = null;
        foreach (var kv in _sessViews)
        {
            var s = kv.Value.S;
            bool dead = s.UpdatedAt > 0 && now - s.UpdatedAt > 6L * 3600 * 1000;
            if (!dead && s.ClaudePid > 0) dead = !PidAlive((uint)s.ClaudePid);
            if (dead) (gone ??= new()).Add(kv.Key);
        }
        if (gone == null) return;
        foreach (var f in gone)
        {
            try { File.Delete(f); } catch { }
            _notif.Remove(_sessViews[f].S.SessionId ?? "");
            _sessViews.Remove(f);
        }
    }

    static bool PidAlive(uint pid)
    {
        nint h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, 0, pid);
        if (h == 0) return false;
        try { return GetExitCodeProcess(h, out uint code) != 0 && code == 259 /*STILL_ACTIVE*/; }
        finally { CloseHandle(h); }
    }

    // Recorre las sesiones (o el state.json legacy si no hay ninguna), dispara las
    // notificaciones por sesion y devuelve el agregado para icono/tooltip.
    static Agg Aggregate(long now)
    {
        List<SessView> views;
        if (_sessViews.Count == 0)
        {
            // Fallback v0.1: hooks viejos instalados -> un solo estado mergeado.
            _legacyView.S = new SessionJson
            {
                SessionId = "_legacy",
                Status = _state.Status,
                LabelKey = _state.LabelKey,
                TurnStartedAt = _state.TurnStartedAt,
                Transcript = _state.Transcript,
                UpdatedAt = _state.UpdatedAt,
            };
            views = new List<SessView> { _legacyView };
        }
        else views = new List<SessView>(_sessViews.Values);

        // Interrupcion (Esc / permiso negado): chequeo escalonado, una sesion por ronda.
        if (_tickN % 6 == 0 && views.Count > 0)
        {
            var v = views[(_interruptRR++) % views.Count];
            var s = v.S;
            v.Interrupted = (s.Status == "thinking" || s.Status == "tool")
                && TestInterrupted(s.UpdatedAt, s.Transcript);
        }

        var a = new Agg { AllIdle = true, Total = views.Count, Tip = T("idle") };

        foreach (var v in views)
        {
            var s = v.S;
            string sid = s.SessionId ?? "";
            if (!_notif.TryGetValue(sid, out var ns)) _notif[sid] = ns = new NotifState();

            bool isRun = s.Status == "thinking" || s.Status == "tool";
            if (!isRun) v.Interrupted = false;
            bool working = isRun && !v.Interrupted;

            if (working && s.TurnStartedAt > 0) ns.LastTurnStart = s.TurnStartedAt;

            if (ns.PrevWorking && !working) // termino un turno
            {
                bool longTurn = ns.LastTurnStart > 0
                    && (now - ns.LastTurnStart) / 1000 >= _config.CompleteMinSeconds;
                if (longTurn && _config.NotifyOnComplete)
                {
                    string body = T("n_done_body").Replace("{0}", FormatElapsed(ns.LastTurnStart));
                    if (!string.IsNullOrEmpty(s.Project)) body = s.Project + " - " + body;
                    ShowNotif(T("n_done_title"), body);
                }
                if (longTurn && _config.Sound) PlayChime();
                _awaitingUserSince = now; _awayPinged = false; ns.LastTurnStart = 0;
            }
            if (working) { _awaitingUserSince = 0; _awayPinged = false; }

            // Pregunta nueva (AskUserQuestion): toast + chime, solo display.
            if (s.Question is { AskedAt: > 0 } q && q.AskedAt != ns.LastQuestionAt)
            {
                ns.LastQuestionAt = q.AskedAt;
                if (_config.NotifyOnQuestion)
                {
                    string body = q.Text ?? "";
                    if (body.Length > 180) body = body.Substring(0, 179) + "…";
                    if (!string.IsNullOrEmpty(s.Project)) body = s.Project + " - " + body;
                    ShowNotif(TL(DetectLang(q.Text), "n_q_title"), body); // titulo en el idioma de la pregunta
                    if (_config.Sound) PlayChime();
                }
            }

            // Toast de permiso solo como fallback: si hay popup pendiente, el popup avisa.
            if (s.Status == "awaiting" && ns.PrevStatus != "awaiting" && _config.NotifyOnPermission
                && !HasPendingFor(s.SessionId))
            {
                string body = T("n_perm_body");
                if (!string.IsNullOrEmpty(s.Project)) body = s.Project + " - " + body;
                ShowNotif(T("n_perm_title"), body);
            }

            ns.PrevWorking = working;
            ns.PrevStatus = s.Status ?? "idle";

            if (working)
            {
                a.AnyWorking = true; a.Working++;
                if (s.TurnStartedAt > 0 && (a.TimerStart == 0 || s.TurnStartedAt < a.TimerStart))
                    a.TimerStart = s.TurnStartedAt;
            }
            if (s.Status == "awaiting") { a.AnyAwaiting = true; a.AllIdle = false; }
            else if (working) a.AllIdle = false;
        }

        if (views.Count == 1)
        {
            var s = views[0].S;
            a.Tip = s.Status switch
            {
                "awaiting" => T("awaiting"),
                "thinking" or "tool" => views[0].Interrupted
                    ? T("idle")
                    : T(string.IsNullOrEmpty(s.LabelKey) ? "tool" : s.LabelKey!),
                _ => T("idle"),
            };
        }
        else if (a.AnyWorking) a.Tip = a.Working + "/" + a.Total + " " + T("s_working");
        else if (a.AnyAwaiting) a.Tip = T("awaiting");

        return a;
    }

    // ---------------- Terminal jump (lado tray) ----------------
    // Se llama desde un click en el panel (tenemos derechos de foreground).
    static void JumpToTerminal(SessionJson s)
    {
        nint h = (nint)s.TermHwnd;
        if (h == 0) return;
        if (IsWindow(h) == 0 || !HwndBelongsTo(h, (uint)s.TermPid))
        {
            h = ReResolveWindow((uint)s.TermPid, s.Project);
            if (h == 0) return;
            s.TermHwnd = h; // cache en memoria; el hook lo re-descubre en el proximo prompt
        }
        if (IsIconic(h) != 0) ShowWindow(h, 9 /*SW_RESTORE*/);
        if (SetForegroundWindow(h) == 0)
        {
            // Fallback clasico cuando Windows niega el foreground.
            uint tid = GetWindowThreadProcessId(h, out _);
            uint cur = GetCurrentThreadId();
            AttachThreadInput(tid, cur, 1);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            AttachThreadInput(tid, cur, 0);
        }
    }

    static bool HwndBelongsTo(nint h, uint pid)
    {
        GetWindowThreadProcessId(h, out uint p);
        return p == pid;
    }

    // El HWND quedo viejo (ventana cerrada/reabierta): buscar otra del mismo proceso.
    static nint ReResolveWindow(uint pid, string? project)
    {
        if (pid == 0 || !PidAlive(pid)) return 0;
        var wins = new Dictionary<uint, List<(nint, string)>> { [pid] = new() };
        _enumWins = wins;
        EnumWindows(&EnumWinCb, 0);
        _enumWins = null;
        var list = wins[pid];
        if (list.Count == 0) return 0;
        if (!string.IsNullOrEmpty(project))
            foreach (var w in list)
                if (w.Item2.Contains(project!, StringComparison.OrdinalIgnoreCase)) return w.Item1;
        return list[0].Item1;
    }

    // ---------------- P/Invoke (Toolhelp32 + ventanas + procesos) ----------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [DllImport("kernel32")] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern int Process32FirstW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern int Process32NextW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32")] static extern int CloseHandle(nint h);
    [DllImport("kernel32")] static extern nint OpenProcess(uint access, int inherit, uint pid);
    [DllImport("kernel32")] static extern int GetExitCodeProcess(nint h, out uint code);
    [DllImport("user32")] static extern int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> cb, nint lp);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint hwnd, char* buf, int max);
    [DllImport("user32")] static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtrW(nint hwnd, int idx);
    [DllImport("user32")] static extern int IsWindow(nint hwnd);
    [DllImport("user32")] static extern int IsIconic(nint hwnd);
    [DllImport("user32")] static extern int AttachThreadInput(uint idAttach, uint idAttachTo, int attach);
    [DllImport("user32")] static extern int BringWindowToTop(nint hwnd);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();
}
