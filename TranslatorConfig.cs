using Playnite.SDK;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace DescriptionTranslator
{
    public class TranslatorConfig : ObservableObject, ISettings
    {
        // —— 语言 & 端点 —— //
        public string SourceLang { get; set; } = "auto";
        public string TargetLang { get; set; } = "zh";
        public bool UseOpenAI { get; set; } = true;
        public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "gpt-3.5-turbo";

        // —— 提示词 —— //
        public string SystemPrompt { get; set; } =
            "You translate ${src} HTML to ${dst}. Only translate text nodes; keep tags/attributes unchanged. " +
            "Return each line as [i]translation matching numbering.";

        // —— 采样参数 —— //
        public double Temperature { get; set; } = 0.2;
        public double TopP { get; set; } = 0.9;
        public int NPredict { get; set; } = 0;      // llama.cpp 用
        public double RepetitionPenalty { get; set; } = 1.1;    // llama.cpp 用
        public double FrequencyPenalty { get; set; } = 0.0;

        // —— 保存回调 —— //
        private System.Action<TranslatorConfig> saver;
        public void AttachSaver(System.Action<TranslatorConfig> s) => saver = s;

        // —— ISettings —— //
        private TranslatorConfig snapshot;
        public void BeginEdit() => snapshot = (TranslatorConfig)MemberwiseClone();
        public void CancelEdit() { if (snapshot != null) CopyFrom(snapshot); snapshot = null; }
        public void EndEdit() { saver?.Invoke(this); snapshot = null; }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (UseOpenAI && string.IsNullOrWhiteSpace(ApiKey))
                errors.Add("⚠ 已勾选 UseOpenAI 但 ApiKey 为空：调用 OpenAI 会失败。");
            if (string.IsNullOrWhiteSpace(ApiUrl))
                errors.Add("⚠ ApiUrl 为空：请填写 OpenAI 或自建端点。");
            return true;                    // 始终返回 true → 设置页总是显示
        }

        [OnDeserialized] internal void OnDeserialized(StreamingContext _) { }

        private void CopyFrom(TranslatorConfig o)
        {
            SourceLang = o.SourceLang; TargetLang = o.TargetLang; UseOpenAI = o.UseOpenAI;
            ApiUrl = o.ApiUrl; ApiKey = o.ApiKey; Model = o.Model;
            SystemPrompt = o.SystemPrompt;
            Temperature = o.Temperature; TopP = o.TopP;
            NPredict = o.NPredict; RepetitionPenalty = o.RepetitionPenalty;
            FrequencyPenalty = o.FrequencyPenalty;
        }
    }
}