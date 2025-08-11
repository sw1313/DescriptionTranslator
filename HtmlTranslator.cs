using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DescriptionTranslator
{
    internal sealed class WebClientEx : WebClient
    {
        public int TimeoutMs { get; set; } = 24 * 60 * 60 * 1000;
        public int ReadWriteTimeoutMs { get; set; } = 24 * 60 * 60 * 1000;

        protected override WebRequest GetWebRequest(Uri address)
        {
            var r = base.GetWebRequest(address);
            if (r != null)
            {
                r.Timeout = TimeoutMs;
                if (r is HttpWebRequest h) h.ReadWriteTimeout = ReadWriteTimeoutMs;
            }
            return r;
        }
    }

    internal class HtmlTranslator
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly TranslatorConfig cfg;

        private const int WORKER_COUNT = 3;
        private const int HTTP_TIMEOUT_MS = 120_000;
        private const int SINGLE_HALLUCINATION_MAX = 2;
        private const int HTTP_MAX_RETRIES = 3;

        // 新增：目标语言占比跳过阈值（90%）
        private const double LANG_SKIP_THRESHOLD = 0.90;

        private static readonly SemaphoreSlim HttpGate = new SemaphoreSlim(WORKER_COUNT, WORKER_COUNT);

        public HtmlTranslator(TranslatorConfig c) => cfg = c;

        private string BuildSystemPrompt(string fallback)
        {
            var sp = (cfg.SystemPrompt ?? "").Trim();
            if (!string.IsNullOrEmpty(sp))
                return sp.Replace("${src}", cfg.SourceLang ?? "auto")
                         .Replace("${dst}", cfg.TargetLang ?? "zh");
            return fallback;
        }

        /* ==================== 新增：整页目标语言占比检测 ==================== */
        public bool ShouldSkipByLanguage(string html, out double coverage)
        {
            coverage = 0;
            if (string.IsNullOrEmpty(html)) return false;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var skipTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "script","style","noscript","code","pre","kbd","samp","var","svg","math" };

            // 收集可翻译纯文本（与翻译时一致：不看 <a> 内文）
            var textNodes = doc.DocumentNode
                .Descendants()
                .Where(n => n.NodeType == HtmlNodeType.Text &&
                            !HasAncestor(n, skipTags) &&
                            !HasAncestor(n, "a"))
                .Select(n => ((HtmlTextNode)n).Text)
                .ToList();

            if (textNodes.Count == 0) return false;

            string langKind = NormalizeLang(cfg.TargetLang);
            long targetLetters = 0;
            long totalLetters = 0;

            foreach (var raw in textNodes)
            {
                if (string.IsNullOrEmpty(raw)) continue;

                // 仅统计“字母类”字符；忽略数字/空白/标点
                foreach (var ch in raw)
                {
                    if (!char.IsLetter(ch)) continue;
                    totalLetters++;
                    if (IsTargetLetter(ch, langKind)) targetLetters++;
                }
            }

            if (totalLetters == 0) return false;
            coverage = targetLetters / (double)totalLetters;
            return coverage >= LANG_SKIP_THRESHOLD;
        }

        private static string NormalizeLang(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "zh";
            lang = lang.Trim().ToLowerInvariant();
            if (lang.StartsWith("zh")) return "zh";
            if (lang.StartsWith("ja")) return "ja";
            if (lang.StartsWith("ko")) return "ko";
            if (lang.StartsWith("en")) return "en";
            if (lang.StartsWith("ru")) return "ru";
            if (lang.StartsWith("es")) return "es";
            if (lang.StartsWith("fr")) return "fr";
            if (lang.StartsWith("de")) return "de";
            return lang; // 其他：不特别支持，占比判断将很宽松
        }

        private static bool IsTargetLetter(char c, string lang)
        {
            switch (lang)
            {
                case "zh":
                    return (c >= 0x4E00 && c <= 0x9FFF)   // CJK
                        || (c >= 0x3400 && c <= 0x4DBF)   // CJK Ext-A
                        || (c >= 0xF900 && c <= 0xFAFF);  // CJK Compatibility
                case "ja":
                    return (c >= 0x3040 && c <= 0x309F)   // ひらがな
                        || (c >= 0x30A0 && c <= 0x30FF)   // カタカナ
                        || (c >= 0xFF66 && c <= 0xFF9D)   // 半角片假名
                        || (c >= 0x4E00 && c <= 0x9FFF)   // 常用汉字
                        || (c >= 0x3400 && c <= 0x4DBF)
                        || (c >= 0xF900 && c <= 0xFAFF);
                case "ko":
                    return (c >= 0xAC00 && c <= 0xD7AF)   // Hangul syllables
                        || (c >= 0x1100 && c <= 0x11FF)   // Jamo
                        || (c >= 0x3130 && c <= 0x318F);  // Compatibility Jamo
                case "en":
                    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                case "ru":
                    return (c >= 0x0400 && c <= 0x04FF);  // Cyrillic
                case "es":
                case "fr":
                case "de":
                    // 粗略：认为拉丁字母即可
                    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                         || (c >= 0x00C0 && c <= 0x024F); // Latin-1 Supplement & Latin Extended
                default:
                    // 默认：认为任何字母都计入目标（会使覆盖度偏高，因而更易跳过；如不想可改成 false）
                    return false;
            }
        }
        /* ==================== 语言占比检测 结束 ==================== */

        public async Task<string> TranslateHtmlFileAsync(string inputPath, string outputPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            string html = File.ReadAllText(inputPath, Encoding.UTF8); // 原封不动读取
            var translated = await TranslateHtmlAsync(html, ct).ConfigureAwait(false);

            try
            {
                var utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(outputPath, translated, utf8NoBom);
            }
            catch (Exception ex)
            {
                Log.Warn($"写入翻译 HTML 失败：{outputPath} - {ex.Message}");
            }
            return translated;
        }

        public async Task<string> TranslateHtmlAsync(string html, CancellationToken ct = default)
        {
            var doc = new HtmlDocument();
            doc.OptionWriteEmptyNodes = true;
            doc.LoadHtml(html);

            // 跳过代码/样式/脚本等（满足“跳过代码”要求）
            var skipTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "script","style","noscript","code","pre","kbd","samp","var","svg","math" };

            // 可翻译的文字属性（不动 href/src）
            var translatableAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "alt","title","aria-label" };
            bool translateAttributes = true;

            var textNodes = doc.DocumentNode
                .Descendants()
                .Where(n => n.NodeType == HtmlNodeType.Text &&
                            !HasAncestor(n, skipTags) &&
                            !HasAncestor(n, "a"))
                .Cast<HtmlTextNode>()
                .ToList();

            var attrHolders = new List<(HtmlAttribute attr, string leadingWs, string core, string trailingWs)>();
            if (translateAttributes)
            {
                foreach (var el in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
                {
                    foreach (var a in el.Attributes)
                    {
                        if (!translatableAttributes.Contains(a.Name)) continue;
                        var raw = a.Value;
                        if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) continue;
                        SplitEdgeWhitespace(raw, out var pre, out var core, out var post);
                        if (core.Length == 0) continue;
                        attrHolders.Add((a, pre, core, post));
                    }
                }
            }

            if (textNodes.Count == 0 && attrHolders.Count == 0)
                return doc.DocumentNode.InnerHtml;

            var items = new List<(Action<string> setter, string core)>(textNodes.Count + attrHolders.Count);

            // 文本节点
            foreach (var tn in textNodes)
            {
                var raw = tn.Text;
                if (string.IsNullOrEmpty(raw)) continue;
                SplitEdgeWhitespace(raw, out var pre, out var core, out var post);
                if (core.Length == 0) continue;

                items.Add((
                    setter: (string translated) => { tn.Text = pre + translated + post; },
                    core: NormalizeForLLM(core)));
            }

            // 属性
            foreach (var (attr, pre, core, post) in attrHolders)
            {
                items.Add((
                    setter: (string translated) => { attr.Value = pre + translated + post; },
                    core: NormalizeForLLM(core)));
            }

            if (items.Count == 0)
                return doc.DocumentNode.InnerHtml;

            var srcList = items.Select(i => i.core).ToList();
            var dstList = await TranslateWithDegradeAsync(srcList, ct).ConfigureAwait(false);

            for (int i = 0; i < items.Count; i++)
            {
                var outLine = dstList[i] ?? srcList[i];

                // 单行守卫：真实 CR/LF 改为空格（不影响 <br>）
                if (outLine.IndexOf('\n') >= 0 || outLine.IndexOf('\r') >= 0)
                    outLine = Regex.Replace(outLine, @"[\r\n]+", " ");

                // 最小清洗：去 $$ i $$ 等
                outLine = Regex.Replace(outLine, @"\$\$\s*i\s*\$\$", "", RegexOptions.IgnoreCase);

                items[i].Item1(outLine);
            }

            return doc.DocumentNode.InnerHtml;
        }

        private static string NormalizeForLLM(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\r", " ").Replace("\n", " ");
        }

        private static void SplitEdgeWhitespace(string raw, out string leading, out string core, out string trailing)
        {
            var m1 = Regex.Match(raw, @"^\s+");
            var m2 = Regex.Match(raw, @"\s+$");
            leading = m1.Success ? m1.Value : "";
            trailing = m2.Success ? m2.Value : "";
            int start = leading.Length;
            int len = raw.Length - start - trailing.Length;
            core = len > 0 ? raw.Substring(start, len) : "";
        }

        /* ====== 你的原“降级/管线”风格（保留） ====== */

        private async Task<List<string>> TranslateWithDegradeAsync(List<string> src, CancellationToken ct)
        {
            int n = src.Count;
            var res = new string[n];
            var tasks = new List<Task>();

            for (int start = 0; start < n; start += 10)
            {
                int s = start, len = Math.Min(10, n - s);

                tasks.Add(Task.Run(async () =>
                {
                    var lines = src.GetRange(s, len);

                    var out10 = await BatchTranslateAsync(lines, "B10", s, ct).ConfigureAwait(false);
                    if (out10 != null)
                    {
                        Array.Copy(out10, 0, res, s, len);
                        return;
                    }

                    if (len == 10)
                    {
                        await TrySmallChunkAsync(src, res, s + 0, 3, ct).ConfigureAwait(false);
                        await TrySmallChunkAsync(src, res, s + 3, 3, ct).ConfigureAwait(false);
                        await TrySmallChunkAsync(src, res, s + 6, 4, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        for (int i = 0; i < len; i++)
                            res[s + i] = await SingleTranslateAsync(src[s + i], s + i, ct).ConfigureAwait(false);
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            for (int i = 0; i < n; i++) res[i] ??= src[i];
            return res.ToList();
        }

        private async Task TrySmallChunkAsync(List<string> src, string[] res, int start, int len, CancellationToken ct)
        {
            var lines = src.GetRange(start, len);
            var outSmall = await BatchTranslateAsync(lines, "B334", start, ct).ConfigureAwait(false);
            if (outSmall != null)
                Array.Copy(outSmall, 0, res, start, len);
            else
                for (int i = 0; i < len; i++)
                    res[start + i] = await SingleTranslateAsync(src[start + i], start + i, ct).ConfigureAwait(false);
        }

        private Task<string[]> BatchTranslateAsync(List<string> lines, string tag, int off, CancellationToken ct)
        {
            string sys = BuildSystemPrompt(
                $"Translate the following text into {cfg.TargetLang}. Keep the same number of lines and preserve punctuation and symbols. Do not insert newlines.");
            string user = string.Join("\n", lines);

            return EnqueueHttp(async () =>
            {
                string raw = await CallLLMAsync(sys, user, tag, off, ct).ConfigureAwait(false);
                if (raw == null) return null;

                raw = Regex.Replace(raw, @"\s*\r?\n\s*", "\n").TrimEnd();
                var outs = raw.Split('\n').Select(s => s).ToArray();

                if (outs.Length != lines.Count) return null;

                for (int i = 0; i < outs.Length; i++)
                {
                    if (outs[i].IndexOf('\n') >= 0 || outs[i].IndexOf('\r') >= 0)
                        outs[i] = Regex.Replace(outs[i], @"[\r\n]+", " ");

                    if (IsHallucination(outs[i], lines[i])) return null;
                }
                return outs;
            }, ct);
        }

        private Task<string> SingleTranslateAsync(string text, int idx, CancellationToken ct)
        {
            return EnqueueHttp(async () =>
            {
                int fail = 0;
                while (true)
                {
                    string sys = BuildSystemPrompt($"Translate this line into {cfg.TargetLang}. Keep punctuation and symbols and do not insert newlines.");
                    string res = (await CallLLMAsync(sys, text, "S1", idx, ct).ConfigureAwait(false));

                    if (!string.IsNullOrEmpty(res) && (res.IndexOf('\n') >= 0 || res.IndexOf('\r') >= 0))
                        res = null;

                    if (!string.IsNullOrWhiteSpace(res) && !IsHallucination(res, text))
                        return res;

                    if (++fail > SINGLE_HALLUCINATION_MAX) return text; // 回退原文
                    Log.Warn($"[S1] idx={idx} 幻觉/空结果 第{fail}次重试");
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }, ct);
        }

        private static async Task<T> EnqueueHttp<T>(Func<Task<T>> inner, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await HttpGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await inner().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransientNetworkError(ex) && ++attempt <= HTTP_MAX_RETRIES)
                {
                    var delayMs = (int)Math.Min(4000, 300 * Math.Pow(2, attempt - 1));
                    Log.Warn($"[HTTP] 瞬时错误，重试 {attempt}/{HTTP_MAX_RETRIES}，等待 {delayMs}ms。{ex.Message}");
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                finally
                {
                    HttpGate.Release();
                }

                throw new Exception("HTTP 调用失败且不再重试。");
            }
        }

        private static bool IsTransientNetworkError(Exception ex)
        {
            if (ex is TimeoutException) return true;

            if (ex is WebException wex)
            {
                if (wex.Status == WebExceptionStatus.Timeout ||
                    wex.Status == WebExceptionStatus.ConnectionClosed ||
                    wex.Status == WebExceptionStatus.ConnectFailure ||
                    wex.Status == WebExceptionStatus.NameResolutionFailure)
                    return true;

                if (wex.Response is HttpWebResponse resp)
                {
                    int code = (int)resp.StatusCode;
                    if (code == 429 || code >= 500) return true;
                }
            }
            return false;
        }

        private object BuildBodyForRequest(string sys, string user)
        {
            if (cfg.UseOpenAI)
            {
                return new
                {
                    model = cfg.Model,
                    messages = new[] {
                        new { role = "system", content = sys },
                        new { role = "user",   content = user }
                    },
                    temperature = cfg.Temperature,
                    top_p = cfg.TopP,
                    frequency_penalty = cfg.FrequencyPenalty,
                    presence_penalty = cfg.PresencePenalty,
                    stream = false,
                    max_tokens = cfg.NPredict > 0 ? (int?)cfg.NPredict : null
                };
            }
            else
            {
                return new
                {
                    model = cfg.Model,
                    messages = new[] {
                        new { role = "system", content = sys },
                        new { role = "user",   content = user }
                    },
                    temperature = cfg.Temperature,
                    top_p = cfg.TopP,
                    stream = false,

                    max_tokens = cfg.NPredict > 0 ? (int?)cfg.NPredict : null,
                    n_predict = cfg.NPredict > 0 ? (int?)cfg.NPredict : null,
                    repeat_penalty = cfg.RepetitionPenalty,
                    repetition_penalty = cfg.RepetitionPenalty,
                    frequency_penalty = cfg.FrequencyPenalty
                };
            }
        }

        private async Task<string> CallLLMAsync(string sys, string user, string tag, int off, CancellationToken outerCt)
        {
            var bodyObj = BuildBodyForRequest(sys, user);
            string jsonBody = Newtonsoft.Json.JsonConvert.SerializeObject(bodyObj);

            var prev = (sys + "\n" + user);
            if (prev.Length > 300) prev = prev.Substring(0, 300) + "...";
            Log.Info($"[SEND] {tag} off={off}\n{prev}");

            using (var wc = new WebClientEx { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                if (cfg.UseOpenAI && !string.IsNullOrWhiteSpace(cfg.ApiKey))
                    wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + cfg.ApiKey;

                var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(HTTP_TIMEOUT_MS);

                try
                {
                    var sendTask = wc.UploadStringTaskAsync(cfg.ApiUrl, jsonBody);
                    using (cts)
                    {
                        var done = await Task.WhenAny(sendTask, Task.Delay(Timeout.Infinite, cts.Token))
                                             .ConfigureAwait(false);
                        if (done != sendTask) throw new TimeoutException("HTTP timeout");
                    }
                    string rsp = sendTask.Result;
                    string previewRsp = rsp.Length > 2048 ? rsp.Substring(0, 2048) + "..." : rsp;
                    Log.Info($"[RECV] {tag} off={off} bytes={rsp.Length}\n{previewRsp}");

                    var jt = JObject.Parse(rsp);
                    return (string)jt["choices"]?[0]?["message"]?["content"]
                        ?? (string)jt["choices"]?[0]?["text"]
                        ?? (string)jt["content"];
                }
                catch (Exception ex) when (ex is TimeoutException || ex is WebException)
                {
                    Log.Warn($"[TIMEOUT] {tag} off={off}: {ex.Message}");
                    throw;
                }
            }
        }

        /* ====== 你的“幻觉检测”（原样保留） ====== */
        private static readonly string[] SuspiciousWords = { "千岁", "千景", "张三" };

        private static bool IsHallucination(string tr, string or)
        {
            if (string.IsNullOrWhiteSpace(tr)) return true;
            if (tr.Contains("\\n") || Regex.IsMatch(tr, @"%\\d+;")) return true;

            foreach (var w in SuspiciousWords)
                if (tr.Contains(w) && !or.Contains(w)) return true;

            string ct = Regex.Replace(tr, "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF]", "");
            string co = Regex.Replace(or, "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF]", "");
            if (co.Length == 0) return false;

            double r = (double)ct.Length / co.Length;
            if (r > 3.5 || r < 0.15) return true;
            return (tr.Length - ct.Length) > 3 * ct.Length;
        }

        private static bool HasAncestor(HtmlNode n, HashSet<string> tags)
        {
            for (var p = n.ParentNode; p != null; p = p.ParentNode)
                if (p.NodeType == HtmlNodeType.Element && tags.Contains(p.Name)) return true;
            return false;
        }

        private static bool HasAncestor(HtmlNode node, string tagName)
        {
            for (var p = node.ParentNode; p != null; p = p.ParentNode)
                if (p.NodeType == HtmlNodeType.Element &&
                    string.Equals(p.Name, tagName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}