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
    /*────────────────── 1. 带超时 WebClient ──────────────────*/
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

    /*────────────────── 2. HtmlTranslator ──────────────────*/
    internal class HtmlTranslator
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly TranslatorConfig cfg;

        // 并发 / 超时 / 幻觉策略（保持原有模式）
        private const int WORKER_COUNT = 3;                // HTTP 并发上限
        private const int HTTP_TIMEOUT_MS = 120_000;       // 单次 HTTP 超时
        private const int SINGLE_HALLUCINATION_MAX = 2;    // 单行幻觉最多允许 2 次
        private const int HTTP_MAX_RETRIES = 3;            // 瞬时错误最大重试次数

        private static readonly SemaphoreSlim HttpGate = new SemaphoreSlim(WORKER_COUNT, WORKER_COUNT);

        public HtmlTranslator(TranslatorConfig c) => cfg = c;

        /*──────── 补齐：拼接 System prompt ────────*/
        private string BuildSystemPrompt(string fallback)
        {
            // 允许在配置里写模板：支持 ${src} / ${dst}
            var sp = (cfg.SystemPrompt ?? "").Trim();
            if (!string.IsNullOrEmpty(sp))
            {
                return sp.Replace("${src}", cfg.SourceLang ?? "auto")
                         .Replace("${dst}", cfg.TargetLang ?? "zh");
            }
            return fallback;
        }

        /*──────── 新增：文件级 API（读取 -> 翻译 -> 写回） ────────*/
        public async Task<string> TranslateHtmlFileAsync(string inputPath, string outputPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            string html;
            try
            {
                // 原封不动读取（UTF-8；如需特定编码可扩展）
                html = File.ReadAllText(inputPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Error($"读取源 HTML 失败：{inputPath}\n{ex}");
                throw;
            }

            var translated = await TranslateHtmlAsync(html, ct).ConfigureAwait(false);

            try
            {
                // 写出翻译结果（不加 BOM）
                var utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(outputPath, translated, utf8NoBom);
            }
            catch (Exception ex)
            {
                Log.Error($"写入翻译 HTML 失败：{outputPath}\n{ex}");
                // 写文件失败不影响返回给上层
            }

            return translated;
        }

        /*──────── 3. TranslateHtmlAsync ────────*/
        public async Task<string> TranslateHtmlAsync(string html, CancellationToken ct = default)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 跳过的标签（保持原排版与代码区域，避免破坏 URL）
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "script","style","noscript","code","pre","kbd","samp","var" };

            // A) 处理含 <br> 的元素（行对齐；不触碰含 <a> 的段）
            var processed = new HashSet<HtmlNode>();
            var brSegs = new List<(HtmlNode el, string raw, string tail, string plain, int? idx)>();
            int nextIdx = 0;

            var brElems = doc.DocumentNode.Descendants()
                .Where(e => e.NodeType == HtmlNodeType.Element
                            && !skip.Contains(e.Name)
                            && e.InnerHtml.IndexOf("<br", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var el in brElems)
            {
                var parts = Regex.Split(el.InnerHtml, @"<\s*br\s*/?>", RegexOptions.IgnoreCase);
                foreach (string seg in parts)
                {
                    string tail = Regex.Match(seg, @"(<[^>]+>)+\s*$").Value;
                    string inner = seg.Substring(0, seg.Length - tail.Length);
                    string plain = WebUtility.HtmlDecode(Regex.Replace(inner, "<[^>]+>", ""))
                                   .Replace("\r", " ").Replace("\n", " ").Trim();

                    bool hasA = seg.IndexOf("<a ", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hasA && plain.Length > 0)
                        brSegs.Add((el, seg, tail, plain, nextIdx++));
                    else
                        brSegs.Add((el, seg, tail, null, null));
                }
                foreach (var n in el.DescendantsAndSelf()) processed.Add(n);
            }

            List<string> transBr = null;
            if (nextIdx > 0)
            {
                var src = new string[nextIdx];
                foreach (var s in brSegs) if (s.idx.HasValue) src[s.idx.Value] = s.plain;
                transBr = await TranslateWithDegradeAsync(src.ToList(), ct).ConfigureAwait(false);
            }

            if (transBr != null)
            {
                foreach (var g in brSegs.GroupBy(s => s.el))
                {
                    var rebuilt = new List<string>();
                    foreach (var s in g)
                        rebuilt.Add(s.idx == null ? s.raw
                            : WebUtility.HtmlEncode(CleanMarkers(transBr[s.idx.Value])));
                    g.Key.InnerHtml = string.Join("<br>", rebuilt);
                }
            }

            // B) 普通文本节点（不处理链接文本）
            var textNodes = doc.DocumentNode.Descendants()
                .Where(n => n.NodeType == HtmlNodeType.Text
                            && !string.IsNullOrWhiteSpace(n.InnerText)
                            && !HasAncestor(n, skip)
                            && !HasAncestor(n, "a")
                            && !processed.Contains(n.ParentNode))
                .ToList();

            if (textNodes.Count > 0)
            {
                var src2 = textNodes.Select(n => WebUtility.HtmlDecode(n.InnerText)
                                            .Replace("\r", " ").Replace("\n", " ").Trim()).ToList();
                var dst2 = await TranslateWithDegradeAsync(src2, ct).ConfigureAwait(false);

                for (int i = 0; i < textNodes.Count; i++)
                    textNodes[i].InnerHtml = WebUtility.HtmlEncode(CleanMarkers(dst2[i]));
            }

            return doc.DocumentNode.InnerHtml;
        }

        /*──────── 4. 分段降级：10行 → 3-3-4 → 单行 ────────*/
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

        /*──────── 5. 批量 / 单行 执行（保持原模式） ────────*/
        private Task<string[]> BatchTranslateAsync(List<string> lines, string tag, int off, CancellationToken ct)
        {
            string sys = BuildSystemPrompt(
                $"Translate the following text into {cfg.TargetLang}. Keep the same number of lines and preserve line breaks.");
            string user = string.Join("\n", lines);

            return EnqueueHttp(async () =>
            {
                string raw = await CallLLMAsync(sys, user, tag, off, ct).ConfigureAwait(false);
                if (raw == null) return null;

                raw = Regex.Replace(raw, @"\s*\r?\n\s*", "\n").Trim();
                var outs = raw.Split('\n').Select(s => s.Trim()).ToArray();
                if (outs.Length != lines.Count) return null;

                for (int i = 0; i < outs.Length; i++)
                    if (IsHallucination(outs[i], lines[i])) return null;

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
                    string sys = BuildSystemPrompt($"Translate this line into {cfg.TargetLang}.");
                    string res = (await CallLLMAsync(sys, text, "S1", idx, ct).ConfigureAwait(false))?.Trim();

                    if (!string.IsNullOrWhiteSpace(res) && !IsHallucination(res, text))
                        return res;

                    if (++fail > SINGLE_HALLUCINATION_MAX) return text;   // 最终放弃
                    Log.Warn($"[S1] idx={idx} 幻觉/空结果 第{fail}次重排尾");
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }, ct);
        }

        /*──────── 6. 有界重试 + 并发闸门（稳定） ────────*/
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

        /*──────── 7. HTTP 调用（按 UseOpenAI 动态组装参数） ────────*/
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

            // 预览日志（截断）
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

                    // 兼容 chat/completions 与若干兼容实现
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

        /*──────── 8. 工具（轻度清洗；不改排版/URL） ────────*/
        private static bool IsHallucination(string tr, string or)
        {
            if (string.IsNullOrWhiteSpace(tr)) return true;
            if (tr.Contains("\\n") || Regex.IsMatch(tr, @"%\\d+;")) return true;

            // 过度长度比/异常字符占比
            string ct = Regex.Replace(tr, "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF]", "");
            string co = Regex.Replace(or, "[^a-zA-Z0-9\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF]", "");
            if (co.Length == 0) return false;

            double r = (double)ct.Length / co.Length;
            if (r > 3.5 || r < 0.15) return true;
            return (tr.Length - ct.Length) > 3 * ct.Length;
        }

        private static string CleanMarkers(string dst)
        {
            if (string.IsNullOrEmpty(dst)) return dst;
            // 仅压缩多余空白；不改结构不改链接
            return Regex.Replace(dst, @"\s{2,}", " ").Trim();
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