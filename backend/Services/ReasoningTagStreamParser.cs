using System.Globalization;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 对流式 assistant 文本做增量切分：标签内 → 推理，标签外 → 给用户正文。
/// 支持多种开始/结束标签对（XML 风格大小写不敏感）。未闭合时 Flush 将剩余归入当前模式。
/// 百炼 / Qwen OpenAI 兼容流是否在 JSON 中另有 reasoning 字段需以实测为准；若将来接入独立字段，应在 Chat 层优先下发 <c>reasoning_chunk</c>，再与标签解析并存。
/// </summary>
public sealed class ReasoningTagStreamParser
{
    private readonly List<(bool IsReasoning, string Text)> _batch = new();
    private bool _inReasoning;
    private string _carry = "";
    private string _currentClose = "";

    /// <summary>较长/较具体的 open 必须排在前面，避免 <c>&lt;thinking&gt;</c> 被当成 <c>&lt;think&gt;</c>。</summary>
    public static readonly (string Open, string Close)[] TagPairs =
    {
        ("<thinking>", "</thinking>"),
        ("<thought>", "</thought>"),
        ("<reasoning>", "</reasoning>"),
        ("\u003cthink\u003e", "\u003c/think\u003e")
    };

    /// <summary>最长开始/结束标签长度，用于流式半截保留。</summary>
    public static int MaxTagLookahead { get; } = Math.Max(
        16,
        TagPairs.Max(p => Math.Max(p.Open.Length, p.Close.Length)));

    public void Reset()
    {
        _inReasoning = false;
        _carry = "";
        _currentClose = "";
        _batch.Clear();
    }

    /// <summary>喂入一段 delta，返回本轮应下发的片段（可多条）。</summary>
    public IReadOnlyList<(bool IsReasoning, string Text)> Append(string? delta)
    {
        _batch.Clear();
        if (string.IsNullOrEmpty(delta))
            return _batch;

        _carry += delta;
        ProcessCarry();

        // 非推理态且缓冲中不可能出现半截开始标签时，立即吐出（避免短句一直卡在 carry 直到 Flush）
        if (_carry.Length > 0 && !_inReasoning && !_carry.Contains('<'))
        {
            Emit(false, _carry);
            _carry = "";
        }

        return _batch.ToList();
    }

    /// <summary>流结束：吐出剩余缓冲（含未闭合标签的回落）。</summary>
    public IReadOnlyList<(bool IsReasoning, string Text)> Flush()
    {
        _batch.Clear();
        ProcessCarry();
        if (_carry.Length > 0)
        {
            Emit(_inReasoning, _carry);
            _carry = "";
        }

        _inReasoning = false;
        _currentClose = "";
        return _batch.ToList();
    }

    /// <summary>反复应用 Step 直至缓冲不再变化（同一 Append 内可完成「开标签 → 推理 → 闭标签 → 正文」多步）。</summary>
    private void ProcessCarry()
    {
        while (true)
        {
            var before = _carry.Length;
            if (_inReasoning)
                StepInReasoning();
            else
                StepNeutral();

            if (_carry.Length == before)
                break;
        }
    }

    private void StepNeutral()
    {
        var (idx, openLen, closeTag) = FindEarliestOpen(_carry);
        if (idx >= 0)
        {
            if (idx > 0)
                Emit(false, _carry[..idx]);
            _carry = _carry[(idx + openLen)..];
            _inReasoning = true;
            _currentClose = closeTag;
            return;
        }

        if (_carry.Length > MaxTagLookahead)
        {
            var safeLen = _carry.Length - MaxTagLookahead;
            Emit(false, _carry[..safeLen]);
            _carry = _carry[safeLen..];
        }
    }

    private void StepInReasoning()
    {
        var closeIdx = IndexOfIgnoreCase(_carry, _currentClose);
        if (closeIdx >= 0)
        {
            if (closeIdx > 0)
                Emit(true, _carry[..closeIdx]);
            _carry = _carry[(closeIdx + _currentClose.Length)..];
            _inReasoning = false;
            _currentClose = "";
            return;
        }

        if (_carry.Length > MaxTagLookahead)
        {
            var safeLen = _carry.Length - MaxTagLookahead;
            Emit(true, _carry[..safeLen]);
            _carry = _carry[safeLen..];
        }
    }

    private static (int Index, int OpenLen, string CloseTag) FindEarliestOpen(string s)
    {
        var bestIdx = -1;
        var openLen = 0;
        var close = "";

        foreach (var (open, c) in TagPairs)
        {
            var i = IndexOfIgnoreCase(s, open);
            if (i < 0) continue;
            if (bestIdx < 0 || i < bestIdx)
            {
                bestIdx = i;
                openLen = open.Length;
                close = c;
            }
        }

        return (bestIdx, openLen, close);
    }

    private static int IndexOfIgnoreCase(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, needle, CompareOptions.IgnoreCase);
    }

    private void Emit(bool isReasoning, string text)
    {
        if (text.Length == 0) return;
        _batch.Add((isReasoning, text));
    }

    /// <summary>从历史 assistant 全文剥离推理标签，仅保留给用户的内容（用于写会话历史）。</summary>
    public static string StripReasoningTags(string? full)
    {
        if (string.IsNullOrEmpty(full)) return full ?? "";
        var p = new ReasoningTagStreamParser();
        var sb = new System.Text.StringBuilder();
        foreach (var part in p.Append(full))
        {
            if (!part.IsReasoning)
                sb.Append(part.Text);
        }
        foreach (var part in p.Flush())
        {
            if (!part.IsReasoning)
                sb.Append(part.Text);
        }
        return sb.ToString();
    }
}
