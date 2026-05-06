using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// DeepSeek Chat 主控件，对标共享项目 ucChat。
    /// 宿主 WebView2（Chromium），采用增量渲染模式：
    /// - 首次加载使用 NavigateToString 构建完整页面
    /// - 后续消息通过 ExecuteScriptAsync 调用 JS 增量追加
    /// - 流式输出时通过 BuildStreamingUpdateJs 实时更新 DOM，消除全页刷新闪烁
    /// </summary>
    public partial class DeepSeekChatControl : System.Windows.Controls.UserControl, IDisposable
    {
        #region Constants

        private const string WelcomeMessage =
            "你好！我是 DeepSeek Chat，你的 AI 编程助手。\n\n" +
            "我可以帮你：\n- 解释代码\n- 修复 Bug\n- 重构代码\n- 生成测试\n- 回答技术问题\n\n开始提问吧！";

        private const string ApiKeyMissingMessage =
            "⚠️ **未配置 API 密钥**\n\n" +
            "请通过菜单 **工具 → 选项 → DeepSeek Chat** 配置你的 DeepSeek API 密钥。\n\n" +
            "获取密钥：https://platform.deepseek.com/api_keys";

        /// <summary>
        /// 流式更新间隔（字符数），每累积这么多字符触发一次 DOM 更新。
        /// </summary>
        private const int StreamRenderInterval = 15;

        #endregion

        #region Properties

        private DeepSeek_v4_for_VisualStudioPackage? _package;
        private DeepSeekOptionsPage? _options;
        private DeepSeekApiService? _apiService;
        private WebSearchService? _webSearchService;
        private CancellationTokenSource? _currentStreamingCts;
        private string? _solutionPath;

        private readonly List<ChatMessage> _messages = new();
        private readonly List<ChatApiMessage> _conversationHistory = new();
        private bool _isGenerating;
        private string _webSearchEngine = "Off"; // "Off" | "Baidu" | "DuckDuckGo"
        private readonly List<string> _pendingWarnings = new(); // 待注入的警告消息

        // ── 多会话支持 ──
        private SessionsContainer? _sessionsContainer;
        private ChatSession? _activeSession;

        // ── 增量渲染状态（对标 Turbo ucChat） ──
        private bool _browserInitialized;
        private int _lastRenderedMessagesLength;
        private readonly StringBuilder _messagesHtml = new();

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化控件。
        /// </summary>
        public DeepSeekChatControl()
        {
            InitializeComponent();

            // 初始化模型和推理强度下拉框
            ModelComboBox.ItemsSource = new[] { "deepseek-v4-pro", "deepseek-v4-flash" };
            ModelComboBox.SelectedIndex = 0;

            EffortComboBox.ItemsSource = new[] { "high", "max" };
            EffortComboBox.SelectedIndex = 0;

            ThinkingCheckBox.IsChecked = true;

            // 联网搜索引擎选择: 默认百度
            WebSearchEngineComboBox.ItemsSource = new[] { "关闭", "🔍 百度搜索", "🦆 DuckDuckGo" };
            WebSearchEngineComboBox.SelectedIndex = 1; // 默认百度搜索
            _webSearchEngine = "Baidu";

            // 注册 WebView2 事件
            ChatWebView.CoreWebView2InitializationCompleted += ChatWebView_CoreWebView2InitializationCompleted;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 工具窗口创建完成后调用，传入 Package 引用。
        /// 对标 TerminalWindowTurbo.OnCreate() → StartControl()。
        /// </summary>
        public void StartControl(DeepSeek_v4_for_VisualStudioPackage package)
        {
            _package = package;
            _options = package.Options;

            InitializeApiService();
            InitializeWebSearchService();
            _ = ResolveSolutionPathAsync();
            _ = LoadAndShowAsync();

            // ── 后台异步校验 API Key 有效性 ──
            _ = ValidateAllApiKeysAsync();
        }

        #endregion

        #region Private Methods - Initialization

        private void InitializeApiService()
        {
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
                return;

            _apiService?.Dispose();
            _apiService = new DeepSeekApiService(_options.ApiKey, _options.SelectedModel);
            _apiService.ConfigureThinking(_options.IsThinkingEnabled, _options.ReasoningEffort);
            Logger.Info("API 服务初始化成功");
        }

        /// <summary>
        /// 初始化联网搜索服务。默认使用百度搜索，若未配置 API Key 则使用 DuckDuckGo。
        /// </summary>
        private void InitializeWebSearchService()
        {
            _webSearchService?.Dispose();
            _webSearchService = new WebSearchService();
            ApplyWebSearchConfig();
            Logger.Info("联网搜索服务初始化成功");
        }

        /// <summary>
        /// 热重载搜索配置 — 从 Options 重新读取 API Key 并应用到 WebSearchService。
        /// 同时遵循用户在 ComboBox 中选择的搜索引擎偏好。
        /// 用于支持用户在 工具→选项 中修改 API Key 后无需重启即可生效。
        /// </summary>
        private void ApplyWebSearchConfig()
        {
            if (_webSearchService == null) return;

            // 根据用户选择的引擎决定使用哪个搜索提供商
            if (_webSearchEngine == "Baidu" && _options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
            {
                _webSearchService.ConfigureBaiduSearch(_options.BaiduApiKey);
                Logger.Info("联网搜索热重载: 百度千帆 (用户选择百度，API Key 已配置)");
            }
            else if (_webSearchEngine == "Baidu")
            {
                // 用户选择百度但未配置 Key，仍然尝试配置（会在搜索时提示用户）
                _webSearchService.ConfigureBaiduSearch(null!);
                Logger.Info("联网搜索热重载: DuckDuckGo (用户选择百度但 API Key 未配置)");
            }
            else
            {
                // DuckDuckGo 或 Off，统一使用 DuckDuckGo
                _webSearchService.ConfigureBaiduSearch(null!);
                Logger.Info($"联网搜索热重载: DuckDuckGo (用户选择 {_webSearchEngine})");
            }
        }

        /// <summary>
        /// 后台异步校验所有已配置的 API Key 是否有效。
        /// 启动时调用，校验结果通过 StatusLabel 提示用户。
        /// </summary>
        private async Task ValidateAllApiKeysAsync()
        {
            // ── 校验 DeepSeek API Key ──
            if (_apiService != null)
            {
                string? deepSeekError = await _apiService.ValidateApiKeyAsync();
                if (deepSeekError != null)
                {
                    Logger.Error($"DeepSeek API Key 校验失败: {deepSeekError}");
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = "⚠️ DeepSeek API Key 无效，请检查配置";
                }
                else
                {
                    Logger.Info("DeepSeek API Key 校验通过");
                }
            }

            // ── 校验百度 API Key ──
            if (_webSearchService != null && _webSearchEngine == "Baidu" &&
                _options != null && !string.IsNullOrWhiteSpace(_options.BaiduApiKey))
            {
                string? baiduError = await _webSearchService.ValidateBaiduApiKeyAsync();
                if (baiduError != null)
                {
                    Logger.Error($"百度 API Key 校验失败: {baiduError}");
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = "⚠️ 百度 API Key 无效，请检查配置";
                }
                else
                {
                    Logger.Info("百度 API Key 校验通过");
                }
            }
        }

        private async Task ResolveSolutionPathAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                _solutionPath = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(_solutionPath))
                    Logger.Info($"检测到解决方案: {_solutionPath}");
                else
                    Logger.Info("未检测到已打开的解决方案，使用默认存储");
            }
            catch (Exception ex)
            {
                Logger.Error("解析解决方案路径失败", ex);
                _solutionPath = null;
            }
        }

        private async Task LoadAndShowAsync()
        {
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            // 加载所有会话
            _sessionsContainer = ChatPersistenceService.LoadSessions(_solutionPath);

            // 确定活跃会话
            if (!string.IsNullOrEmpty(_sessionsContainer.ActiveSessionId))
            {
                _activeSession = _sessionsContainer.Sessions
                    .FirstOrDefault(s => s.Id == _sessionsContainer.ActiveSessionId);
            }
            _activeSession ??= _sessionsContainer.Sessions.FirstOrDefault();

            // 如果没有会话，创建默认会话
            if (_activeSession == null)
            {
                _activeSession = CreateNewSessionInternal();
                _sessionsContainer.Sessions.Add(_activeSession);
                _sessionsContainer.ActiveSessionId = _activeSession.Id;
            }

            // 加载活跃会话的消息
            _messages.Clear();
            _conversationHistory.Clear();

            if (_activeSession.Messages.Count > 0)
            {
                Logger.Info($"[Render] LoadConversation: 从会话 '{_activeSession.Title}' 加载了 {_activeSession.Messages.Count} 条消息");
                foreach (var msg in _activeSession.Messages)
                {
                    msg.IsStreaming = false;
                    _messages.Add(msg);
                    if (msg.Role is "user" or "assistant")
                    {
                        _conversationHistory.Add(new ChatApiMessage
                        {
                            Role = msg.Role,
                            Content = msg.Content ?? string.Empty,
                        });
                    }
                }
            }

            // 没有消息则显示欢迎语
            if (_messages.Count == 0)
            {
                bool hasApiKey = _options != null && !string.IsNullOrEmpty(_options.ApiKey);
                string welcomeContent = hasApiKey ? WelcomeMessage : ApiKeyMissingMessage;

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = welcomeContent,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(welcomeMsg);
                _activeSession.Messages.Add(welcomeMsg);
                Logger.Info(hasApiKey ? "[Render] 添加欢迎语" : "[Render] 添加欢迎语 + API密钥缺失警告");
            }

            // 填充会话下拉框
            PopulateSessionComboBox();

            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                Logger.Info("[Render] 开始初始化 WebView2 CoreWebView2 环境");
                var env = await CoreWebView2Environment.CreateAsync(
                    null,
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeepSeekVS", "WebView2"));
                await ChatWebView.EnsureCoreWebView2Async(env);
                Logger.Info("[Render] CoreWebView2 环境初始化成功");
            }
            catch (Exception ex)
            {
                Logger.Error("[Render] WebView2 初始化失败", ex);
                StatusLabel.Text = $"WebView2 初始化失败: {ex.Message}";
            }
        }

        #endregion

        #region Private Methods - Rendering

        /// <summary>
        /// 增量更新浏览器内容。
        /// 对标 ucChat.UpdateBrowser()：首次使用 NavigateToString，
        /// 后续通过 ExecuteScriptAsync 调用 window.__appendMessageHtml 增量追加。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 模式用于浏览器更新（fire-and-forget），异常已在方法内处理
        private async void UpdateBrowser()
        #pragma warning restore VSTHRD100
        {
            if (ChatWebView.CoreWebView2 == null)
                return;

            try
            {
                string allMessages = _messagesHtml.ToString();

                // ── 增量更新路径 ──
                if (_browserInitialized && allMessages.Length > _lastRenderedMessagesLength)
                {
                    string delta = allMessages.Substring(_lastRenderedMessagesLength);
                    string jsFragment = System.Text.Json.JsonSerializer.Serialize(delta);

                    try
                    {
                        string script = $"window.__appendMessageHtml({jsFragment});";
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
                        _lastRenderedMessagesLength = allMessages.Length;
                        return;
                    }
                    catch
                    {
                        // 增量更新失败时回退到全量刷新
                    }
                }

                // ── 全量刷新路径 ──
                string html = ChatHtmlService.BuildInitialPage(_messages);
                ChatWebView.CoreWebView2.NavigateToString(html);
                _browserInitialized = true;
                _lastRenderedMessagesLength = allMessages.Length;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateBrowser 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 构建消息 HTML 片段并追加到 _messagesHtml，然后更新浏览器。
        /// </summary>
        private void AddMessagesHtml(string role, string content, string? reasoningContent = null)
        {
            if (role == "user")
            {
                _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(content));
            }
            else
            {
                var tempMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = content,
                    ReasoningContent = reasoningContent ?? string.Empty,
                    IsStreaming = false,
                };
                _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(tempMsg, _messages.Count - 1));
            }
        }

        /// <summary>
        /// CoreWebView2 初始化完成回调。
        /// </summary>
        private void ChatWebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Logger.Info("[Render] CoreWebView2InitializationCompleted: 成功");
                ChatWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                ChatWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

                // 构建初始 HTML 内容
                RebuildMessagesHtml();
                _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    UpdateBrowser();
                });
            }
            else
            {
                Logger.Error($"[Render] CoreWebView2 初始化失败: {e.InitializationException?.Message}", e.InitializationException);
                _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"WebView2 初始化失败: {e.InitializationException?.Message}";
                });
            }
        }

        /// <summary>
        /// 根据 _messages 列表重建 _messagesHtml。
        /// </summary>
        private void RebuildMessagesHtml()
        {
            _messagesHtml.Clear();
            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (msg.Role == "user")
                {
                    _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(msg.Content ?? string.Empty));
                }
                else
                {
                    _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(msg, i));
                }
            }
            _lastRenderedMessagesLength = 0;
        }

        #endregion

        #region Private Methods - Session Management

        /// <summary>
        /// 创建新会话的内部方法（不保存）。
        /// </summary>
        private ChatSession CreateNewSessionInternal()
        {
            return new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "新对话",
                Messages = new List<ChatMessage>(),
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
            };
        }

        /// <summary>
        /// 保存当前活跃会话到容器并持久化。
        /// </summary>
        private void SaveCurrentSession()
        {
            if (_activeSession == null || _sessionsContainer == null) return;

            _activeSession.Messages = _messages.ToList();
            _activeSession.LastActiveAt = DateTime.Now;
            _sessionsContainer.ActiveSessionId = _activeSession.Id;
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);
        }

        /// <summary>
        /// 根据第一条用户消息自动设置会话标题。
        /// 截取前30个字符，去掉换行。
        /// </summary>
        private void AutoTitleSession()
        {
            if (_activeSession == null) return;
            if (_activeSession.Title != "新对话") return;

            var firstUserMsg = _messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg == null || string.IsNullOrWhiteSpace(firstUserMsg.Content))
                return;

            string title = firstUserMsg.Content.Trim();
            // 取第一行或前30个字符
            int newlineIdx = title.IndexOf('\n');
            if (newlineIdx > 0)
                title = title.Substring(0, newlineIdx).Trim();
            if (title.Length > 30)
                title = title.Substring(0, 30) + "…";

            _activeSession.Title = title;
            PopulateSessionComboBox();
            SaveCurrentSession();
            Logger.Info($"会话标题自动更新为: {title}");
        }

        /// <summary>
        /// 切换到指定会话。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于会话切换（从事件处理程序调用），异常已在方法内处理
        private async void SwitchToSession(ChatSession session)
        #pragma warning restore VSTHRD100
        {
            if (session == null || session == _activeSession) return;

            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            // 保存当前会话
            SaveCurrentSession();

            // 切换到新会话
            _activeSession = session;
            _activeSession.LastActiveAt = DateTime.Now;

            // 清空并加载消息
            _messages.Clear();
            _conversationHistory.Clear();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            foreach (var msg in _activeSession.Messages)
            {
                msg.IsStreaming = false;
                _messages.Add(msg);
                if (msg.Role is "user" or "assistant")
                {
                    _conversationHistory.Add(new ChatApiMessage
                    {
                        Role = msg.Role,
                        Content = msg.Content ?? string.Empty,
                    });
                }
            }

            // 更新下拉框选中项
            PopulateSessionComboBox();

            // 完整刷新浏览器
            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            Logger.Info($"切换到会话: {_activeSession.Title}");
        }

        /// <summary>
        /// 填充会话下拉框，保持选中当前活跃会话。
        /// </summary>
        private void PopulateSessionComboBox()
        {
            if (_sessionsContainer == null) return;

            // 按最后活跃时间倒序排列
            var sortedSessions = _sessionsContainer.Sessions
                .OrderByDescending(s => s.LastActiveAt)
                .ToList();

            SessionComboBox.ItemsSource = null;
            SessionComboBox.ItemsSource = sortedSessions;

            if (_activeSession != null)
            {
                SessionComboBox.SelectedItem = sortedSessions.FirstOrDefault(s => s.Id == _activeSession.Id);
            }
        }

        /// <summary>
        /// 创建新对话（"新对话" 按钮点击）。
        /// </summary>
        private void CreateNewChat()
        {
            // 停止当前生成
            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            // 保存当前会话
            SaveCurrentSession();

            // 创建新会话
            _activeSession = CreateNewSessionInternal();
            if (_sessionsContainer == null)
                _sessionsContainer = new SessionsContainer { SolutionPath = _solutionPath ?? "(unsaved)" };
            _sessionsContainer.Sessions.Add(_activeSession);
            _sessionsContainer.ActiveSessionId = _activeSession.Id;

            // 清空并添加欢迎语
            _messages.Clear();
            _conversationHistory.Clear();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            var welcomeMsg = new ChatMessage
            {
                Role = "assistant",
                Content = WelcomeMessage,
                Timestamp = DateTime.Now,
                IsRendered = true,
            };
            _messages.Add(welcomeMsg);
            _activeSession.Messages.Add(welcomeMsg);

            // 更新下拉框
            PopulateSessionComboBox();

            // 持久化
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

            // 重置搜索额度状态（新会话可能额度已恢复）
            _webSearchService?.ResetQuotaState();

            // 刷新浏览器
            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            InputTextBox.Focus();
            Logger.Info("创建新会话");
        }

        /// <summary>
        /// 删除当前会话。
        /// </summary>
        private void DeleteCurrentSession()
        {
            if (_sessionsContainer == null || _activeSession == null) return;
            if (_sessionsContainer.Sessions.Count <= 1)
            {
                // 最后一个会话不能删除，清空即可
                ClearCurrentSessionMessages();
                return;
            }

            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            string deletedTitle = _activeSession.Title;
            _sessionsContainer.Sessions.Remove(_activeSession);

            // 切换到第一个会话
            _activeSession = _sessionsContainer.Sessions.FirstOrDefault();
            _sessionsContainer.ActiveSessionId = _activeSession?.Id;

            // 加载新会话消息
            _messages.Clear();
            _conversationHistory.Clear();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            if (_activeSession != null)
            {
                foreach (var msg in _activeSession.Messages)
                {
                    msg.IsStreaming = false;
                    _messages.Add(msg);
                    if (msg.Role is "user" or "assistant")
                    {
                        _conversationHistory.Add(new ChatApiMessage
                        {
                            Role = msg.Role,
                            Content = msg.Content ?? string.Empty,
                        });
                    }
                }
            }

            // 更新下拉框并持久化
            PopulateSessionComboBox();
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

            // 刷新浏览器
            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            Logger.Info($"已删除会话: {deletedTitle}");
        }

        /// <summary>
        /// 清空当前会话的消息（保留会话本身）。
        /// </summary>
        private void ClearCurrentSessionMessages()
        {
            _messages.Clear();
            _conversationHistory.Clear();
            _messagesHtml.Clear();
            _lastRenderedMessagesLength = 0;

            if (_activeSession != null)
            {
                _activeSession.Messages.Clear();
                _activeSession.Title = "新对话";
            }

            var welcomeMsg = new ChatMessage
            {
                Role = "assistant",
                Content = WelcomeMessage,
                Timestamp = DateTime.Now,
                IsRendered = true,
            };
            _messages.Add(welcomeMsg);
            _activeSession?.Messages.Add(welcomeMsg);

            PopulateSessionComboBox();
            SaveCurrentSession();

            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();
            Logger.Info("已清空当前会话消息");
        }

        #endregion

        #region Private Methods - API Interaction

        #pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件处理，符合 WPF 模式
        private async void SendMessage()
        #pragma warning restore VSTHRD100
        {
            if (_isGenerating) return;

            var userText = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(userText))
                return;

            // 校验 API 密钥
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
                var warningMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = ApiKeyMissingMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                _messages.Add(warningMsg);
                AddMessagesHtml("assistant", ApiKeyMissingMessage);
                UpdateBrowser();
                StatusLabel.Text = "⚠️ 请先配置 API 密钥 (工具 → 选项 → DeepSeek Chat)";
                return;
            }

            // 热重载 API 服务
            InitializeApiService();
            if (_apiService == null) return;

            InputTextBox.Text = string.Empty;

            // ── 添加用户消息 ──
            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = userText!,
                Timestamp = DateTime.Now,
            };
            _messages.Add(userMsg);
            _conversationHistory.Add(new ChatApiMessage { Role = "user", Content = userText! });

            // 自动设置会话标题（使用第一条用户消息）
            AutoTitleSession();

            // ── 创建助手消息占位 ──
            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ReasoningContent = string.Empty,
                Timestamp = DateTime.Now,
                IsStreaming = true,
                IsRendered = false,
            };
            _messages.Add(assistantMsg);
            int assistantMsgIndex = _messages.Count - 1;

            // ── 批量构建 HTML（用户消息 + 助手占位），仅调用一次 UpdateBrowser 避免竞态重复渲染 ──
            AddMessagesHtml("user", userText!);
            AddMessagesHtml("assistant", string.Empty);
            UpdateBrowser();

            _isGenerating = true;
            UpdateButtonsState();

            bool isWebSearchEnabled = _webSearchEngine != "Off";
            StatusLabel.Text = isWebSearchEnabled ? "正在联网搜索…" : "DeepSeek 思考中…";

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

            // ── 联网搜索（在 API 调用之前执行） ──
            string searchContext = string.Empty;
            List<WebSearchResult> capturedSearchResults = new List<WebSearchResult>();
            string? engineSwitchNote = null; // 引擎切换原因提示
            if (isWebSearchEnabled && _webSearchService != null)
            {
                // ── 热重载 API Key（支持不重启生效） ──
                ApplyWebSearchConfig();
                // ── 检查百度 API Key ──
                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = "⚠️ 请先配置百度 API Key (工具→选项→DeepSeek Chat→Web Search)";
                    assistantMsg.Content = "⚠️ **百度搜索未配置**\n\n请通过菜单 **工具 → 选项 → DeepSeek Chat → Web Search** 配置百度千帆 API Key。\n\n获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n\n也可以切换到 DuckDuckGo 搜索（免费，无需 Key）。\n\n";
                    assistantMsg.IsStreaming = false;
                    _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: true);
                    _isGenerating = false;
                    UpdateButtonsState();
                    StatusLabel.Text = "⚠️ 百度 API Key 未配置";
                    return;
                }

                // ── 时间词语替换（如"今天"→具体日期） ──
                string timeAwareQuery = ResolveTimeExpressions(userText!);

                // ── AI 优化搜索查询 ──
                string optimizedQuery = timeAwareQuery;
                string? searchRecency = null;

                try
                {

                    if (_apiService != null)
                    {
                        try
                        {
                            StatusLabel.Text = "AI 正在优化搜索词…";
                            bool isBaidu = _webSearchEngine == "Baidu";
                            var optimization = await OptimizeSearchQueryAsync(timeAwareQuery, _currentStreamingCts.Token, isBaidu);
                            if (optimization != null && !string.IsNullOrWhiteSpace(optimization.SearchQuery) && optimization.NeedSearch)
                            {
                                optimizedQuery = optimization.SearchQuery;
                                searchRecency = optimization.SearchRecency;
                                Logger.Info($"AI 优化搜索词: \"{userText}\" → \"{optimizedQuery}\", recency={searchRecency}");
                                StatusLabel.Text = $"搜索词已优化: \"{optimizedQuery}\"";
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Info($"搜索词优化失败，使用原始查询: {ex.Message}");
                            // 优化失败不影响流程，使用原始查询
                        }
                    }

                    var searchResults = await _webSearchService.SearchAsync(optimizedQuery, _currentStreamingCts.Token, searchRecency);
                    capturedSearchResults = searchResults;
                    if (searchResults.Count > 0)
                    {
                        string providerLabel = _webSearchService.ActiveProvider == SearchProvider.Baidu
                            ? "百度搜索" : "DuckDuckGo";
                        StatusLabel.Text = $"已通过 {providerLabel} 获取 {searchResults.Count} 条搜索结果，正在抓取网页内容…";

                        // 在助手消息中显示搜索状态
                        assistantMsg.Content = $"🔍 已联网搜索到 {searchResults.Count} 条结果（{providerLabel}），正在抓取网页内容…\n\n";
                        _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                        // ── 抓取网页内容增强上下文（await 确保完成后才构建 AI 上下文） ──
                        await EnrichSearchContextAsync(searchResults, _currentStreamingCts.Token);
                        searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);

                        Logger.Info($"联网搜索完成，通过 {providerLabel} 获取 {searchResults.Count} 条结果");
                    }
                    else
                    {
                        // 检查是否是百度额度耗尽
                        if (_webSearchService.IsBaiduQuotaExhausted)
                        {
                            engineSwitchNote = "⚠️ 百度搜索免费额度已用尽，本次已自动切换至 DuckDuckGo。请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待次日重置。";
                            StatusLabel.Text = "⚠️ 百度搜索额度已耗尽，已自动切换至 DuckDuckGo";
                            assistantMsg.Content = "⚠️ 百度搜索免费额度已用尽，已自动切换至 DuckDuckGo 搜索…\n\n";
                            _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                            // 立即用 DuckDuckGo 重试（使用优化后的搜索词）
                            searchResults = await _webSearchService.SearchAsync(optimizedQuery, _currentStreamingCts.Token);
                            capturedSearchResults = searchResults;
                            if (searchResults.Count > 0)
                            {
                                StatusLabel.Text = $"已通过 DuckDuckGo 获取 {searchResults.Count} 条结果，正在抓取网页内容…";
                                assistantMsg.Content = $"🔍 已通过 DuckDuckGo 搜索到 {searchResults.Count} 条结果，正在抓取网页内容…\n\n";
                                _ = UpdateStreamingMessageAsync(assistantMsgIndex, assistantMsg.Content, string.Empty, isComplete: false);

                                await EnrichSearchContextAsync(searchResults, _currentStreamingCts.Token);
                                searchContext = WebSearchService.FormatSearchResultsForContext(searchResults);
                            }
                        }
                        else
                        {
                            StatusLabel.Text = "未找到搜索结果，使用内置知识回复…";
                        }
                        Logger.Info("联网搜索未找到结果");
                    }
                }
                catch (ApiKeyInvalidException ex)
                {
                    // 百度 Key 无效 → 与 DeepSeek API Key 无效相同逻辑：直接报错并停止，不静默回退
                    Logger.Error($"[Render] 百度 API Key 无效", ex);
                    assistantMsg.Content = "⚠️ 百度搜索 API Key 无效，请检查配置：工具 → 选项 → DeepSeek Chat → Web Search。\n\n获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n\n也可以切换到 DuckDuckGo 搜索（免费，无需 Key）。";
                    assistantMsg.IsStreaming = false;
                    await UpdateStreamingMessageAsync(assistantMsgIndex,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                    _messages.Remove(assistantMsg); // 不保存到对话记录
                    _isGenerating = false;
                    UpdateButtonsState();
                    StatusLabel.Text = "⚠️ 百度 API Key 无效";
                    _currentStreamingCts?.Cancel();
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error($"联网搜索异常: {ex.Message}", ex);
                    StatusLabel.Text = "搜索失败，使用内置知识回复…";
                }
            }

            // ── 引擎切换提示：若用户选择百度但实际使用了 DuckDuckGo，记录原因 ──
            if (string.IsNullOrEmpty(engineSwitchNote) &&
                _webSearchEngine == "Baidu" &&
                _webSearchService != null &&
                _webSearchService.ActiveProvider == SearchProvider.DuckDuckGo)
            {
                engineSwitchNote = "⚠️ 百度搜索未返回结果，本次已自动切换至 DuckDuckGo。";
            }
            if (!string.IsNullOrEmpty(engineSwitchNote))
            {
                _pendingWarnings.Add(engineSwitchNote!);
            }

            try
            {
                var requestMessages = BuildRequestMessages(searchContext);
                var reasoningBuffer = new StringBuilder();
                var contentBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                var apiService = _apiService!;
                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = "DeepSeek 深度思考中…";

                        // 定期更新思考面板
                        if (reasoningBuffer.Length - lastReasoningLength >= 80)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            await UpdateStreamingMessageAsync(assistantMsgIndex,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                    else
                    {
                        if (reasoningBuffer.Length > 0 && lastReasoningLength < reasoningBuffer.Length)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                        }

                        contentBuffer.Append(chunk);
                        streamRenderTick += chunk.Length;
                        StatusLabel.Text = "DeepSeek 回复中...";

                        if (streamRenderTick >= StreamRenderInterval)
                        {
                            streamRenderTick = 0;
                            assistantMsg.Content = contentBuffer.ToString();
                            await UpdateStreamingMessageAsync(assistantMsgIndex,
                                contentBuffer.ToString(),
                                reasoningBuffer.ToString(),
                                isComplete: false);
                        }
                    }
                }

                // ── 流式完成：渲染最终 Markdown ──
                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Render] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    assistantMsgIndex,
                    contentBuffer.ToString(),
                    reasoningBuffer.ToString());

                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

                // ── 注入搜索结果链接卡片到 AI 消息上方 ──
                if (capturedSearchResults.Count > 0)
                {
                    string providerLabel = _webSearchService?.ActiveProvider == SearchProvider.Baidu
                        ? "百度搜索" : "DuckDuckGo";
                    string searchCardJs = ChatHtmlService.BuildSearchResultsInjectionJs(
                        assistantMsgIndex, capturedSearchResults, providerLabel);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(searchCardJs);
                }

                _conversationHistory.Add(new ChatApiMessage { Role = "assistant", Content = contentBuffer.ToString() });

                // 后台持久化
                var capturedMsg = assistantMsg;
                _ = Task.Run(() =>
                {
                    capturedMsg.HtmlContent = "rendered";
                    capturedMsg.IsRendered = true;
                    SaveCurrentSession();
                });
            }
            catch (ApiKeyInvalidException ex)
            {
                Logger.Error($"[Render] API Key 无效", ex);
                assistantMsg.Content = $"⚠️ {ex.Message}";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                _messages.Remove(assistantMsg); // 不保存到对话记录
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[Render] 用户停止生成");
                assistantMsg.Content += "\n\n*[已停止]*";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
            {
                Logger.Error($"[Render] API 认证失败", ex);
                assistantMsg.Content = "⚠️ DeepSeek API Key 无效或已过期，请通过 工具 → 选项 → DeepSeek Chat 重新配置。\n获取密钥：https://platform.deepseek.com/api_keys";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                _messages.Remove(assistantMsg); // 不保存到对话记录
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] API 出错", ex);
                assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                assistantMsg.IsStreaming = false;
                await UpdateStreamingMessageAsync(assistantMsgIndex,
                    assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
            }
            finally
            {
                assistantMsg.IsStreaming = false;
                _isGenerating = false;
                StatusLabel.Text = string.Empty;
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
            }
        }

        /// <summary>
        /// 通过 JS 增量更新流式消息的 DOM 内容。
        /// </summary>
        private async Task UpdateStreamingMessageAsync(int messageIndex, string content, string reasoningContent, bool isComplete)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string js = ChatHtmlService.BuildStreamingUpdateJs(messageIndex, content, reasoningContent, isComplete);
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateStreamingMessage 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// <summary>
        /// 调用 AI 分析用户问题和上下文，生成优化的搜索关键词。
        /// 百度引擎：返回严格 JSON（含 search_recency 时效过滤）。
        /// DuckDuckGo：仅返回优化后的纯文本关键词。
        /// </summary>
        /// <param name="userQuery">用户原始问题</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="isBaiduSearch">是否使用百度搜索（true=JSON格式，false=纯文本关键词）</param>
        /// <returns>优化后的搜索查询对象，失败返回 null</returns>
        private async Task<SearchQueryOptimization?> OptimizeSearchQueryAsync(string userQuery, CancellationToken ct, bool isBaiduSearch = true)
        {
            if (_apiService == null)
                return null;

            // ── 构建优化提示词 ──
            string contextSummary = string.Empty;
            if (_conversationHistory.Count > 1)
            {
                // 取最近几条用户消息作为上下文
                var recent = _conversationHistory
                    .Where(m => m.Role == "user")
                    .Reverse()
                    .Take(3)
                    .Reverse()
                    .Select(m => m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content);
                contextSummary = string.Join(" | ", recent);
            }

            string contextLine = string.IsNullOrWhiteSpace(contextSummary)
                ? $"用户问题：{userQuery}"
                : $"对话上下文：{contextSummary}\n用户问题：{userQuery}";

            string optimizationPrompt;
            string systemPrompt;

            if (isBaiduSearch)
            {
                // ── 百度引擎：JSON 格式（含 search_recency 时效过滤） ──
                optimizationPrompt =
                    "你是一个搜索查询优化助手。根据用户的问题和对话上下文，生成优化的联网搜索关键词。\n\n" +
                    "规则：\n" +
                    "1. 提取核心搜索意图，去除无关词汇\n" +
                    "2. 关键词应简洁精准，不超过72个字符（一个汉字=2字符）\n" +
                    "3. 如需时效性信息，设置 search_recency 为 week/month/semiyear/year\n" +
                    "4. 如果用户只是聊天/问候/代码问题（不需要联网），设置 need_search 为 false\n" +
                    "5. 如果内容携带时间信息，请不要移除\n" +
                    "6. 严格返回 JSON 格式，不要包含任何其他文本\n\n" +
                    "JSON 格式：\n" +
                    "{\"search_query\":\"优化后的关键词\",\"search_recency\":null,\"need_search\":true}\n\n" +
                    contextLine +
                    "\n\n请返回优化后的搜索 JSON：";
                systemPrompt = "你只返回 JSON，不返回任何其他内容。";
            }
            else
            {
                // ── DuckDuckGo 引擎：纯文本关键词（无需 JSON，无需 recency） ──
                optimizationPrompt =
                    "你是一个搜索查询优化助手。根据用户的问题，生成优化的联网搜索关键词。\n\n" +
                    "规则：\n" +
                    "1. 提取核心搜索意图，去除无关词汇\n" +
                    "2. 关键词应简洁精准，不超过72个字符（一个汉字=2字符）\n" +
                    "3. 如果用户不需要联网搜索，回复 NO_SEARCH\n" +
                    "4. 只返回优化后的关键词本身，不要任何解释、标点或格式\n\n" +
                    "5. 如果内容携带时间信息，请不要移除\n" +
                    contextLine +
                    "\n\n优化后的关键词：";
                systemPrompt = "你只返回优化后的搜索关键词，不返回任何其他内容。";
            }

            try
            {
                var optimizationMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = systemPrompt },
                    new ChatApiMessage { Role = "user", Content = optimizationPrompt },
                };

                Logger.Info($"开始 AI 搜索词优化 ({(isBaiduSearch ? "百度" : "DuckDuckGo")})，原始查询: \"{userQuery}\"");
                var rawResponse = await _apiService.CompleteAsync(optimizationMessages, ct);
                Logger.Info($"AI 搜索词优化原始响应: {rawResponse}");

                if (isBaiduSearch)
                {
                    // ── 百度：校验 JSON ──
                    return ParseAndValidateSearchOptimization(rawResponse, userQuery);
                }
                else
                {
                    // ── DuckDuckGo：纯文本关键词 ──
                    string keyword = rawResponse?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(keyword) ||
                        keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SearchQueryOptimization
                        {
                            SearchQuery = userQuery,
                            NeedSearch = keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase) ? false : true,
                        };
                    }
                    // 清理可能的多余内容（AI 偶尔会返回带引号或前缀的文字）
                    keyword = keyword.Trim('"', '\'', '`');
                    if (keyword.Length > 72)
                        keyword = keyword.Substring(0, 72);
                    return new SearchQueryOptimization
                    {
                        SearchQuery = keyword,
                        NeedSearch = true,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("搜索词优化已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"搜索词优化异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 解析并校验 AI 返回的搜索优化 JSON。
        /// 若 JSON 不合法或关键字段缺失，回退到原始查询。
        /// </summary>
        private static SearchQueryOptimization ParseAndValidateSearchOptimization(string rawResponse, string fallbackQuery)
        {
            try
            {
                // 尝试提取 JSON 部分（AI 可能在 JSON 前后附加了文字）
                string jsonStr = rawResponse.Trim();

                // 去掉可能的 markdown 代码块标记
                if (jsonStr.StartsWith("```"))
                {
                    int endOfFirstLine = jsonStr.IndexOf('\n');
                    if (endOfFirstLine > 0)
                        jsonStr = jsonStr.Substring(endOfFirstLine + 1);
                    if (jsonStr.EndsWith("```"))
                        jsonStr = jsonStr.Substring(0, jsonStr.Length - 3);
                    jsonStr = jsonStr.Trim();
                }

                var result = System.Text.Json.JsonSerializer.Deserialize<SearchQueryOptimization>(jsonStr,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });

                // ── 校验 ──
                if (result == null)
                    throw new InvalidOperationException("JSON 解析结果为 null");

                if (string.IsNullOrWhiteSpace(result.SearchQuery))
                {
                    Logger.Info("AI 优化搜索词为空，使用原始查询");
                    return new SearchQueryOptimization
                    {
                        SearchQuery = fallbackQuery,
                        NeedSearch = result.NeedSearch,
                    };
                }

                // 校验 recency 值
                var validRecencies = new HashSet<string> { "week", "month", "semiyear", "year" };
                if (result.SearchRecency != null && !validRecencies.Contains(result.SearchRecency))
                {
                    Logger.Info($"无效的 search_recency 值: {result.SearchRecency}，已忽略");
                    result.SearchRecency = null;
                }

                Logger.Info($"搜索词优化成功: \"{fallbackQuery}\" → \"{result.SearchQuery}\"");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Info($"搜索优化 JSON 解析失败: {ex.Message}，使用原始查询 \"{fallbackQuery}\"");
                return new SearchQueryOptimization
                {
                    SearchQuery = fallbackQuery,
                    NeedSearch = true,
                };
            }
        }

        /// <summary>
        /// 将用户输入中的时间词语替换为具体日期。
        /// 例如："今天" → "2026-05-06"，"本周" → "2026-05-04 至 2026-05-10"。
        /// </summary>
        private static string ResolveTimeExpressions(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return query;

            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");
            string yesterday = now.AddDays(-1).ToString("yyyy-MM-dd");
            string tomorrow = now.AddDays(1).ToString("yyyy-MM-dd");
            string thisWeekStart = now.AddDays(-(int)now.DayOfWeek + 1).ToString("yyyy-MM-dd");
            string thisWeekEnd = now.AddDays(7 - (int)now.DayOfWeek).ToString("yyyy-MM-dd");
            string thisMonth = now.ToString("yyyy年M月");
            string lastMonth = now.AddMonths(-1).ToString("yyyy年M月");
            string thisYear = now.ToString("yyyy年");

            var result = query;

            // 精确匹配（长词优先，避免"今天"匹配到"今天天气"中的一部分）
            var replacements = new Dictionary<string, string>
            {
                ["今天"] = today,
                ["今日"] = today,
                ["昨天"] = yesterday,
                ["昨日"] = yesterday,
                ["明天"] = tomorrow,
                ["明日"] = tomorrow,
                ["本周"] = $"{thisWeekStart} 至 {thisWeekEnd}",
                ["这周"] = $"{thisWeekStart} 至 {thisWeekEnd}",
                ["这个月"] = thisMonth,
                ["本月"] = thisMonth,
                ["上个月"] = lastMonth,
                ["上月"] = lastMonth,
                ["今年"] = thisYear,
                ["当前日期"] = today,
                ["目前"] = $"最新(截至{today})",
                ["最近"] = $"最近(截至{today})",
                ["最新"] = $"最新(截至{today})",
                ["近期"] = $"近期(截至{today})",
                ["最近一周"] = $"最近一周({thisWeekStart} 至 {thisWeekEnd})",
                ["最近一个月"] = $"最近一个月({lastMonth} 至 {thisMonth})",
                ["最近几天"] = $"最近几天({yesterday} 至 {today})",
                ["前几天"] = $"前几天({yesterday} 至 {today})",
            };

            foreach (var kvp in replacements)
            {
                result = result.Replace(kvp.Key, kvp.Value);
            }

            if (result != query)
                Logger.Info($"时间词语解析: \"{query}\" → \"{result}\"");

            return result;
        }

        /// <summary>
        /// 异步抓取搜索结果中前几条 URL 的网页内容，用于增强搜索上下文。
        /// 这是"尽力而为"的后台操作，失败不影响主流程。
        /// </summary>
        private async Task EnrichSearchContextAsync(List<WebSearchResult> results, CancellationToken ct)
        {
            if (_webSearchService == null || results.Count == 0) return;

            try
            {
                // 只抓取前8条结果的网页内容
                int fetchCount = Math.Min(8, results.Count);
                for (int i = 0; i < fetchCount; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        string? pageContent = await _webSearchService.FetchWebPageContentAsync(results[i].Url, ct);
                        if (!string.IsNullOrWhiteSpace(pageContent))
                        {
                            // 将提取的网页内容追加到结果的 Snippet 中
                            string enriched = results[i].Snippet +
                                $"\n[网页内容摘要: {TruncateText(pageContent!, 300)}]";
                            results[i].Snippet = TruncateText(enriched, 800);
                            Logger.Info($"网页内容抓取成功: {results[i].Url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"网页内容抓取跳过 ({results[i].Url}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"网页内容增强失败: {ex.Message}", ex);
            }
        }

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 构建发送给 API 的消息列表。
        /// 当启用联网搜索时，将搜索结果作为系统消息注入到对话历史之前。
        /// </summary>
        /// <param name="searchContext">联网搜索的结果上下文，为空则不注入。</param>
        private List<ChatApiMessage> BuildRequestMessages(string searchContext = "")
        {
            var messages = new List<ChatApiMessage>();

            // ── 系统提示词 ──
            string systemPrompt = _options?.SystemPrompt ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
            }

            // ── 注入联网搜索结果作为系统消息 ──
            if (!string.IsNullOrWhiteSpace(searchContext))
            {
                messages.Add(new ChatApiMessage { Role = "system", Content = searchContext });
            }

            // ── 对话历史 ──
            messages.AddRange(_conversationHistory.Select(m => new ChatApiMessage
            {
                Role = m.Role,
                Content = m.Content,
            }));

            return messages;
        }

        private void StopGeneration()
        {
            _currentStreamingCts?.Cancel();
        }

        #pragma warning disable VSTHRD100 // async void 用于 WPF 按钮事件处理，符合 WPF 模式
        private async void ClearConversation()
        #pragma warning restore VSTHRD100
        {
            if (_isGenerating)
            {
                _currentStreamingCts?.Cancel();
                _isGenerating = false;
                UpdateButtonsState();
            }

            ClearCurrentSessionMessages();
            Logger.Info("清空对话完成");
        }

        #endregion

        #region Private Methods - Helpers

        private void UpdateButtonsState()
        {
            SendButton.IsEnabled = !_isGenerating;
            StopButton.Visibility = _isGenerating ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Visibility = _isGenerating ? Visibility.Collapsed : Visibility.Visible;
            InputTextBox.IsReadOnly = _isGenerating;
        }

        #endregion

        #region Event Handlers - Input

        /// <summary>
        /// 输入框键盘事件：Enter 直接发送消息，Ctrl+Enter 插入换行。
        /// </summary>
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                // 普通 Enter: 发送消息
                e.Handled = true;
                SendMessage();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopGeneration();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearConversation();
        }

        private void SessionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionComboBox.SelectedItem is ChatSession session && session != _activeSession)
            {
                SwitchToSession(session);
            }
        }

        private void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteCurrentSession();
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            CreateNewChat();
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && ModelComboBox.SelectedItem is string model)
            {
                _apiService.UpdateModel(model);
                Logger.Info($"模型切换为: {model}");
            }
        }

        private void ThinkingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_apiService != null)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                string effort = EffortComboBox.SelectedItem as string ?? "high";
                _apiService.ConfigureThinking(enabled, effort);
                Logger.Info($"思考模式: {(enabled ? "启用" : "禁用")}, 强度: {effort}");
            }
        }

        private void EffortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_apiService != null && EffortComboBox.SelectedItem is string effort)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                _apiService.ConfigureThinking(enabled, effort);
                Logger.Info($"推理强度切换为: {effort}");
            }
        }

        /// <summary>
        /// 联网搜索引擎选择变更事件。
        /// </summary>
        private void WebSearchEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WebSearchEngineComboBox.SelectedIndex < 0) return;

            _webSearchEngine = WebSearchEngineComboBox.SelectedIndex switch
            {
                0 => "Off",
                1 => "Baidu",
                2 => "DuckDuckGo",
                _ => "Off",
            };

            Logger.Info($"联网搜索引擎切换为: {_webSearchEngine}");

            // 热重载搜索配置（支持用户修改 API Key 后即时生效）
            if (_webSearchEngine != "Off")
            {
                ApplyWebSearchConfig();
            }

            // 若选择了百度但未配置 API Key，提示用户
            if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
            {
                StatusLabel.Text = "⚠️ 百度搜索需要 API Key，请在 工具→选项→DeepSeek Chat→Web Search 中配置";
            }
        }

        #endregion

        #region Event Handlers - WebView2

        /// <summary>
        /// WebView2 新窗口请求事件：拦截 target='_blank' 链接，在系统默认浏览器中打开。
        /// 使搜索结果卡片中的 URL 可以点击跳转。
        /// </summary>
        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true; // 阻止 WebView2 内部打开
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri,
                    UseShellExecute = true,
                });
                Logger.Info($"在外部浏览器打开: {e.Uri}");
            }
            catch (Exception ex)
            {
                Logger.Error($"打开外部浏览器失败 ({e.Uri}): {ex.Message}", ex);
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(message)) return;

                var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                if (obj.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? string.Empty;
                    if (type == "applyCode")
                    {
                        string code = obj.TryGetProperty("code", out var codeProp)
                            ? codeProp.GetString() ?? string.Empty : string.Empty;
                        ApplyCodeToActiveDocument(code);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebMessage 处理异常: {ex.Message}", ex);
            }
        }

        private void ApplyCodeToActiveDocument(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                    var doc = dte?.ActiveDocument;
                    if (doc != null)
                    {
                        var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                        var editPoint = textDoc.StartPoint.CreateEditPoint();
                        editPoint.ReplaceText(textDoc.EndPoint, code, 0);
                        Logger.Info("代码已应用到活动文档");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"应用代码失败: {ex.Message}", ex);
                }
            });
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源，保存对话。
        /// </summary>
        public void Dispose()
        {
            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _apiService?.Dispose();
            _webSearchService?.Dispose();

            SaveCurrentSession();

            Logger.Info("DeepSeekChatControl 已释放");
        }

        #endregion
    }
}
