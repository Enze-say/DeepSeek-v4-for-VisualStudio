using System;
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

        // 最大输出 token 数（用于校验 ping）
        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

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
        private string _htmlContent = string.Empty;
        private string _htmlDataUri = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isStreaming;
        private bool _isRendered;

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

        /// <summary>
        /// Markdown 渲染后的 HTML 内容（带深色主题 CSS）。
        /// 由 MarkdownRenderService 在流式完成后生成。
        /// 为空时表示尚未渲染或正在流式传输中。
        /// 注意：此属性较大（~60KB），不通过 RPC 同步到 UI，仅用于本地持久化。
        /// </summary>
        public string HtmlContent
        {
            get => _htmlContent;
            set => SetProperty(ref _htmlContent, value);
        }

        /// <summary>
        /// data:text/html;base64,... 格式的 Data URI，可直接绑定到 WebView2.Source。
        /// 由 MarkdownRenderService.ConvertToDataUri() 生成。
        /// 注意：此属性较大（~60KB），不通过 RPC 同步到 UI，仅用于本地持久化。
        /// </summary>
        public string HtmlDataUri
        {
            get => _htmlDataUri;
            set => SetProperty(ref _htmlDataUri, value);
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

        /// <summary>
        /// 指示 Markdown 是否已完成 HTML 渲染。
        /// 用于 XAML 触发器切换 TextBox → WebView2 显示。
        /// </summary>
        [DataMember]
        public bool IsRendered
        {
            get => _isRendered;
            set => SetProperty(ref _isRendered, value);
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

    // ======== 会话模型（多轮对话支持） ========

    /// <summary>
    /// 表示一次对话会话，包含多条消息和元数据。
    /// 会话标题默认为"新对话"，在用户发送第一条消息时自动更新。
    /// </summary>
    [DataContract]
    public class ChatSession
    {
        [DataMember]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [DataMember]
        public string Title { get; set; } = "新对话";

        [DataMember]
        public List<ChatMessage> Messages { get; set; } = new();

        [DataMember]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [DataMember]
        public DateTime LastActiveAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 持久化用的会话容器，存储一个解决方案下的所有会话。
    /// </summary>
    [DataContract]
    internal class SessionsContainer
    {
        [DataMember]
        public string SolutionPath { get; set; } = string.Empty;

        [DataMember]
        public DateTime LastSaved { get; set; }

        [DataMember]
        public List<ChatSession> Sessions { get; set; } = new();

        [DataMember]
        public string? ActiveSessionId { get; set; }
    }

    // ======== 搜索查询优化模型 ========

    /// <summary>
    /// AI 返回的搜索查询优化结果。由 AI 分析用户问题和上下文后生成。
    /// 必须严格校验 JSON 格式。
    /// </summary>
    public class SearchQueryOptimization
    {
        /// <summary>优化后的搜索关键词（必填，不超过72字符）</summary>
        [JsonPropertyName("search_query")]
        public string SearchQuery { get; set; } = string.Empty;

        /// <summary>搜索时效过滤（可选）: week/month/semiyear/year</summary>
        [JsonPropertyName("search_recency")]
        public string? SearchRecency { get; set; }

        /// <summary>是否需要联网搜索</summary>
        [JsonPropertyName("need_search")]
        public bool NeedSearch { get; set; } = true;
    }
}