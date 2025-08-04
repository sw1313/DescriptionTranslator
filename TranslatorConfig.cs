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
        public string Model { get; set; } = "gpt-4o-mini";   // 可按需改成你的默认模型

        // —— 自定义系统提示（支持 ${src}/${dst}）—— //
        public string SystemPrompt { get; set; } = "";

        // —— 采样 / 生成参数（OpenAI 风格默认）—— //
        public double Temperature { get; set; } = 0.7;  // OpenAI 默认
        public double TopP { get; set; } = 1.0;  // OpenAI 默认
        /// <summary>0 表示不指定，让服务端自行决定；>0 时在 OpenAI 模式下作为 max_tokens</summary>
        public int NPredict { get; set; } = 0;

        /// <summary>OpenAI: [-2,2]，默认 0</summary>
        public double FrequencyPenalty { get; set; } = 0.0;
        /// <summary>OpenAI: [-2,2]，默认 0</summary>
        public double PresencePenalty { get; set; } = 0.0;

        /// <summary>自建端点（llama.cpp 等）常用：重复惩罚；OpenAI 模式下不会发送</summary>
        public double RepetitionPenalty { get; set; } = 1.0;

        // —— 并发控制 —— //
        public int HttpConcurrency { get; set; } = 3;
        public int ChunkConcurrency { get; set; } = 3;

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

            if (HttpConcurrency < 1) HttpConcurrency = 1;
            if (ChunkConcurrency < 1) ChunkConcurrency = 1;

            if (NPredict < 0) NPredict = 0;                         // 0 表示不指定
            if (Temperature < 0) Temperature = 0;                   // OpenAI [0,2]
            if (Temperature > 2) Temperature = 2;
            if (TopP < 0) TopP = 0; if (TopP > 1) TopP = 1;          // OpenAI [0,1]
            if (FrequencyPenalty < -2) FrequencyPenalty = -2;
            if (FrequencyPenalty > 2) FrequencyPenalty = 2;
            if (PresencePenalty < -2) PresencePenalty = -2;
            if (PresencePenalty > 2) PresencePenalty = 2;

            if (RepetitionPenalty < 0) RepetitionPenalty = 0;       // 自建端点用

            return true;
        }

        [OnDeserialized] internal void OnDeserialized(StreamingContext _) { }

        private void CopyFrom(TranslatorConfig o)
        {
            SourceLang = o.SourceLang;
            TargetLang = o.TargetLang;
            UseOpenAI = o.UseOpenAI;
            ApiUrl = o.ApiUrl;
            ApiKey = o.ApiKey;
            Model = o.Model;
            SystemPrompt = o.SystemPrompt;

            Temperature = o.Temperature;
            TopP = o.TopP;
            NPredict = o.NPredict;
            FrequencyPenalty = o.FrequencyPenalty;
            PresencePenalty = o.PresencePenalty;
            RepetitionPenalty = o.RepetitionPenalty;

            HttpConcurrency = o.HttpConcurrency;
            ChunkConcurrency = o.ChunkConcurrency;
        }
    }
}