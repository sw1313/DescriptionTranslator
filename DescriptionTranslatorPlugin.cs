using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
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

            // ① 告诉 Playnite「我有设置页」—— 通过 Properties.HasSettings
            //    （不要 override；直接在构造函数里赋值）
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            // ② 读取（或创建）配置对象
            cfg = LoadPluginSettings<TranslatorConfig>() ?? new TranslatorConfig();

            // ③ 绑定保存回调：这样在设置页点“保存”时，配置会真正写入 config.json
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

        // ───── 游戏右键菜单：单个翻译 ─────
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                MenuSection = "@DescriptionTranslator",
                Description = "翻译此游戏描述",
                Action = _2 => TranslateGames(args.Games)
            };
        }

        private void TranslateGames(IEnumerable<Game> games)
        {
            var translator = new HtmlTranslator(cfg);

            foreach (var g in games)
            {
                if (string.IsNullOrWhiteSpace(g.Description))
                    continue;

                try
                {
                    g.Description = translator.TranslateHtml(g.Description);
                    api.Database.Games.Update(g);
                }
                catch (Exception ex)
                {
                    Log.Error($"[{g.Name}] 翻译失败：{ex}");
                }
            }

            api.Notifications.Add(new NotificationMessage(
                Guid.NewGuid().ToString(), "描述翻译已完成", NotificationType.Info));
        }

        // ───── 设置序列化 & 视图 ─────
        public override ISettings GetSettings(bool _) => cfg;
        public override UserControl GetSettingsView(bool _) => new SettingsView { DataContext = cfg };
    }
}