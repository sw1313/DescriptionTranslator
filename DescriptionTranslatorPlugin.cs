using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

            // 提升 .NET 的同主机并发连接上限（默认是 2，会“假阻塞”）
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
        /// 后台线程 + 可取消全局进度；内部使用**有界并发**翻译多个游戏。
        /// </summary>
        private void TranslateGames(IEnumerable<Game> games)
        {
            var gameList = games.Where(g => !string.IsNullOrWhiteSpace(g.Description)).ToList();
            if (gameList.Count == 0)
            {
                api.Dialogs.ShowMessage("所选游戏均无描述，无需翻译。", "DescriptionTranslator");
                return;
            }

            // ActivateGlobalProgress 的回调是同步 Action。
            // 我们在里面自己做 Task.WhenAll().GetAwaiter().GetResult() 来等待并发完成。
            api.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = $"正在翻译描述… (0 / {gameList.Count})";
                progress.IsIndeterminate = false;

                var translator = new HtmlTranslator(cfg);
                int done = 0;

                // 游戏级有界并发（与 HTTP 并发/服务端 slots 保持近似）
                int parallelGames = Math.Max(1, cfg.ChunkConcurrency);
                var gate = new SemaphoreSlim(parallelGames, parallelGames);

                var tasks = gameList.Select(async g =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (progress.CancelToken.IsCancellationRequested) return;

                        string newHtml = await translator.TranslateHtmlAsync(g.Description, progress.CancelToken).ConfigureAwait(false);
                        g.Description = newHtml;
                        api.Database.Games.Update(g);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[{g.Name}] 翻译失败：{ex}");
                    }
                    finally
                    {
                        Interlocked.Increment(ref done);
                        progress.Text = $"正在翻译描述… ({done} / {gameList.Count})";
                        gate.Release();
                    }
                }).ToArray();

                Task.WhenAll(tasks).GetAwaiter().GetResult();

                api.Notifications.Add(new NotificationMessage(
                    Guid.NewGuid().ToString(),
                    "描述翻译任务已结束（成功或已取消）。",
                    NotificationType.Info));

            }, new GlobalProgressOptions("DescriptionTranslator", true)
            {
                IsIndeterminate = false
            });
        }

        public override ISettings GetSettings(bool _) => cfg;
        public override UserControl GetSettingsView(bool _) => new SettingsView { DataContext = cfg };
    }
}