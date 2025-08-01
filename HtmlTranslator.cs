using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DescriptionTranslator
{
    internal class HtmlTranslator
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly TranslatorConfig cfg;
        public HtmlTranslator(TranslatorConfig c) => cfg = c;

        // ───────────────────────── 主入口 ─────────────────────────
        public string TranslateHtml(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 仅选择可见纯文本节点；跳过脚本/样式/代码等容器
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "script","style","noscript","code","pre","kbd","samp","var" };

            var nodes = doc.DocumentNode
                .DescendantsAndSelf()
                .Where(n => n.NodeType == HtmlNodeType.Text
                            && !string.IsNullOrWhiteSpace(n.InnerText)
                            && !HasAncestor(n, skip))
                .ToList();

            if (nodes.Count == 0 || MostlyTargetLang(nodes))
                return html;

            // 提取每个文本节点的纯文本（不添加任何编号/符号）
            var srcLines = nodes.Select(n =>
                                WebUtility.HtmlDecode(n.InnerText)
                                          .Replace("\r", " ")
                                          .Replace("\n", " ")
                                          .Trim())
                                .ToList();

            // 三层：10行批 → 334 → 单行；每层做行数 + 幻觉校验
            var translated = MultiTierTranslate(srcLines);

            // 回写（写前做编号/符号清理，保留原文原本就有的 “- ” 列表）
            for (int i = 0; i < nodes.Count; i++)
            {
                var cleaned = CleanMarkers(srcLines[i], translated[i]);
                nodes[i].InnerHtml = WebUtility.HtmlEncode(cleaned);
            }

            return doc.DocumentNode.InnerHtml;
        }

        // ───────────────────── 三层回退总调度 ─────────────────────
        private List<string> MultiTierTranslate(List<string> src)
        {
            int n = src.Count;
            var result = new string[n];

            // ▲ 第一层：10 行批量（并发）
            Parallel.ForEach(Partition(src, 10), part =>
            {
                var outLines = BatchTranslate(part.lines, "L1-10", part.startIdx);
                if (outLines != null)
                    Array.Copy(outLines, 0, result, part.startIdx, part.lines.Count);
            });

            // ▲ 第二层：对仍未填充的行，按其所在 10 行块拆 3-3-4 尝试（并发）
            Parallel.ForEach(Partition334Missing(result, src), sub =>
            {
                var outLines = BatchTranslate(sub.lines, "L2-334", sub.startIdx);
                if (outLines != null)
                {
                    Array.Copy(outLines, 0, result, sub.startIdx, sub.lines.Count);
                }
                else
                {
                    // 该 3/3/4 子批不过 → 逐行（立即降到 L3）
                    for (int i = 0; i < sub.lines.Count; i++)
                    {
                        if (result[sub.startIdx + i] == null)
                            result[sub.startIdx + i] = SingleTranslate(sub.lines[i], sub.startIdx + i);
                    }
                }
            });

            // ▲ 第三层：单行兜底（并发）
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 6 }, i =>
            {
                if (result[i] == null)
                    result[i] = SingleTranslate(src[i], i);
            });

            return result.ToList();
        }

        // ─────────────────── 批量翻译 + 校验 ───────────────────
        private string[] BatchTranslate(List<string> lines, string tag, int startIndex)
        {
            if (lines == null || lines.Count == 0) return null;

            // 合并为多行文本（我们自己不加编号）
            string joined = string.Join("\n", lines);

            // system prompt 仅说明“同样行数”，不去强调禁止编号（避免约束带来的副作用）
            string sysPrompt = $"Translate the following text into {cfg.TargetLang}. " +
                               $"Output must contain exactly the same number of lines as input.";

            string raw = CallLLM(sysPrompt, joined, tag, startIndex);
            if (raw == null) return null;

            // 统一换行 → 按 \n 拆分
            raw = Regex.Replace(raw, @"\s*\r?\n\s*", "\n").Trim();
            var outs = raw.Split('\n').Select(s => s.Trim()).ToArray();

            // ① 行数严格一致
            if (outs.Length != lines.Count) return null;

            // ② 幻觉检测：任意一行不过 → 整批失败
            for (int i = 0; i < outs.Length; i++)
                if (IsHallucination(outs[i], lines[i])) return null;

            return outs;
        }

        // ───────────────────── 单行翻译（兜底） ─────────────────────
        private string SingleTranslate(string text, int idx)
        {
            string sysPrompt = $"Translate this line into {cfg.TargetLang}.";
            string raw = CallLLM(sysPrompt, text, "L3-1", idx);
            if (string.IsNullOrWhiteSpace(raw)) return text;

            // 单行也做一次幻觉检测，不通过则保留原文
            raw = raw.Trim();
            return IsHallucination(raw, text) ? text : raw;
        }

        // ─────────────────────── LLM 调用封装 ───────────────────────
        private string CallLLM(string systemPrompt, string userContent, string tag, int offset)
        {
            using var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            if (cfg.UseOpenAI && !string.IsNullOrWhiteSpace(cfg.ApiKey))
                wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + cfg.ApiKey;

            object body;
            if (cfg.UseOpenAI)
            {
                body = new
                {
                    model = cfg.Model,
                    messages = new[] {
                        new { role = "system", content = systemPrompt },
                        new { role = "user",   content = userContent }
                    },
                    temperature = 0.2,
                    top_p = 0.3
                };
            }
            else
            {
                body = new
                {
                    model = cfg.Model,
                    messages = new[] {
                        new { role = "system", content = systemPrompt },
                        new { role = "user",   content = userContent }
                    },
                    temperature = 0.2,
                    top_p = 0.3,
                    stream = false
                };
            }

            try
            {
                string rsp = wc.UploadString(cfg.ApiUrl,
                              Newtonsoft.Json.JsonConvert.SerializeObject(body));
                var jt = JObject.Parse(rsp);
                string content =
                    (string)jt["choices"]?[0]?["message"]?["content"] ??
                    (string)jt["content"];
                if (content == null) return null;
                return content;
            }
            catch (Exception ex)
            {
                Log.Warn($"[{tag}] offset {offset}: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────── 幻觉检测 ───────────────────────
        private static bool IsHallucination(string translated, string original)
        {
            if (string.IsNullOrWhiteSpace(translated)) return true;

            // 长度比（宽松）
            string co = Regex.Replace(original, @"\s+", "");
            string ct = Regex.Replace(translated, @"\s+", "");
            if (co.Length == 0) return false;

            double ratio = (double)ct.Length / co.Length;
            if (ratio > 3.0 || ratio < 0.15) return true;

            // 典型伪模式：%nnn;、字面 "\n"、凭空 [i]
            if (Regex.IsMatch(translated, @"%\\d+;")) return true;
            if (translated.Contains(@"\n")) return true;
            if (Regex.IsMatch(translated, @"$$\s*i\s*$$", RegexOptions.IgnoreCase)) return true;

            return false;
        }

        // ───────────── 输出侧：去编号/保留原有 bullet ─────────────
        private static string CleanMarkers(string src, string dst)
        {
            if (string.IsNullOrWhiteSpace(dst)) return dst ?? "";
            string s = dst.Trim();

            // 删任意位置的 [i]/[I]
            s = Regex.Replace(s, @"$$\s*i\s*$$", "", RegexOptions.IgnoreCase);

            bool srcBullet = Regex.IsMatch(src, @"^\s*([\-–—]|[•·])\s+");
            bool srcNum = Regex.IsMatch(src, @"^\s*\d+\.\s*");

            // 句首常见编号/符号
            var leading = new Regex(
                @"^\s*((\d{1,3}[\.\:、．])|[$\（]?\d{1,3}[$\）]|[①-⑩]|[ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ]+\.?|第?[一二三四五六七八九十百千]+[\.．:：、）)]|[•·])\s+");

            if (!srcBullet && !srcNum)
            {
                // 原文无序号/符号 → 删除译文句首编号/符号
                s = leading.Replace(s, "");
            }
            else if (srcBullet)
            {
                // 原文是 "- " → 译文句首统一成 "- "
                s = leading.Replace(s, "- ");
            }
            // 原文如果是数字序号：不强制改写，只清理句内伪编号

            // 句内 “。 2.” / “! 3.” / “? 4.”
            s = Regex.Replace(s, @"([。.!?])\s*\d{1,3}[\.\:、．]\s+", "$1 ");

            // 压缩重复 bullet："- - 文本" → "- 文本"
            s = Regex.Replace(s, @"^\s*-\s+(?:-\s+)+", "- ");

            // 压缩多空格
            s = Regex.Replace(s, @"\s{2,}", " ").TrimStart();
            return s;
        }

        // ────────────────────── 分区工具 ──────────────────────
        private static IEnumerable<(int startIdx, List<string> lines)> Partition(List<string> src, int size)
        {
            for (int i = 0; i < src.Count; i += size)
                yield return (i, src.GetRange(i, Math.Min(size, src.Count - i)));
        }

        // 对“还未成功”的位置，按其所在 10 行块组织 3/3/4 子批
        private static IEnumerable<(int startIdx, List<string> lines)> Partition334Missing(string[] result, List<string> src)
        {
            int n = src.Count;
            for (int b = 0; b < n; b += 10)
            {
                // 子批范围： [b, b+3), [b+3, b+6), [b+6, b+10)
                var ranges = new (int st, int ln)[] {
                    (b, Math.Min(3, n - b)),
                    (b + 3, Math.Min(3, Math.Max(0, n - (b + 3)))),
                    (b + 6, Math.Min(4, Math.Max(0, n - (b + 6))))
                };
                foreach (var r in ranges)
                {
                    if (r.ln <= 0) continue;
                    bool need = false;
                    for (int i = 0; i < r.ln; i++)
                        if (result[r.st + i] == null) { need = true; break; }
                    if (need)
                        yield return (r.st, src.GetRange(r.st, r.ln));
                }
            }
        }

        // ──────────────────── 目标语占比判断 ────────────────────
        private bool MostlyTargetLang(IEnumerable<HtmlNode> nodes)
        {
            var raw = string.Concat(nodes.Select(n => n.InnerText));
            var keep = Regex.Replace(raw, @"[^a-zA-Z0-9\u3040-\u30ff\u4e00-\u9fff]", "");
            if (keep.Length == 0) return false;

            Regex re = cfg.TargetLang switch
            {
                "zh" => new Regex(@"[\u4e00-\u9fff]"),
                "ja" => new Regex(@"[\u3040-\u30ff\u4e00-\u9fff]"),
                "en" => new Regex(@"[A-Za-z]"),
                _ => null
            };
            return re != null && re.Matches(keep).Count / (double)keep.Length >= 0.9;
        }

        // ─────────────────────── 祖先标签判断 ───────────────────────
        private static bool HasAncestor(HtmlNode n, HashSet<string> tags)
        {
            for (var p = n.ParentNode; p != null; p = p.ParentNode)
                if (p.NodeType == HtmlNodeType.Element && tags.Contains(p.Name)) return true;
            return false;
        }
    }
}