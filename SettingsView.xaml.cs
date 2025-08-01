using System.Windows;
using System.Windows.Controls;

namespace DescriptionTranslator
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            // 若 DataContext 在 Loaded 之后才设定，也能回显
            this.DataContextChanged += (s, e) =>
            {
                if (DataContext is TranslatorConfig cfg && ApiKeyBox != null)
                {
                    ApiKeyBox.Password = cfg.ApiKey ?? string.Empty;
                }
            };
        }

        private TranslatorConfig Cfg => DataContext as TranslatorConfig;

        // 打开设置页时，把已保存的 ApiKey 填回 PasswordBox（显示为圆点）
        private void ApiKeyBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && Cfg != null)
            {
                box.Password = Cfg.ApiKey ?? string.Empty;
            }
        }

        // 用户修改时，立即写回配置对象；点“保存”时 Playnite 会持久化到 config.json
        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && Cfg != null)
            {
                Cfg.ApiKey = box.Password;
            }
        }
    }
}