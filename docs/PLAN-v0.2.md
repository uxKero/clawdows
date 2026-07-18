# Claude Status Bar v0.2 — Features estilo VibeIsland

> **Estado (2026-07-17): IMPLEMENTADO.** Las 5 fases estan completas y desplegadas.
> Nota de implementacion: los payloads de hooks con `tool_input` requieren registrar
> `JsonElement` en el `JsonSerializerContext` (si no, el parse falla silencioso).
> El unico punto que se valida en uso real (no simulable headless) es la semantica
> exacta de `PermissionRequest` en sesion interactiva; el diseño degrada al prompt
> de terminal si algo difiere.

## Contexto

VibeIsland (vibeisland.app, macOS) permite **aprobar permisos, revisar planes y saltar a la terminal** desde un panel nativo. Objetivo: llevar esas features a claude-bar (Windows, tray Win32 nativo AOT ~3 MB, sin frameworks), solo Claude Code por ahora.

**Hallazgo clave (verificado en docs oficiales):** el hook **`PermissionRequest`** de Claude Code se dispara cuando aparece un diálogo de permiso y su stdout puede decidirlo:
`{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"|"deny"}}}`.
Soporta `matcher` y `timeout` por hook (default 600 s). Esto habilita Allow/Deny en GUI **sin inyección de teclado**. Límites conocidos: `AskUserQuestion` no se puede responder desde hooks (solo mostrar + saltar a terminal), y los payloads no traen info de terminal → se descubre caminando la cadena de procesos padre del hook.

**Alcance elegido por el usuario:** aprobación de permisos GUI (popup flotante), dashboard multi-sesión, terminal jump, revisión de planes, + bonus: mostrar preguntas de AskUserQuestion.

**Principio de diseño:** archivos como único IPC (procesos hook ↔ tray), escrituras atómicas (`WriteAtomic` ya existe), el lado hook siempre falla en silencio (exit 0 sin output) → Claude Code cae al prompt normal de terminal. Nada puede romper el flujo actual.

## Archivos críticos

- `src/Cli.cs` — rewrite de `HookCommand`, nuevo subcomando `hook permission` (handshake), caminata de procesos padre, cambios en `InstallCommand`/`HookMap`
- `src/Program.cs` — modelos `SessionJson`/`ReqJson` + `JsonCtx`, agregación en `Tick`, notificaciones por sesión, escaneo de requests, nuevos P/Invoke
- `src/Panel.cs` — refactor a layout-list, filas de sesión, subvista "Aprobaciones"
- `src/Approve.cs` (nuevo) — ventana de aprobación/plan reutilizando los helpers GDI de Panel
- `src/assets/strings.json` — ~20 claves nuevas × 3 idiomas (en/es/zh)
- `src/ClaudeStatusBar.csproj` — solo si se agrega `Approve.cs`

## Esquemas de estado (en `%CLAUDE_CONFIG_DIR%\statusbar\`)

### `sessions.d\<sid>.json` — estado por sesión (reemplaza los markers vacíos)
`{ sessionId, status, labelKey, turnStartedAt, transcript, cwd, project, claudePid, termPid, termHwnd, termExe, updatedAt, question? }`
- `project` = nombre de carpeta del cwd (lo calcula el hook).
- `claudePid` para detectar crash (OpenProcess + STILL_ACTIVE); `termHwnd` validado con `IsWindow` antes de saltar.
- `HookCommand` escribe el archivo de sesión **y** sigue escribiendo el `state.json` mergeado de hoy (compat con exe viejo, `Shutdown`, statusline). `SessionCount` pasa a contar `*.json`; los markers legacy se borran al arrancar.

### `requests\<sid>-<unixMs>-<hookPid>.req.json` — aprobación pendiente
`{ id, sessionId, project, tool, kind: "permission"|"plan", summary[], plan?, createdAt, expiresAt, hookPid }`
- `summary`: hasta ~6 líneas legibles según tool (Bash → command truncado, Edit/Write → file_path, WebFetch → url, mcp__ → nombre, fallback → JSON compactado).
- `plan` solo cuando `tool == ExitPlanMode`; `tool_input` se parsea como `JsonElement` (AOT-safe, defensivo).

### `requests\<base>.decision.json` — respuesta del tray
`{ behavior: "allow"|"deny"|"passthrough", decidedAt }`
- El **hook** es dueño de ambos archivos: los borra en `finally` en todo camino de salida. El tray solo limpia huérfanos (startup >10 min, o `hookPid` muerto → Esc/kill).

### `config.json` — nuevas opciones
`GuiApprovals=true, GuiPlanReview=true, PermissionTimeoutSeconds=60, PlanTimeoutSeconds=300, NotifyOnQuestion=true`
(timeouts < 600 s del hook para que el fallback al prompt de terminal siempre gane).

## Wiring de hooks (`InstallCommand`)

`HookMap` pasa a `(evt, arg, matcher?, timeout?)` y agrega:
`("PermissionRequest", "permission", "*", 600)` — una sola entrada; el subcomando distingue internamente `ExitPlanMode` (kind=plan) del resto. `IsOurs`/uninstall ya funcionan sin cambios; re-instalar sobre versión vieja migra limpio.

## Identificación de terminal (hook, en `sessionstart`) — agnóstica: cualquier terminal o IDE

El mecanismo **no usa whitelist de terminales**: encuentra el ancestro más cercano del hook que posea una ventana visible top-level. Eso cubre automáticamente Windows Terminal, conhost/cmd/pwsh standalone, Alacritty, WezTerm, mintty (git-bash), y las terminales integradas de IDEs (VS Code → `Code.exe`, Cursor, Windsurf, JetBrains → `idea64.exe`/`rider64.exe`, etc.): la cadena de padres del shell embebido siempre llega al proceso principal del IDE, que es dueño de la ventana.

1. `CreateToolhelp32Snapshot` → mapa pid→(ppid, exe) en una pasada.
2. Subir desde el propio PID (cap 15 saltos, guarda anti-ciclos): `claude-status-bar ← cmd ← node/claude ← shell ← [ptyhost/helper sin ventana…] ← proceso con ventana (terminal o IDE)`.
3. `EnumWindows` + `GetWindowThreadProcessId` → ancestro más cercano con ventana visible top-level (sin `WS_EX_TOOLWINDOW`, con título) → `termPid/termHwnd/termExe`; guardar también `claudePid`.
4. **IDE/proceso con varias ventanas top-level** (p.ej. dos ventanas de VS Code sobre el mismo `Code.exe`): elegir la ventana cuyo título contenga el nombre de la carpeta del proyecto (`project` del cwd); si ninguna matchea, la primera en Z-order (`GetTopWindow`/orden de EnumWindows). Guardar también `project` para poder re-resolver en el jump si el HWND quedó stale.
5. Sin ventana encontrada → fila sin botón de jump (degradación, no error).

Los procesos intermedios sin ventana (ptyhost de VS Code, conhost oculto de ConPTY, winpty) no rompen la caminata: simplemente se saltan porque no tienen ventana visible.

**Jump (tray):** click en panel da derechos de foreground → `SW_RESTORE` si minimizada + `SetForegroundWindow`; fallback `AttachThreadInput` + `BringWindowToTop`. Limitación documentada: no se puede elegir la pestaña exacta de Windows Terminal, solo la ventana.

## Handshake de permisos (`hook permission`)

1. Parse stdin; si `!GuiApprovals` o tray no corre (mutex) → exit 0 silencioso (prompt normal).
2. Marca sesión `awaiting` (más confiable que el sniffing actual del texto de Notification).
3. Escribe `.req.json` y pollea `.decision.json` cada 150 ms hasta `expiresAt`.
4. allow/deny → imprime el JSON de decisión; passthrough/timeout → exit 0 sin output → prompt de terminal.
5. `finally`: borra sus archivos.

**Tray (en `Tick`, cada ~240 ms):** diff del dir `requests\` contra `_pending` en memoria. Nuevo → toast + chime + popup. Desaparecido → cerrar popup. `hookPid` muerto → limpiar + cerrar. Varias requests concurrentes: popup muestra de a una con header "1 / 3"; el panel además marca cada sesión con chip "Revisar".

## Ventana de aprobación / plan (`ClaudeStatusBarApprove`)

Una sola window class, un HWND oculto reutilizado, dos modos:
- **Compacto (permiso):** ~360 px, popup redondeado estilo panel, abajo-derecha sobre el tray. Clawd + título + proyecto, tool en acento, líneas de summary, botones **Allow** (rect redondeado acento) / **Deny** / link "Responder en terminal (42s)" con countdown desde `expiresAt` (repinta el tick de 80 ms).
- **Plan:** 560×640 clampeado al work area, arrastrable (`WM_NCHITTEST` → `HTCAPTION` en banda superior), Esc = passthrough. Markdown-lite con GDI: `#/##/###` → fuentes de heading, `-`/`*`/`1.` → bullet con punto acento, fences ``` → bloque con fondo oscuro y Consolas; medición con `DrawTextW(DT_CALCRECT)`; scroll manual `_scrollY` vía `WM_MOUSEWHEEL` + scrollbar fino de 4 px. Se quitan marcadores `**`/backticks en v1. Botones **Aprobar plan** / **Rechazar** / **Abrir en terminal**.
- Se muestra con `SWP_SHOWWINDOW | SWP_NOACTIVATE` (no roba foco); no se oculta al perder foco, persiste hasta decisión/expiración.
- **DPI:** mantener sin manifiesto DPI (bitmap-stretch consistente con el panel actual); per-monitor DPI queda como follow-up.

## Preguntas (AskUserQuestion)

En `hook pretool`: si `tool_name == AskUserQuestion`, guardar primera pregunta + hasta 4 opciones en `session.question`, `labelKey = "question"`. Toast (gated por `NotifyOnQuestion`). Se limpia en posttool/userpromptsubmit/stop/sessionend. Panel: la fila de sesión muestra la pregunta (2 líneas máx, acento) + jump. Solo display.

## Tick / agregación (`Program.cs`)

- Cada tick: `state.json` solo para `Shutdown`.
- Cada 4 ticks: enumerar `sessions.d\*.json` con caché por `LastWriteTimeUtc` (solo re-deserializa cambiados). Si está vacío → fallback al `state.json` legacy (compat total con hooks viejos).
- `TestInterrupted` por sesión, escalonado; sesión muerta (`claudePid`) → borrar archivo.
- Agregado: `working = any(...)` → animación igual que hoy. Tooltip 1 sesión = formato actual; N > 1 = `"Claude - 2/3 trabajando"` + timer del turno más largo + uso (cap 127 chars, uso al final).
- Notificaciones por sesión: `Dictionary<sessionId, NotifState>` reemplaza los escalares `_prevWorking/etc.`; toast de "listo" incluye el proyecto. Away ping solo cuando TODAS idle. Toast de permiso viejo queda como fallback cuando `GuiApprovals` off.
- Sweep al arrancar: markers legacy, requests >10 min, `*.tmp`.

## Panel (`Panel.cs`)

Refactor de `int[] Items` fijo a **layout-list**: `BuildLayout()` devuelve `List<PanelItem>{kind, rect, arg}` usada por paint y hit-test (las filas de sesión tienen altura variable).
- `K_SESSION` (40 px, 2 líneas): proyecto + dot de estado (acento=working, amarillo=awaiting, gris=idle); línea 2 estado + elapsed; derecha `↗` jump o chip "Revisar" si hay request pendiente (click → abre popup en vez de saltar). +18 px si hay pregunta. Cap 5 filas + "+N más". 0 sesiones → línea muted.
- Nueva subvista **Aprobaciones** (view 3): toggles GuiApprovals / GuiPlanReview / NotifyOnQuestion (timeouts solo por config.json en v1).
- Orden vista principal: header → sesiones → Idioma → Notificaciones → Aprobaciones → Timer → Uso → Info → Salir. `PANEL_W` 270 → 300.

## i18n

~20 claves nuevas en en/es/zh: `question, s_sessions, s_none, s_working, s_review, s_more, apr_title, apr_allow, apr_deny, apr_terminal, apr_count, plan_title, plan_approve, plan_reject, plan_open, m_approvals, m_gui_approvals, m_plan_review, m_notif_question, n_q_title, n_done_body_proj`.

## Entregable inmediato tras aprobar

Copiar este plan al repo como `docs/PLAN-v0.2.md` (pedido del usuario), para que quede versionado y sirva de referencia durante la implementación.

## Fases de construcción

**Fase 0 — experimentos (subcomando throwaway `hook debuglog` que loguea stdin + cadena de padres):**
1. Confirmar que `PermissionRequest` dispara, bloquea el diálogo de terminal mientras corre, y acepta el JSON documentado (allow, deny, sin-output).
2. Confirmar comportamiento de timeout (¿cae al prompt o auto-deny?).
3. Loguear `tool_input` de `ExitPlanMode` (¿`.plan`?) y `AskUserQuestion` (shape de questions/options).
4. Verificar cadena de padres bajo Windows Terminal, VS Code (terminal integrada), conhost/cmd standalone y git-bash (mintty); si hay otro IDE a mano (Cursor/JetBrains), probarlo también. Incluir el caso de 2 ventanas del mismo IDE abiertas → confirmar que la heurística por título elige la correcta.

**Fase 1 — estado por sesión:** `SessionJson`, rewrite `HookCommand`, caminata de padres, migración de markers, agregación con fallback. *Verificar:* 2 terminales con Claude → el tray camina si cualquiera trabaja; matar un claude → su sesión desaparece en ~5 s; statusline sigue funcionando.

**Fase 2 — panel multi-sesión + jump:** refactor layout-list, filas, click-jump. *Verificar:* levanta la ventana correcta desde minimizada y desde atrás; fila sin `termHwnd` se degrada bien.

**Fase 3 — handshake de permisos + popup compacto:** subcomando, entrada `PermissionRequest` en install, protocolo req/decision, popup, toggles, cola de concurrentes. *Verificar:* allow/deny desde popup gobierna el tool real; "responder en terminal" y timeout caen al prompt; 2 sesiones pidiendo a la vez; Esc con request pendiente limpia; toggle off = comportamiento v0.1 exacto.

**Fase 4 — ventana de plan:** markdown-lite, scroll, drag. *Verificar:* plan real de 200+ líneas con fences y texto CJK.

**Fase 5 — preguntas + pulido:** AskUserQuestion, i18n es/zh completo, chequeo de tamaño de binario (esperado +30–60 KB), README, bump a v0.2.

Cada fase es útil por sí sola y no rompe el contrato en disco de la anterior.

## Riesgos (se resuelven en Fase 0)

1. Semántica exacta de `PermissionRequest` (¿bloquea el diálogo? ¿timeout → prompt o deny?) — toda la feature C depende de esto; el diseño degrada seguro igual.
2. Ancestría de procesos según cómo Claude Code spawnea hooks — fallback: sin botón de jump.
3. Rechazos de `SetForegroundWindow` — fallback AttachThreadInput.
4. Shapes de `tool_input` — parseo defensivo con `JsonElement` en todos lados.
5. Popup `SWP_NOACTIVATE` sobre apps fullscreen — aceptable si queda detrás de fullscreen real.
