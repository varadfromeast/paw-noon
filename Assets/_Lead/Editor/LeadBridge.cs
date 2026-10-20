// File-based editor bridge for the lead (cloud) to drive Unity without MCP.
// Drop text files into <project>/Lead/cmd/ (processed in name order). One command per line:
//   refresh | recompile | play | stop | pause | unpause | clearlog | status
//   menu <Menu/Path>              -> EditorApplication.ExecuteMenuItem
//   call <Namespace.Type.Method> [stringArg] -> invoke static method (any loaded assembly)
//   gamesize <w> <h>              -> set Game view to a fixed resolution
//   shot <relative/path.png> [superSize] -> ScreenCapture (Game view; play mode)
//   focusgame                     -> focus the Game view window
//   wait <seconds>                -> delay the rest of the file
// Results append to Lead/out/<cmdfile>.out ; console mirrors to Lead/console.log ;
// compile messages to Lead/compile.log ; heartbeat/state to Lead/state.txt
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace PawNoonLead
{
    [InitializeOnLoad]
    public static class LeadBridge
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Lead"));
        static readonly string CmdDir = Path.Combine(Root, "cmd");
        static readonly string OutDir = Path.Combine(Root, "out");
        static readonly string ConsoleLog = Path.Combine(Root, "console.log");
        static readonly string CompileLog = Path.Combine(Root, "compile.log");
        static readonly string StateFile = Path.Combine(Root, "state.txt");
        static readonly object LogLock = new object();

        static string _activeFile;
        static Queue<string> _pending = new Queue<string>();
        static double _resumeAt;
        static double _nextPoll;
        static double _nextState;
        static readonly string DomainId = Guid.NewGuid().ToString("N");
        static double _resumeWrittenAt = -1;

        static LeadBridge()
        {
            Directory.CreateDirectory(CmdDir);
            Directory.CreateDirectory(OutDir);
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.compilationStarted -= OnCompileStarted;
            CompilationPipeline.compilationStarted += OnCompileStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            PlayerSettings.runInBackground = true;
            Append(ConsoleLog, $"--- domain loaded {DateTime.Now:HH:mm:ss.fff} playing={EditorApplication.isPlayingOrWillChangePlaymode}\n");
            // Resume a file that was interrupted by a domain reload (e.g. play / recompile).
            string resume = Path.Combine(Root, "resume.txt");
            if (File.Exists(resume))
            {
                try
                {
                    var lines = File.ReadAllLines(resume);
                    if (lines.Length > 0)
                    {
                        _activeFile = lines[0];
                        foreach (var l in lines.Skip(1)) _pending.Enqueue(l);
                    }
                    File.Delete(resume);
                }
                catch (Exception e) { Debug.LogWarning("[Lead] resume failed: " + e.Message); }
            }
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(type).Append(": ").Append(condition).Append('\n');
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                if (!string.IsNullOrEmpty(stackTrace))
                    sb.Append("    ").Append(stackTrace.Replace("\n", "\n    ").TrimEnd()).Append('\n');
            }
            Append(ConsoleLog, sb.ToString());
        }

        static void OnCompileStarted(object ctx)
        {
            Append(CompileLog, $"=== compile started {DateTime.Now:HH:mm:ss}\n");
        }

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            var sb = new StringBuilder();
            int errors = messages.Count(m => m.type == CompilerMessageType.Error);
            int warns = messages.Count(m => m.type == CompilerMessageType.Warning);
            sb.Append($"[{Path.GetFileName(assembly)}] errors={errors} warnings={warns}\n");
            foreach (var m in messages)
            {
                if (m.type == CompilerMessageType.Error || (m.type == CompilerMessageType.Warning && !m.file.Contains("PackageCache")))
                    sb.Append("  ").Append(m.type).Append(' ').Append(m.file).Append('(').Append(m.line).Append(',').Append(m.column).Append("): ").Append(m.message).Append('\n');
            }
            Append(CompileLog, sb.ToString());
        }

        static void Append(string path, string text)
        {
            try { lock (LogLock) File.AppendAllText(path, text); } catch { }
        }

        static void Out(string text)
        {
            string name = _activeFile != null ? Path.GetFileNameWithoutExtension(_activeFile) : "misc";
            Append(Path.Combine(OutDir, name + ".out"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\n");
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= _nextState)
            {
                _nextState = now + 1.0;
                try
                {
                    File.WriteAllText(StateFile,
                        $"time={DateTime.Now:HH:mm:ss}\nplaying={EditorApplication.isPlaying}\npaused={EditorApplication.isPaused}\n" +
                        $"compiling={EditorApplication.isCompiling}\nupdating={EditorApplication.isUpdating}\nactive={_activeFile}\npending={_pending.Count}\n");
                }
                catch { }
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            // A resume file written in this same domain that survived 4 s means no reload happened: drop it.
            if (_resumeWrittenAt > 0 && now - _resumeWrittenAt > 4.0 && EditorApplication.isPlaying == EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _resumeWrittenAt = -1;
                try { File.Delete(Path.Combine(Root, "resume.txt")); } catch { }
            }
            if (now < _resumeAt) return;

            if (_pending.Count > 0)
            {
                string line = _pending.Dequeue();
                try { Execute(line.Trim()); }
                catch (Exception e) { Out("ERROR " + line + " -> " + e); }
                if (_pending.Count == 0 && _activeFile != null) { Out("DONE"); _activeFile = null; }
                return;
            }

            if (now < _nextPoll) return;
            _nextPoll = now + 0.25;
            string[] files;
            try { files = Directory.GetFiles(CmdDir, "*.txt").OrderBy(f => f, StringComparer.Ordinal).ToArray(); }
            catch { return; }
            if (files.Length == 0) return;
            string file = files[0];
            string[] lines;
            try { lines = File.ReadAllLines(file); File.Delete(file); }
            catch { return; }
            _activeFile = file;
            Out("BEGIN " + Path.GetFileName(file));
            foreach (var l in lines) if (!string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")) _pending.Enqueue(l);
            if (_pending.Count == 0) { Out("DONE"); _activeFile = null; }
        }

        // Persist the remaining queue so a domain reload (play mode, recompile) doesn't drop it.
        static void SaveResume()
        {
            try
            {
                var lines = new List<string> { _activeFile ?? "misc" };
                lines.AddRange(_pending);
                File.WriteAllLines(Path.Combine(Root, "resume.txt"), lines);
                _resumeWrittenAt = EditorApplication.timeSinceStartup;
            }
            catch { }
        }

        static void Execute(string line)
        {
            string cmd = line;
            string arg = "";
            int sp = line.IndexOf(' ');
            if (sp > 0) { cmd = line.Substring(0, sp); arg = line.Substring(sp + 1).Trim(); }
            Out("> " + line);
            switch (cmd)
            {
                case "refresh":
                    // never reload scripts under a running game: the MonoBehaviours survive the domain reload with
                    // their private fields nulled and Update() throws every frame (lesson of 20 Oct)
                    if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; Out("ok stopped play first"); }
                    SaveResume();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    File.Delete(Path.Combine(Root, "resume.txt"));
                    Out("ok refreshed; compiling=" + EditorApplication.isCompiling);
                    if (EditorApplication.isCompiling) SaveResume();
                    break;
                case "recompile":
                    if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; Out("ok stopped play first"); }
                    SaveResume();
                    CompilationPipeline.RequestScriptCompilation();
                    Out("ok compile requested");
                    break;
                case "play":
                    SaveResume();
                    EditorApplication.isPlaying = true;
                    Out("ok entering play");
                    _resumeAt = EditorApplication.timeSinceStartup + 1.0;
                    break;
                case "stop":
                    SaveResume();
                    EditorApplication.isPlaying = false;
                    Out("ok stopping");
                    _resumeAt = EditorApplication.timeSinceStartup + 1.0;
                    break;
                case "pause": EditorApplication.isPaused = true; Out("ok"); break;
                case "unpause": EditorApplication.isPaused = false; Out("ok"); break;
                case "clearlog":
                    try { File.WriteAllText(ConsoleLog, ""); File.WriteAllText(CompileLog, ""); } catch { }
                    Out("ok"); break;
                case "status":
                    Out($"playing={EditorApplication.isPlaying} compiling={EditorApplication.isCompiling} scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().path}");
                    break;
                case "menu":
                    Out(EditorApplication.ExecuteMenuItem(arg) ? "ok menu " + arg : "FAILED menu " + arg);
                    break;
                case "call":
                    CallStatic(arg);
                    break;
                case "gamesize":
                    {
                        var p = arg.Split(' ');
                        SetGameViewSize(int.Parse(p[0]), int.Parse(p[1]));
                        break;
                    }
                case "focusgame":
                    FocusGameView();
                    Out("ok");
                    break;
                case "shot":
                    {
                        var p = arg.Split(' ');
                        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", p[0]));
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        int super = p.Length > 1 ? int.Parse(p[1]) : 1;
                        FocusGameView();
                        ScreenCapture.CaptureScreenshot(path, super);
                        Out("ok shot -> " + path);
                        _resumeAt = EditorApplication.timeSinceStartup + 0.5;
                        break;
                    }
                case "wait":
                    _resumeAt = EditorApplication.timeSinceStartup + double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    Out("ok waiting " + arg);
                    break;
                default:
                    Out("UNKNOWN " + cmd);
                    break;
            }
        }

        static void CallStatic(string arg)
        {
            string target = arg, sArg = null;
            int sp = arg.IndexOf(' ');
            if (sp > 0) { target = arg.Substring(0, sp); sArg = arg.Substring(sp + 1); }
            int dot = target.LastIndexOf('.');
            string typeName = target.Substring(0, dot), method = target.Substring(dot + 1);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(typeName, false); } catch { }
                if (t == null) continue;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var mi = t.GetMethods(flags).FirstOrDefault(m => m.Name == method && m.GetParameters().Length == (sArg == null ? 0 : 1));
                if (mi == null) { Out("FAILED no method " + target); return; }
                object r = mi.Invoke(null, sArg == null ? null : new object[] { ConvertArg(sArg, mi.GetParameters()[0].ParameterType) });
                Out("ok call " + target + " -> " + (r == null ? "null" : r.ToString()));
                return;
            }
            Out("FAILED no type " + typeName);
        }

        static object ConvertArg(string s, Type t)
        {
            if (t == typeof(string)) return s;
            if (t.IsEnum) return Enum.Parse(t, s, true);
            return Convert.ChangeType(s, t, System.Globalization.CultureInfo.InvariantCulture);
        }

        static Type GameViewType => Type.GetType("UnityEditor.GameView,UnityEditor");

        static void FocusGameView()
        {
            var t = GameViewType;
            if (t == null) return;
            var w = EditorWindow.GetWindow(t, false, null, true);
            w.Focus();
        }

        // Adds (if needed) and selects a fixed-resolution Game view size via reflection.
        static void SetGameViewSize(int w, int h)
        {
            try
            {
                var sizesType = Type.GetType("UnityEditor.GameViewSizes,UnityEditor");
                var singleType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                var instance = singleType.GetProperty("instance").GetValue(null, null);
                var groupType = (GameViewSizeGroupType)Enum.Parse(typeof(GameViewSizeGroupType), "Standalone");
                var current = sizesType.GetProperty("currentGroupType")?.GetValue(instance, null);
                if (current != null) groupType = (GameViewSizeGroupType)current;
                var group = sizesType.GetMethod("GetGroup").Invoke(instance, new object[] { (int)groupType });
                var groupT = group.GetType();
                int total = (int)groupT.GetMethod("GetTotalCount").Invoke(group, null);
                int index = -1;
                for (int i = 0; i < total; i++)
                {
                    var sz = groupT.GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                    var szT = sz.GetType();
                    if ((int)szT.GetProperty("width").GetValue(sz, null) == w && (int)szT.GetProperty("height").GetValue(sz, null) == h) { index = i; break; }
                }
                if (index < 0)
                {
                    var sizeT = Type.GetType("UnityEditor.GameViewSize,UnityEditor");
                    var kindT = Type.GetType("UnityEditor.GameViewSizeType,UnityEditor");
                    var ctor = sizeT.GetConstructor(new[] { kindT, typeof(int), typeof(int), typeof(string) });
                    var newSize = ctor.Invoke(new object[] { Enum.Parse(kindT, "FixedResolution"), w, h, $"Lead {w}x{h}" });
                    groupT.GetMethod("AddCustomSize").Invoke(group, new[] { newSize });
                    index = (int)groupT.GetMethod("GetTotalCount").Invoke(group, null) - 1;
                }
                var gv = EditorWindow.GetWindow(GameViewType, false, null, true);
                var prop = GameViewType.GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) prop.SetValue(gv, index, null);
                else GameViewType.GetMethod("SizeSelectionCallback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(gv, new object[] { index, null });
                gv.Repaint();
                Out($"ok gamesize {w}x{h} index={index}");
            }
            catch (Exception e) { Out("FAILED gamesize " + e.Message); }
        }
    }
}
