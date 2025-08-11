using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace DescriptionTranslator
{
    public class DescriptionTranslatorPlugin : GenericPlugin
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private readonly TranslatorConfig cfg;

        // 与 extension.yaml 的 Id 必须一致
        public override Guid Id => Guid.Parse("5B22F060-3027-4F4B-8C38-96F9E0F6D1C4");

        public DescriptionTranslatorPlugin(IPlayniteAPI api) : base(api)
        {
            this.api = api;

            // 提升 .NET 同主机并发连接上限
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 128);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            Properties = new GenericPluginProperties { HasSettings = true };

            cfg = LoadPluginSettings<TranslatorConfig>() ?? new TranslatorConfig();
            cfg.AttachSaver(s => SavePluginSettings(s));
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs _)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@DescriptionTranslator",
                Description = "批量翻译所有游戏描述",
                Action = _2 => TranslateGames(api.Database.Games.ToList())
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                MenuSection = "@DescriptionTranslator",
                Description = "翻译此游戏描述",
                Action = _2 => TranslateGames(args.Games)
            };
        }

        /// <summary>
        /// TEMP HTML 中转 + 不确定进度 + 分批写库（NAS/SMB 友好）
        /// </summary>
        private void TranslateGames(IEnumerable<Game> games)
        {
            var gameList = games.Where(g => !string.IsNullOrWhiteSpace(g.Description)).ToList();
            if (gameList.Count == 0)
            {
                api.Dialogs.ShowMessage("所选游戏均无描述，无需翻译。", "DescriptionTranslator");
                return;
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "Playnite.DescriptionTranslator");
            try { Directory.CreateDirectory(tempRoot); } catch { }

            var options = new GlobalProgressOptions("DescriptionTranslator", true)
            {
                IsIndeterminate = true
            };

            api.Dialogs.ActivateGlobalProgress(async progress =>
            {
                progress.IsIndeterminate = true;
                progress.Text = "正在准备翻译…";

                var translator = new HtmlTranslator(cfg);
                int done = 0;

                int parallelGames = Math.Max(1, cfg.ChunkConcurrency);
                var gate = new SemaphoreSlim(parallelGames, parallelGames);

                var results = new ConcurrentDictionary<Guid, string>();
                var utf8NoBom = new UTF8Encoding(false);

                var tasks = gameList.Select(async g =>
                {
                    await gate.WaitAsync(progress.CancelToken).ConfigureAwait(false);
                    try
                    {
                        if (progress.CancelToken.IsCancellationRequested) return;

                        // === 新增：整页目标语言占比检测（仅看可翻译纯文本；跳过代码/链接）===
                        try
                        {
                            if (translator.ShouldSkipByLanguage(g.Description, out double cov))
                            {
                                Log.Info($"[Skip] {g.Name} 覆盖度 {cov:P1} 已为目标语言，跳过翻译。");
                                int curSkipped = Interlocked.Increment(ref done);
                                progress.Text = $"已跳过（≥90% 目标语言）：{curSkipped} / {gameList.Count}";
                                return;
                            }
                        }
                        catch (Exception lex)
                        {
                            Log.Warn($"[{g.Name}] 语言覆盖度检测失败，继续翻译：{lex.Message}");
                        }

                        // 1) 原描述 → TEMP 源 HTML（原封不动）
                        var baseName = g.Id.ToString("N");
                        var srcPath = Path.Combine(tempRoot, $"{baseName}.orig.html");
                        var dstPath = Path.Combine(tempRoot, $"{baseName}.trans.html");

                        try
                        {
                            File.WriteAllText(srcPath, g.Description, utf8NoBom);
                        }
                        catch (Exception ioex)
                        {
                            Log.Error($"[{g.Name}] 写入临时源文件失败：{ioex}");
                            return;
                        }

                        // 2) 翻译（内部：只改文本，不动标签/URL）
                        string newHtml = await translator.TranslateHtmlFileAsync(srcPath, dstPath, progress.CancelToken)
                                                         .ConfigureAwait(false);

                        results[g.Id] = newHtml;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log.Error($"[{g.Name}] 翻译失败：{ex}");
                    }
                    finally
                    {
                        gate.Release();
                        int cur = Interlocked.Increment(ref done);
                        progress.Text = $"处理进度…（{cur} / {gameList.Count}）";
                    }
                }).ToArray();

                await Task.WhenAll(tasks).ConfigureAwait(false);

                progress.Text = "正在写入结果…";
                const int WRITE_CHUNK = 10; // SMB/NAS 可调 5~10
                var toWrite = gameList
                    .Where(g => results.ContainsKey(g.Id))
                    .Select(g => (Game: g, Html: results[g.Id]))
                    .ToList();

                int wrote = 0;
                for (int i = 0; i < toWrite.Count; i += WRITE_CHUNK)
                {
                    if (progress.CancelToken.IsCancellationRequested) break;

                    var slice = toWrite.Skip(i).Take(WRITE_CHUNK).ToList();

                    await api.MainView.UIDispatcher.InvokeAsync(() =>
                    {
                        using (api.Database.BufferedUpdate())
                        {
                            foreach (var (game, html) in slice)
                            {
                                game.Description = html;
                                api.Database.Games.Update(game);
                            }
                        }
                    });

                    wrote += slice.Count;
                    progress.Text = $"正在写入结果…（{wrote} / {toWrite.Count}）";
                    await Task.Delay(1);
                }

                await api.MainView.UIDispatcher.InvokeAsync(() =>
                {
                    api.Notifications.Add(new NotificationMessage(
                        Guid.NewGuid().ToString(),
                        "描述翻译任务已结束（成功/跳过/已取消）。",
                        NotificationType.Info));
                });

            }, options);
        }

        public override ISettings GetSettings(bool _) => cfg;
        public override UserControl GetSettingsView(bool _) => new SettingsView { DataContext = cfg };
    }
}