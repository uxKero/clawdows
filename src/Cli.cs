// Sub-comandos del .exe: hook / statusline / install / uninstall.
// Hacen que un solo binario reemplace a los scripts Node del prototipo.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Threading;

internal static unsafe partial class Program
{
    static string _configDir = "", _sbDir = "";
    static Mutex? _singleInstance;

    static void ComputePaths()
    {
        _configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _sbDir = Path.Combine(_configDir, "statusbar");
        _statePath = Path.Combine(_sbDir, "state.json");
        _configPath = Path.Combine(_sbDir, "config.json");
        _usagePath = Path.Combine(_sbDir, "usage.json");
    }

    static string ExePath() => Environment.ProcessPath ?? "claude-status-bar.exe";
    static string MutexName() => "ClaudeStatusBar_" + Sanitize(_configDir);

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
        return sb.ToString();
    }

    // OJO: como el .exe es GUI subsystem (sin consola), Console.OpenStandardInput
    // devuelve vacio. Leemos/escribimos los handles estandar directo con ReadFile/
    // WriteFile, que SI funciona cuando el proceso padre redirige stdin/stdout.
    const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11;
    [DllImport("kernel32")] static extern nint GetStdHandle(int n);
    [DllImport("kernel32")] static extern int ReadFile(nint h, byte* buf, uint toRead, out uint read, nint overlapped);
    [DllImport("kernel32")] static extern int WriteFile(nint h, byte* buf, uint toWrite, out uint written, nint overlapped);

    static string ReadAllStdin()
    {
        try
        {
            nint h = GetStdHandle(STD_INPUT_HANDLE);
            if (h == 0 || h == (nint)(-1)) return "";
            using var ms = new MemoryStream();
            var buf = new byte[8192];
            fixed (byte* p = buf)
            {
                while (true)
                {
                    if (ReadFile(h, p, (uint)buf.Length, out uint read, 0) == 0 || read == 0) break;
                    ms.Write(buf, 0, (int)read);
                }
            }
            var bytes = ms.ToArray();
            int off = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0; // saltar BOM
            return Encoding.UTF8.GetString(bytes, off, bytes.Length - off).Trim();
        }
        catch { return ""; }
    }

    static void WriteAllStdout(string text) => WriteBytesStdout(Encoding.UTF8.GetBytes(text));

    static void WriteBytesStdout(byte[] b)
    {
        try
        {
            nint h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == 0 || h == (nint)(-1) || b.Length == 0) return;
            fixed (byte* p = b) WriteFile(h, p, (uint)b.Length, out _, 0);
        }
        catch { }
    }

    static void WriteAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { }
    }

    // ================= hook =================
    static string LabelKeyForTool(string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return "tool";
        if (tool.StartsWith("mcp__", StringComparison.Ordinal)) return "mcp";
        return tool switch
        {
            "AskUserQuestion" => "question",
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "edit",
            "Read" or "NotebookRead" => "read",
            "Bash" or "BashOutput" or "PowerShell" or "KillShell" => "bash",
            "Grep" or "Glob" or "LS" => "search",
            "WebFetch" or "WebSearch" => "web",
            "Task" => "task",
            "TodoWrite" or "ExitPlanMode" => "planning",
            _ => "tool",
        };
    }

    static void WriteState(StateJson st) =>
        WriteAtomic(_statePath, JsonSerializer.Serialize(st, JsonCtx.Default.StateJson));

    // state.json mergeado (last-writer-wins): compat con v0.1 y canal del Shutdown.
    static void WriteMergedState(SessionJson s, bool shutdown) => WriteState(new StateJson
    {
        Status = s.Status,
        LabelKey = s.LabelKey,
        TurnStartedAt = s.TurnStartedAt,
        Transcript = s.Transcript ?? "",
        UpdatedAt = s.UpdatedAt,
        Shutdown = shutdown,
    });

    static bool TrayRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName()); return true; }
        catch { return false; }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    // Ubica el bin de Git Bash (preferido sobre el bash de WSL para statuslines `bash ...`).
    static string? FindGitBin()
    {
        string[] cands =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin"),
        };
        foreach (var d in cands)
            if (File.Exists(Path.Combine(d, "bash.exe"))) return d;
        return null;
    }

    static void EnsureTray()
    {
        try
        {
            if (TrayRunning()) return;
            Process.Start(new ProcessStartInfo { FileName = ExePath(), UseShellExecute = true });
        }
        catch { }
    }

    static int HookCommand(string ev)
    {
        ev = ev.ToLowerInvariant();
        HookInput? p = null;
        try { p = JsonSerializer.Deserialize(ReadAllStdin(), JsonCtx.Default.HookInput); } catch { }
        string sid = p?.SessionId ?? Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") ?? "";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool haveSid = sid.Length > 0;

        // Estado de ESTA sesion (sessions.d\<sid>.json); el mergeado se escribe igual.
        var ss = (haveSid ? ReadSession(sid) : null) ?? new SessionJson();
        ss.SessionId = sid;
        ss.UpdatedAt = now;
        if (p?.TranscriptPath is { Length: > 0 } tp) ss.Transcript = tp;
        if (p?.Cwd is { Length: > 0 } cw) { ss.Cwd = cw; ss.Project = ProjectName(cw); }

        switch (ev)
        {
            case "permission":
                return PermissionCommand(p); // handshake GUI (no toca ss aca)
            case "sessionstart":
                CleanLegacyMarkers();
                ss.Status = "idle"; ss.LabelKey = ""; ss.TurnStartedAt = 0;
                DiscoverTerminal(ss);
                break;
            case "userpromptsubmit":
                ss.Status = "thinking"; ss.LabelKey = "thinking"; ss.TurnStartedAt = now;
                ss.Question = null;
                if (ss.TermPid == 0) DiscoverTerminal(ss); // sesion resumida por hooks viejos
                break;
            case "pretool":
                ss.Status = "tool"; ss.LabelKey = LabelKeyForTool(p?.ToolName);
                if (ss.TurnStartedAt == 0) ss.TurnStartedAt = now;
                if (p?.ToolName == "AskUserQuestion")
                {
                    LoadConfig();
                    ss.Question = ParseQuestion(p.ToolInput, now);
                    if (haveSid) WriteSession(ss);
                    WriteMergedState(ss, false);
                    return QuestionCommand(p, ss); // bloquea esperando la respuesta del popup
                }
                break;
            case "posttool":
                ss.Status = "thinking"; ss.LabelKey = "thinking";
                ss.Question = null;
                break;
            case "notification":
                {
                    string m = (p?.Message ?? "").ToLowerInvariant();
                    bool perm = m.Contains("permis") || m.Contains("permiso") || m.Contains("approve")
                                || m.Contains("allow") || m.Contains("grant") || m.Contains("confirm");
                    if (perm) { ss.Status = "awaiting"; ss.LabelKey = "awaiting"; }
                    else { ss.Status = "idle"; ss.LabelKey = ""; ss.TurnStartedAt = 0; }
                    break;
                }
            case "stop":
                ss.Status = "idle"; ss.LabelKey = ""; ss.TurnStartedAt = 0;
                ss.Question = null;
                break;
            case "sessionend":
                {
                    if (haveSid) DeleteSession(sid);
                    ss.Status = "idle"; ss.LabelKey = ""; ss.TurnStartedAt = 0;
                    WriteMergedState(ss, shutdown: CountSessions() <= 0);
                    return 0;
                }
            default:
                return 0;
        }

        if (haveSid) WriteSession(ss);
        WriteMergedState(ss, shutdown: false);
        if (ev == "sessionstart") EnsureTray();
        return 0;
    }

    // ================= permission (handshake GUI) =================
    // El hook PermissionRequest de Claude Code espera nuestro stdout para decidir.
    // Escribimos un request file, el tray muestra Allow/Deny y deja un decision file.
    // Sin decision (timeout / tray apagado / passthrough) -> exit 0 sin output y
    // Claude Code muestra su prompt normal en la terminal. Nunca podemos romper el flujo.
    static string RequestsDir => Path.Combine(_sbDir, "requests");

    static int PermissionCommand(HookInput? p)
    {
        try
        {
            if (p == null) return 0;
            LoadConfig();
            string tool = p.ToolName ?? "";
            bool plan = tool == "ExitPlanMode";
            if (plan ? !_config.GuiPlanReview : !_config.GuiApprovals) return 0;
            if (!TrayRunning()) return 0;

            string sid = p.SessionId ?? "";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int timeoutS = plan
                ? Math.Clamp(_config.PlanTimeoutSeconds, 30, 570)
                : Math.Clamp(_config.PermissionTimeoutSeconds, 10, 300);

            var ss = sid.Length > 0 ? ReadSession(sid) : null;
            var req = new ReqJson
            {
                Id = Sanitize(sid) + "-" + now + "-" + Environment.ProcessId,
                SessionId = sid,
                Project = p.Cwd is { Length: > 0 } cw ? ProjectName(cw) : ss?.Project,
                Tool = tool,
                Kind = plan ? "plan" : "permission",
                Summary = BuildSummary(tool, p.ToolInput),
                Plan = plan ? PlanText(p.ToolInput) : null,
                CreatedAt = now,
                ExpiresAt = now + timeoutS * 1000L,
                HookPid = Environment.ProcessId,
            };
            string reqPath = Path.Combine(RequestsDir, req.Id + ".req.json");
            string decPath = Path.Combine(RequestsDir, req.Id + ".decision.json");

            try
            {
                // Primero el request (el tray lo ve y muestra el popup), despues la sesion
                // en "awaiting" — asi el toast fallback ve el pendiente y no duplica aviso.
                WriteAtomic(reqPath, JsonSerializer.Serialize(req, JsonCtx.Default.ReqJson));
                if (ss != null)
                {
                    ss.Status = "awaiting"; ss.LabelKey = "awaiting"; ss.UpdatedAt = now;
                    WriteSession(ss); WriteMergedState(ss, false);
                }
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < req.ExpiresAt)
                {
                    if (File.Exists(decPath))
                    {
                        DecisionJson? d = null;
                        try { d = JsonSerializer.Deserialize(File.ReadAllText(decPath), JsonCtx.Default.DecisionJson); } catch { }
                        string b = d?.Behavior ?? "";
                        if (b is "allow" or "deny")
                            WriteAllStdout("{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"" + b + "\"}}}");
                        return 0; // passthrough / desconocido -> prompt normal en terminal
                    }
                    Thread.Sleep(150);
                }
            }
            finally
            {
                // El hook es dueño de sus archivos: los borra en todo camino de salida.
                try { File.Delete(reqPath); } catch { }
                try { File.Delete(decPath); } catch { }
                if (ss != null)
                {
                    ss.Status = "tool"; ss.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    WriteSession(ss); WriteMergedState(ss, false);
                }
            }
        }
        catch { }
        return 0;
    }

    // Resumen humano del tool_input para el popup (max ~5 lineas cortas).
    static string[] BuildSummary(string tool, JsonElement input)
    {
        string text = "";
        try
        {
            if (input.ValueKind == JsonValueKind.Object)
            {
                string? Get(string n) => input.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                text = tool switch
                {
                    "Bash" or "PowerShell" => Get("command") ?? "",
                    "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "Read" => Get("file_path") ?? "",
                    "WebFetch" => Get("url") ?? "",
                    "WebSearch" => Get("query") ?? "",
                    _ => "",
                } ?? "";
                if (text.Length == 0)
                    foreach (var prop in input.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } sv)
                        { text = sv; break; }
                if (text.Length == 0) text = input.GetRawText();
            }
            else if (input.ValueKind != JsonValueKind.Undefined) text = input.GetRawText();
        }
        catch { }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (text.Length > 200) text = text.Substring(0, 199) + "…";

        // Cortar en lineas de ~44 chars (ancho del popup) — max 5.
        var lines = new List<string>();
        for (int i = 0; i < text.Length && lines.Count < 5; i += 44)
            lines.Add(text.Substring(i, Math.Min(44, text.Length - i)));
        if (lines.Count == 0) lines.Add("");
        return lines.ToArray();
    }

    // AskUserQuestion: todas las preguntas (max 4) con sus opciones (max 4 c/u).
    static QuestionJson[] ParseQuestions(JsonElement input, long now)
    {
        var list = new List<QuestionJson>();
        try
        {
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array) return list.ToArray();
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object) continue;
                string? text = q.TryGetProperty("question", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.IsNullOrEmpty(text)) continue;
                var opts = new List<string>();
                if (q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                    foreach (var o in os.EnumerateArray())
                    {
                        if (o.ValueKind == JsonValueKind.String) opts.Add(o.GetString() ?? "");
                        else if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("label", out var l)
                                 && l.ValueKind == JsonValueKind.String) opts.Add(l.GetString() ?? "");
                        if (opts.Count >= 4) break;
                    }
                bool multi = q.TryGetProperty("multiSelect", out var ms) && ms.ValueKind == JsonValueKind.True;
                list.Add(new QuestionJson { Text = text, Options = opts.ToArray(), MultiSelect = multi, AskedAt = now });
                if (list.Count >= 4) break;
            }
        }
        catch { }
        return list.ToArray();
    }

    static QuestionJson? ParseQuestion(JsonElement input, long now)
    {
        var all = ParseQuestions(input, now);
        return all.Length > 0 ? all[0] : null;
    }

    // Handshake de preguntas: el popup muestra la pregunta y las opciones; al elegir,
    // PreToolUse DENIEGA el AskUserQuestion con la respuesta como motivo -> Claude la
    // recibe como feedback y continua sin re-preguntar. Sin respuesta -> terminal.
    static int QuestionCommand(HookInput p, SessionJson? ss)
    {
        try
        {
            if (!_config.GuiQuestions || !TrayRunning()) return 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var questions = ParseQuestions(p.ToolInput, now);
            if (questions.Length == 0) return 0;

            string sid = p.SessionId ?? "";
            int timeoutS = Math.Clamp(_config.QuestionTimeoutSeconds, 10, 570);
            var req = new ReqJson
            {
                Id = Sanitize(sid) + "-" + now + "-" + Environment.ProcessId,
                SessionId = sid,
                Project = p.Cwd is { Length: > 0 } cw ? ProjectName(cw) : ss?.Project,
                Tool = "AskUserQuestion",
                Kind = "question",
                Summary = BuildSummary("AskUserQuestion", p.ToolInput),
                Questions = questions,
                CreatedAt = now,
                ExpiresAt = now + timeoutS * 1000L,
                HookPid = Environment.ProcessId,
            };
            string reqPath = Path.Combine(RequestsDir, req.Id + ".req.json");
            string decPath = Path.Combine(RequestsDir, req.Id + ".decision.json");
            bool answered = false;
            try
            {
                WriteAtomic(reqPath, JsonSerializer.Serialize(req, JsonCtx.Default.ReqJson));
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < req.ExpiresAt)
                {
                    if (File.Exists(decPath))
                    {
                        DecisionJson? d = null;
                        try { d = JsonSerializer.Deserialize(File.ReadAllText(decPath), JsonCtx.Default.DecisionJson); } catch { }
                        if (d?.Behavior == "answer" && d.Answers is { Length: > 0 } ans)
                        {
                            answered = true;
                            var sb = new StringBuilder();
                            sb.Append("The user already answered THIS question set via the Claude Status Bar popup (GUI). ");
                            for (int i = 0; i < ans.Length && i < questions.Length; i++)
                                sb.Append("Question: \"").Append(questions[i].Text).Append("\" -> Answer: \"").Append(ans[i]).Append("\". ");
                            sb.Append("Treat these as the user's real answers and continue; do not repeat these exact questions. ");
                            sb.Append("You may still use AskUserQuestion later for any NEW questions.");
                            string reason = JsonSerializer.Serialize(sb.ToString(), JsonCtx.Default.String);
                            WriteAllStdout("{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":" + reason + "}}");
                        }
                        return 0; // passthrough / desconocido -> pregunta normal en terminal
                    }
                    Thread.Sleep(150);
                }
            }
            finally
            {
                try { File.Delete(reqPath); } catch { }
                try { File.Delete(decPath); } catch { }
                if (answered && ss != null)
                {
                    ss.Question = null; ss.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    WriteSession(ss); // ya respondida: que no quede colgada en el panel
                }
            }
        }
        catch { }
        return 0;
    }

    static string? PlanText(JsonElement input)
    {
        try
        {
            if (input.ValueKind == JsonValueKind.Object)
            {
                if (input.TryGetProperty("plan", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
                return input.GetRawText();
            }
        }
        catch { }
        return null;
    }

    // ================= statusline =================
    static int? Round(double? v) => v.HasValue ? (int)Math.Round(v.Value) : null;

    static int StatuslineCommand()
    {
        string raw = ReadAllStdin();
        try
        {
            var p = JsonSerializer.Deserialize(raw, JsonCtx.Default.StatuslineInput);
            if (p != null)
            {
                var u = new UsageJson
                {
                    CtxPct = Round(p.ContextWindow?.UsedPercentage),
                    FiveHourPct = Round(p.RateLimits?.FiveHour?.UsedPercentage),
                    SevenDayPct = Round(p.RateLimits?.SevenDay?.UsedPercentage),
                };
                WriteAtomic(_usagePath, JsonSerializer.Serialize(u, JsonCtx.Default.UsageJson));
            }
        }
        catch { }

        // reenviar a la statusline original (si la guardamos al instalar)
        try
        {
            string origFile = Path.Combine(_sbDir, "statusline-original.txt");
            if (File.Exists(origFile))
            {
                string cmd = File.ReadAllText(origFile).Trim();
                if (cmd.Length > 0)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + cmd,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    // Para statuslines basadas en `bash`: anteponer Git Bash al PATH del hijo,
                    // si no `bash` resuelve al de WSL (que suele fallar). Claude Code usa Git Bash.
                    string? gitBin = FindGitBin();
                    if (gitBin != null)
                        psi.Environment["PATH"] = gitBin + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
                    var proc = Process.Start(psi)!;
                    // Passthrough de BYTES (sin recodificar) para no corromper ANSI/UTF-8 de tu barra.
                    var inBytes = Encoding.UTF8.GetBytes(raw);
                    proc.StandardInput.BaseStream.Write(inBytes, 0, inBytes.Length);
                    proc.StandardInput.BaseStream.Flush();
                    proc.StandardInput.BaseStream.Close();
                    using var outMs = new MemoryStream();
                    proc.StandardOutput.BaseStream.CopyTo(outMs);
                    proc.WaitForExit();
                    WriteBytesStdout(outMs.ToArray());
                }
            }
        }
        catch { }
        return 0;
    }

    // ================= install / uninstall =================
    static List<string> ConfigDirArgs(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--config-dir" && i + 1 < args.Length) list.Add(args[++i]);
        if (list.Count == 0) list.Add(_configDir);
        return list;
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
    static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly (string evt, string arg, string? matcher, int timeout)[] HookMap =
    {
        ("SessionStart", "sessionstart", null, 0), ("UserPromptSubmit", "userpromptsubmit", null, 0),
        ("PreToolUse", "pretool", "*", 0), ("PostToolUse", "posttool", "*", 0),
        ("Notification", "notification", null, 0), ("Stop", "stop", null, 0), ("SessionEnd", "sessionend", null, 0),
        // timeout largo: el subcomando corta solo antes (PermissionTimeoutSeconds / PlanTimeoutSeconds)
        ("PermissionRequest", "permission", "*", 600),
    };

    static bool IsOurs(JsonNode? n, string exe)
    {
        if (n is not JsonObject o || o["hooks"] is not JsonArray hs) return false;
        foreach (var h in hs)
            if (h?["command"]?.GetValue<string>() is string c && c.Contains(" hook")
                && (c.Contains(exe, StringComparison.OrdinalIgnoreCase)
                    || c.Contains("clawdows", StringComparison.OrdinalIgnoreCase)
                    || c.Contains("claude-status-bar", StringComparison.OrdinalIgnoreCase))) // migracion del nombre viejo
                return true;
        return false;
    }

    static int InstallCommand(string[] args)
    {
        string exe = ExePath();
        foreach (var dir in ConfigDirArgs(args)) InstallInto(dir, exe);
        EnsureTray();
        WriteAllStdout("Clawdows instalado. Abri una ventana NUEVA de Claude Code.\r\n");
        return 0;
    }

    static void InstallInto(string dir, string exe)
    {
        string settingsPath = Path.Combine(dir, "settings.json");
        string sbForDir = Path.Combine(dir, "statusbar");
        if (!Directory.Exists(dir)) return;

        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject(); }
        catch { root = new JsonObject(); }
        if (File.Exists(settingsPath)) File.Copy(settingsPath, settingsPath + ".bak-statusbar-" + Stamp(), true);

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var (evt, arg, matcher, timeout) in HookMap)
        {
            var arr = hooks[evt] as JsonArray ?? new JsonArray();
            for (int i = arr.Count - 1; i >= 0; i--) if (IsOurs(arr[i], exe)) arr.RemoveAt(i);
            var hk = new JsonObject(); hk["type"] = "command"; hk["command"] = "\"" + exe + "\" hook " + arg;
            if (timeout > 0) hk["timeout"] = timeout;
            var hkArr = new JsonArray();
            ((IList<JsonNode?>)hkArr).Add(hk);
            var entry = new JsonObject(); entry["hooks"] = hkArr;
            if (matcher != null) entry["matcher"] = matcher;
            ((IList<JsonNode?>)arr).Add(entry);
            hooks[evt] = arr;
        }

        // statusline: preservar la original y enchufar la nuestra (uso ctx/5h/7d)
        string slCmd = "\"" + exe + "\" statusline";
        var sl = root["statusLine"] as JsonObject;
        string? existing = sl?["command"]?.GetValue<string>();
        if (existing != null && !existing.Contains("\" statusline"))
        {
            Directory.CreateDirectory(sbForDir);
            File.WriteAllText(Path.Combine(sbForDir, "statusline-original.txt"), existing);
        }
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = slCmd };

        File.WriteAllText(settingsPath, root.ToJsonString(PrettyJson) + "\n");
    }

    static int UninstallCommand(string[] args)
    {
        string exe = ExePath();
        foreach (var dir in ConfigDirArgs(args)) UninstallFrom(dir, exe);
        WriteAllStdout("Clawdows desinstalado.\r\n");
        return 0;
    }

    static void UninstallFrom(string dir, string exe)
    {
        string settingsPath = Path.Combine(dir, "settings.json");
        if (!File.Exists(settingsPath)) return;
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject(); }
        catch { return; }
        File.Copy(settingsPath, settingsPath + ".bak-statusbar-" + Stamp(), true);

        if (root["hooks"] is JsonObject hooks)
        {
            var keys = new List<string>();
            foreach (var kv in hooks) keys.Add(kv.Key);
            foreach (var evt in keys)
            {
                if (hooks[evt] is not JsonArray arr) continue;
                for (int i = arr.Count - 1; i >= 0; i--) if (IsOurs(arr[i], exe)) arr.RemoveAt(i);
                if (arr.Count == 0) hooks.Remove(evt);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        }

        // restaurar statusline original (si la habiamos reemplazado)
        var sl = root["statusLine"] as JsonObject;
        if (sl?["command"]?.GetValue<string>() is string c && c.Contains("\" statusline"))
        {
            string origFile = Path.Combine(dir, "statusbar", "statusline-original.txt");
            if (File.Exists(origFile))
                root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = File.ReadAllText(origFile).Trim() };
            else
                root.Remove("statusLine");
        }

        File.WriteAllText(settingsPath, root.ToJsonString(PrettyJson) + "\n");
    }

    // ================= modelos JSON extra =================
    class HookInput
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
        [JsonPropertyName("transcript_path")] public string? TranscriptPath { get; set; }
        [JsonPropertyName("cwd")] public string? Cwd { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("tool_input")] public JsonElement ToolInput { get; set; }
    }

    // Request de aprobacion pendiente (requests\<id>.req.json) y su decision.
    class ReqJson
    {
        public string? Id { get; set; }
        public string? SessionId { get; set; }
        public string? Project { get; set; }
        public string? Tool { get; set; }
        public string? Kind { get; set; }   // permission | plan | question
        public string[]? Summary { get; set; }
        public string? Plan { get; set; }
        public QuestionJson[]? Questions { get; set; }
        public long CreatedAt { get; set; }
        public long ExpiresAt { get; set; }
        public int HookPid { get; set; }
    }
    class DecisionJson
    {
        public string? Behavior { get; set; } // allow | deny | passthrough | answer
        public string[]? Answers { get; set; } // para behavior=answer (una por pregunta)
        public long DecidedAt { get; set; }
    }
    class StatuslineInput
    {
        [JsonPropertyName("context_window")] public CtxWin? ContextWindow { get; set; }
        [JsonPropertyName("rate_limits")] public RateLimits? RateLimits { get; set; }
    }
    class CtxWin { [JsonPropertyName("used_percentage")] public double? UsedPercentage { get; set; } }
    class RateLimits
    {
        [JsonPropertyName("five_hour")] public RateWin? FiveHour { get; set; }
        [JsonPropertyName("seven_day")] public RateWin? SevenDay { get; set; }
    }
    class RateWin { [JsonPropertyName("used_percentage")] public double? UsedPercentage { get; set; } }
}
