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

            // 提升 .NET 的同主机并发连接上限（默认是 2）
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 128);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            Properties = new GenericPluginProperties { HasSettings = true };

            cfg = LoadPluginSettings<TranslatorConfig>() ?? new TranslatorConfig();
            cfg.AttachSaver(s => SavePluginSettings(s));
        }

        // ───── 主菜单：批量翻译 ─────
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs _)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@DescriptionTranslator",
                Description = "批量翻译所有游戏描述",
                Action = _2 => TranslateGames(api.Database.Games.ToList())
            };
        }

        // ───── 右键菜单：单个翻译 ─────
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
        /// 通过 TEMP HTML 中转进行翻译；显示“不确定进度”动画；UI 线程分批写库，适配 SMB/NAS。
        /// </summary>
        private void TranslateGames(IEnumerable<Game> games)
        {
            var gameList = games.Where(g => !string.IsNullOrWhiteSpace(g.Description)).ToList();
            if (gameList.Count == 0)
            {
                api.Dialogs.ShowMessage("所选游戏均无描述，无需翻译。", "DescriptionTranslator");
                return;
            }

            // TEMP 根目录（缓存）
            var tempRoot = Path.Combine(Path.GetTempPath(), "Playnite.DescriptionTranslator");
            try { Directory.CreateDirectory(tempRoot); }
            catch (Exception ex) { Log.Error($"创建临时目录失败：{ex}"); }

            // ✅ 不确定进度条（左右平移）
            var options = new GlobalProgressOptions("DescriptionTranslator", true)
            {
                IsIndeterminate = true
            };

            api.Dialogs.ActivateGlobalProgress(async progress =>
            {
                // 再保险：确保是“左右平移”模式
                progress.IsIndeterminate = true;
                progress.Text = "正在准备翻译…";

                var translator = new HtmlTranslator(cfg);
                int done = 0;

                // 游戏级有界并发
                int parallelGames = Math.Max(1, cfg.ChunkConcurrency);
                var gate = new SemaphoreSlim(parallelGames, parallelGames);

                // 后台翻译（通过临时 HTML 文件），结果暂存内存
                var results = new ConcurrentDictionary<Guid, string>();
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

                var tasks = gameList.Select(async g =>
                {
                    await gate.WaitAsync(progress.CancelToken).ConfigureAwait(false);
                    try
                    {
                        if (progress.CancelToken.IsCancellationRequested) return;

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

                        // 2) 翻译 HTML（保持原排版/链接）
                        string newHtml = await translator
                            .TranslateHtmlFileAsync(srcPath, dstPath, progress.CancelToken)
                            .ConfigureAwait(false);

                        // 3) 暂存结果
                        results[g.Id] = newHtml;
                    }
                    catch (OperationCanceledException) { /* 用户取消 */ }
                    catch (Exception ex)
                    {
                        Log.Error($"[{g.Name}] 翻译失败：{ex}");
                    }
                    finally
                    {
                        gate.Release();

                        // 只更新文本，不使用百分比/数值，避免进度条变为确定模式
                        int cur = Interlocked.Increment(ref done);
                        progress.Text = $"翻译中…（{cur} / {gameList.Count}）";
                    }
                }).ToArray();

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // ===== 分批写库（UI 线程；小批次 + 短暂让步，避免 UI 长时间占用）=====
                progress.Text = "正在写入结果…";
                const int WRITE_CHUNK = 10; // SMB/NAS 建议更小（5~10）
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

                    // 让 UI 消息泵有切换时间片，防止 UI“未响应”
                    await Task.Delay(1);
                }

                await api.MainView.UIDispatcher.InvokeAsync(() =>
                {
                    api.Notifications.Add(new NotificationMessage(
                        Guid.NewGuid().ToString(),
                        "描述翻译任务已结束（成功或已取消）。",
                        NotificationType.Info));
                });

            }, options);
        }

        public override ISettings GetSettings(bool _) => cfg;
        public override UserControl GetSettingsView(bool _) => new SettingsView { DataContext = cfg };
    }
}