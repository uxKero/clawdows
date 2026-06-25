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

    static void WriteAllStdout(string text)
    {
        try
        {
            nint h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == 0 || h == (nint)(-1)) return;
            var b = Encoding.UTF8.GetBytes(text);
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

    static int SessionCount(int delta, string sessionId)
    {
        string dir = Path.Combine(_sbDir, "sessions.d");
        try
        {
            Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(sessionId))
            {
                string f = Path.Combine(dir, Sanitize(sessionId));
                if (delta > 0) File.WriteAllText(f, "");
                else if (delta < 0 && File.Exists(f)) File.Delete(f);
            }
            return Directory.GetFiles(dir).Length;
        }
        catch { return 1; }
    }

    static void WriteState(StateJson st) =>
        WriteAtomic(_statePath, JsonSerializer.Serialize(st, JsonCtx.Default.StateJson));

    static bool TrayRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName()); return true; }
        catch { return false; }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
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
        var prev = ReadState() ?? new StateJson();

        var st = new StateJson
        {
            Status = "idle",
            LabelKey = "",
            TurnStartedAt = prev.TurnStartedAt,
            Transcript = p?.TranscriptPath ?? prev.Transcript ?? "",
            UpdatedAt = now,
        };

        switch (ev)
        {
            case "sessionstart":
                SessionCount(+1, sid);
                st.TurnStartedAt = 0;
                WriteState(st);
                EnsureTray();
                return 0;
            case "userpromptsubmit":
                st.Status = "thinking"; st.LabelKey = "thinking"; st.TurnStartedAt = now; break;
            case "pretool":
                st.Status = "tool"; st.LabelKey = LabelKeyForTool(p?.ToolName);
                if (st.TurnStartedAt == 0) st.TurnStartedAt = now; break;
            case "posttool":
                st.Status = "thinking"; st.LabelKey = "thinking"; break;
            case "notification":
                {
                    string m = (p?.Message ?? "").ToLowerInvariant();
                    bool perm = m.Contains("permis") || m.Contains("permiso") || m.Contains("approve")
                                || m.Contains("allow") || m.Contains("grant") || m.Contains("confirm");
                    if (perm) { st.Status = "awaiting"; st.LabelKey = "awaiting"; }
                    else { st.TurnStartedAt = 0; }
                    break;
                }
            case "stop":
                st.TurnStartedAt = 0; break;
            case "sessionend":
                if (SessionCount(-1, sid) <= 0) st.Shutdown = true;
                st.TurnStartedAt = 0; break;
            default:
                return 0;
        }
        WriteState(st);
        return 0;
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
                    var proc = Process.Start(psi)!;
                    proc.StandardInput.Write(raw);
                    proc.StandardInput.Close();
                    string outp = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    WriteAllStdout(outp);
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

    static readonly (string evt, string arg)[] HookMap =
    {
        ("SessionStart", "sessionstart"), ("UserPromptSubmit", "userpromptsubmit"),
        ("PreToolUse", "pretool"), ("PostToolUse", "posttool"),
        ("Notification", "notification"), ("Stop", "stop"), ("SessionEnd", "sessionend"),
    };

    static bool IsOurs(JsonNode? n, string exe)
    {
        if (n is not JsonObject o || o["hooks"] is not JsonArray hs) return false;
        foreach (var h in hs)
            if (h?["command"]?.GetValue<string>() is string c
                && c.Contains(exe, StringComparison.OrdinalIgnoreCase) && c.Contains(" hook"))
                return true;
        return false;
    }

    static int InstallCommand(string[] args)
    {
        string exe = ExePath();
        foreach (var dir in ConfigDirArgs(args)) InstallInto(dir, exe);
        EnsureTray();
        WriteAllStdout("Claude Status Bar instalado. Abri una ventana NUEVA de Claude Code.\r\n");
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
        foreach (var (evt, arg) in HookMap)
        {
            var arr = hooks[evt] as JsonArray ?? new JsonArray();
            for (int i = arr.Count - 1; i >= 0; i--) if (IsOurs(arr[i], exe)) arr.RemoveAt(i);
            var hk = new JsonObject(); hk["type"] = "command"; hk["command"] = "\"" + exe + "\" hook " + arg;
            var hkArr = new JsonArray();
            ((IList<JsonNode?>)hkArr).Add(hk);
            var entry = new JsonObject(); entry["hooks"] = hkArr;
            if (evt == "PreToolUse" || evt == "PostToolUse") entry["matcher"] = "*";
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
        WriteAllStdout("Claude Status Bar desinstalado.\r\n");
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
