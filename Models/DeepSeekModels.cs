using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ======== API 请求模型 ========
    public class DeepSeekChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "deepseek-v4-pro";

        [JsonPropertyName("messages")]
        public List<ChatApiMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = true;

        // 思考模式控制 (V4 新参数)
        [JsonPropertyName("thinking")]
        public ThinkingControl? Thinking { get; set; }

        // 推理强度 (V4 新参数, 仅思考模式开启时有效)
        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; set; }

        // 注意: 思考模式下 temperature/top_p 等参数不生效，此处省略
    }

    public class ThinkingControl
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "enabled"; // "enabled" 或 "disabled"
    }

    // 对话消息（API 格式）
    public class ChatApiMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningContent { get; set; }
    }

    // ======== 非流式响应模型 ========
    public class DeepSeekChatResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<DeepSeekChoice> Choices { get; set; } = new();

        [JsonPropertyName("usage")]
        public DeepSeekUsage? Usage { get; set; }
    }

    public class DeepSeekChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public DeepSeekMessage? Message { get; set; }

        [JsonPropertyName("delta")]
        public DeepSeekDelta? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    public class DeepSeekMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }
    }

    public class DeepSeekDelta
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }
    }

    public class DeepSeekUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    // ======== 流式响应行 ========
    public class DeepSeekStreamChunk
    {
        [JsonPropertyName("choices")]
        public List<DeepSeekChoice> Choices { get; set; } = new();
    }

    // ======== 视图模型用的 UI 消息模型（添加时间戳等） ========
    [DataContract]
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _role = "user";
        private string _content = string.Empty;
        private string _reasoningContent = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isStreaming;

        [DataMember]
        public string Role
        {
            get => _role;
            set => SetProperty(ref _role, value);
        }

        [DataMember]
        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }

        /// <summary>
        /// 思考模式下的推理过程内容，以单行滚动形式展示。
        /// 非思考模式下此属性为空。
        /// </summary>
        [DataMember]
        public string ReasoningContent
        {
            get => _reasoningContent;
            set => SetProperty(ref _reasoningContent, value);
        }

        [DataMember]
        public DateTime Timestamp
        {
            get => _timestamp;
            set => SetProperty(ref _timestamp, value);
        }

        [DataMember]
        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        // INotifyPropertyChanged 实现
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}