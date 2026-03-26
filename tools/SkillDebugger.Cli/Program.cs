using System.Text.Json;
using OfficeCopilot.Server.Services.SkillVm;

namespace SkillDebugger.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "validate" => CmdValidate(args.AsSpan(1)),
                "step" => CmdStep(args.AsSpan(1)),
                "replay" => CmdReplay(args.AsSpan(1)),
                "run" => CmdRun(args.AsSpan(1)),
                "help" or "-h" or "--help" => PrintHelp(),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[skill-debugger] " + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string a)
    {
        Console.Error.WriteLine("未知命令: " + a);
        PrintHelp();
        return 1;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            skill-debugger — 脚本式 Skill VM 调试（模式 A）

            命令:
              validate <技能目录> [--extra <其他技能目录> ...]
                  校验 manifest、段可读性、allowedGotoTargets 与 SkillVmGotoPolicy。

              step <技能目录> [--session id] [--extra <dir> ...] --action next|goto|finish|return|pause [--target-skill id] [--target-segment id]
                  单步推进并打印 PC / 栈 / 结果摘要。

              replay <技能目录> <trace.json> [--verify] [--extra <dir> ...]
                  按轨迹 JSON（见 docs/skill-debugger-trace-schema.json）回放 skill_step 序列。

              run <技能目录> [--session id] [--extra <dir> ...]
                  交互：输入 next/goto/finish/return/pause 或 quit。

            示例:
              dotnet run --project tools/SkillDebugger.Cli -- validate backend/Skills/skill-vm-demo
              dotnet run --project tools/SkillDebugger.Cli -- step backend/Skills/skill-vm-demo --action next
            """);
        return 0;
    }

    private static int CmdValidate(ReadOnlySpan<string> args)
    {
        var (skillDir, extras) = ParseDirAndExtras(args);
        if (string.IsNullOrEmpty(skillDir))
        {
            Console.Error.WriteLine("用法: validate <技能目录> [--extra <dir> ...]");
            return 1;
        }

        var store = SkillDebuggerSkillStore.Load(skillDir, extras);
        var errors = new List<string>();
        foreach (var kv in store.ById)
        {
            var info = kv.Value;
            foreach (var seg in info.Manifest.Segments)
            {
                var t = info.GetSegmentText(seg.Id);
                if (string.IsNullOrEmpty(t))
                    errors.Add($"技能 {info.Id} 段 {seg.Id}：无法解析正文（检查 source 或 segments/{seg.Id}.md）。");
            }

            if (info.Manifest.AllowedGotoTargets is { Count: > 0 } list)
            {
                foreach (var raw in list)
                {
                    var key = (raw ?? "").Trim();
                    var colon = key.IndexOf(':');
                    if (colon <= 0)
                    {
                        errors.Add($"技能 {info.Id} allowedGotoTargets 项格式无效：{key}");
                        continue;
                    }

                    var ts = key[..colon].Trim();
                    var tseg = key[(colon + 1)..].Trim();
                    var to = store.TryGet(ts);
                    if (to == null)
                        errors.Add($"技能 {info.Id} goto 目标技能未加载：{ts}（使用 --extra 指定目录）");
                    else if (!SkillVmGotoPolicy.SegmentExists(to.Manifest, tseg))
                        errors.Add($"技能 {info.Id} → {ts}:{tseg} 段不存在。");
                }
            }
        }

        Console.WriteLine($"已加载技能数：{store.ById.Count}");
        if (errors.Count == 0)
        {
            Console.WriteLine("校验通过。");
            return 0;
        }

        Console.WriteLine("校验问题：");
        foreach (var e in errors)
            Console.WriteLine("  - " + e);
        return 2;
    }

    private static int CmdStep(ReadOnlySpan<string> args)
    {
        var skillDir = "";
        var session = "cli";
        var extras = new List<string>();
        string? action = null;
        string? targetSkill = null;
        string? targetSeg = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--session" && i + 1 < args.Length) { session = args[++i]; continue; }
            if (a == "--extra" && i + 1 < args.Length) { extras.Add(args[++i]); continue; }
            if (a == "--action" && i + 1 < args.Length) { action = args[++i]; continue; }
            if (a == "--target-skill" && i + 1 < args.Length) { targetSkill = args[++i]; continue; }
            if (a == "--target-segment" && i + 1 < args.Length) { targetSeg = args[++i]; continue; }
            if (!a.StartsWith('-') && string.IsNullOrEmpty(skillDir)) { skillDir = a; continue; }
        }

        if (string.IsNullOrEmpty(skillDir) || string.IsNullOrEmpty(action))
        {
            Console.Error.WriteLine("用法: step <技能目录> [--session id] [--extra <dir> ...] --action next|goto|finish|return|pause ...");
            return 1;
        }

        var store = SkillDebuggerSkillStore.Load(skillDir, extras);
        var primary = ResolvePrimarySkill(skillDir, store);
        var state = VmStepRunner.CreateInitialState(session, primary);
        var runner = new VmStepRunner(store);

        var act = action.Trim().ToLowerInvariant();
        string msg;
        switch (act)
        {
            case "next":
                msg = runner.ApplyNext(state);
                break;
            case "goto":
                msg = runner.ApplyGoto(state, targetSkill, targetSeg);
                break;
            case "finish":
                state.Finished = true;
                state.Paused = false;
                msg = "[SkillVM] finish";
                break;
            case "return":
                msg = runner.ApplyReturn(state);
                break;
            case "pause":
                state.Paused = true;
                msg = "[SkillVM] pause";
                break;
            default:
                Console.Error.WriteLine("无效 action");
                return 1;
        }

        DumpState(state, msg);
        return 0;
    }

    private static int CmdReplay(ReadOnlySpan<string> args)
    {
        var verify = false;
        var skillDir = "";
        string? tracePath = null;
        var extras = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--verify") { verify = true; continue; }
            if (a == "--extra" && i + 1 < args.Length) { extras.Add(args[++i]); continue; }
            if (!a.StartsWith('-'))
            {
                if (string.IsNullOrEmpty(skillDir)) skillDir = a;
                else if (string.IsNullOrEmpty(tracePath)) tracePath = a;
            }
        }

        if (string.IsNullOrEmpty(skillDir) || string.IsNullOrEmpty(tracePath) || !File.Exists(tracePath))
        {
            Console.Error.WriteLine("用法: replay <技能目录> <trace.json> [--verify] [--extra <dir> ...]");
            return 1;
        }

        var json = File.ReadAllText(tracePath);
        var trace = JsonSerializer.Deserialize<TraceFileDto>(json, JsonOpts)
            ?? throw new InvalidOperationException("无法解析轨迹 JSON");

        var store = SkillDebuggerSkillStore.Load(skillDir, extras);
        var entrySkill = trace.SkillId.Trim();
        var primary = (!string.IsNullOrEmpty(entrySkill) ? store.TryGet(entrySkill) : null)
            ?? ResolvePrimarySkill(skillDir, store);
        var state = VmStepRunner.CreateInitialState("replay", primary);
        if (!string.IsNullOrEmpty(entrySkill) && store.TryGet(entrySkill) != null)
        {
            state.ActiveSkillId = entrySkill;
            var first = SkillVmSegmentContent.GetFirstSegmentId(store.TryGet(entrySkill)!.Manifest);
            if (!string.IsNullOrEmpty(first))
                state.CurrentSegmentId = first;
        }

        var runner = new VmStepRunner(store);
        foreach (var e in trace.Entries)
        {
            if (!string.Equals(e.Kind, "skill_step", StringComparison.OrdinalIgnoreCase))
                continue;
            var step = e.SkillStep ?? throw new InvalidOperationException("缺少 skillStep");
            var act = (step.Action ?? "").Trim().ToLowerInvariant();
            string msg;
            switch (act)
            {
                case "next":
                    msg = runner.ApplyNext(state);
                    break;
                case "goto":
                    msg = runner.ApplyGoto(state, step.TargetSkillId, step.TargetSegmentId);
                    break;
                case "finish":
                    state.Finished = true;
                    state.Paused = false;
                    msg = "finish";
                    break;
                case "return":
                    msg = runner.ApplyReturn(state);
                    break;
                case "pause":
                    state.Paused = true;
                    msg = "pause";
                    break;
                default:
                    throw new InvalidOperationException("未知 action：" + act);
            }

            Console.WriteLine(msg);
            if (verify && e.StateAfter != null && !StateEqualsLoose(state, e.StateAfter))
                Console.Error.WriteLine("[verify] 与 stateAfter 不一致（宽松比较）。");
        }

        Console.WriteLine("--- 回放结束 ---");
        DumpState(state, "");
        return 0;
    }

    private static bool StateEqualsLoose(SkillVmState a, SkillVmState b) =>
        string.Equals(a.ActiveSkillId, b.ActiveSkillId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.CurrentSegmentId, b.CurrentSegmentId, StringComparison.OrdinalIgnoreCase)
        && a.Finished == b.Finished
        && a.Paused == b.Paused
        && a.Stack.Count == b.Stack.Count;

    private static int CmdRun(ReadOnlySpan<string> args)
    {
        var skillDir = "";
        var session = "cli";
        var extras = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--session" && i + 1 < args.Length) { session = args[++i]; continue; }
            if (a == "--extra" && i + 1 < args.Length) { extras.Add(args[++i]); continue; }
            if (!a.StartsWith('-') && string.IsNullOrEmpty(skillDir)) { skillDir = a; continue; }
        }

        if (string.IsNullOrEmpty(skillDir))
        {
            Console.Error.WriteLine("用法: run <技能目录> [--session id] [--extra <dir> ...]");
            return 1;
        }

        var store = SkillDebuggerSkillStore.Load(skillDir, extras);
        var primary = ResolvePrimarySkill(skillDir, store);
        var state = VmStepRunner.CreateInitialState(session, primary);
        var runner = new VmStepRunner(store);
        Console.WriteLine("交互模式。命令: next | goto <segment> | goto <skill> <segment> | finish | return | pause | state | quit");
        while (true)
        {
            Console.Write("skill-vm> ");
            var line = Console.ReadLine();
            if (line == null || line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var cmd = parts[0].ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "next":
                        Console.WriteLine(runner.ApplyNext(state));
                        break;
                    case "goto":
                        if (parts.Length == 2)
                            Console.WriteLine(runner.ApplyGoto(state, null, parts[1]));
                        else if (parts.Length >= 3)
                            Console.WriteLine(runner.ApplyGoto(state, parts[1], parts[2]));
                        else
                            Console.WriteLine("goto <segmentId> 或 goto <skillId> <segmentId>");
                        break;
                    case "finish":
                        state.Finished = true;
                        state.Paused = false;
                        Console.WriteLine("finish");
                        break;
                    case "return":
                        Console.WriteLine(runner.ApplyReturn(state));
                        break;
                    case "pause":
                        state.Paused = true;
                        Console.WriteLine("pause");
                        break;
                    case "state":
                        DumpState(state, "");
                        break;
                    default:
                        Console.WriteLine("未知命令");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        return 0;
    }

    private static void DumpState(SkillVmState state, string lastMsg)
    {
        if (!string.IsNullOrEmpty(lastMsg))
            Console.WriteLine(lastMsg);
        Console.WriteLine("--- PC ---");
        Console.WriteLine("  activeSkillId=" + state.ActiveSkillId);
        Console.WriteLine("  currentSegmentId=" + state.CurrentSegmentId);
        Console.WriteLine("  finished=" + state.Finished + " paused=" + state.Paused);
        Console.WriteLine("--- Stack ---");
        if (state.Stack.Count == 0)
            Console.WriteLine("  (empty)");
        else
        {
            for (var i = 0; i < state.Stack.Count; i++)
            {
                var f = state.Stack[i];
                Console.WriteLine($"  [{i}] skill={f.SkillId} seg={f.SegmentId} returnTo={f.ReturnSegmentId}");
            }
        }

        Console.WriteLine("--- State (JSON) ---");
        Console.WriteLine(JsonSerializer.Serialize(state, JsonOpts));
    }

    private static SkillInfo ResolvePrimarySkill(string skillDir, SkillDebuggerSkillStore store)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(skillDir), "skill.manifest.json");
        var m = SkillVmManifestLoader.TryLoad(manifestPath);
        var id = string.IsNullOrWhiteSpace(m?.SkillId)
            ? Path.GetFileName(Path.GetFullPath(skillDir).TrimEnd(Path.DirectorySeparatorChar))
            : m!.SkillId.Trim();
        return store.TryGet(id) ?? throw new InvalidOperationException("无法解析主技能：" + skillDir);
    }

    private static (string skillDir, List<string> extras) ParseDirAndExtras(ReadOnlySpan<string> args)
    {
        var extras = new List<string>();
        var skillDir = "";
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--extra" && i + 1 < args.Length)
            {
                extras.Add(args[++i]);
                continue;
            }

            if (!a.StartsWith('-') && string.IsNullOrEmpty(skillDir))
                skillDir = a;
        }

        return (skillDir, extras);
    }
}
