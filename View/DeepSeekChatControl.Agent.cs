using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Agent 工作流 + 重试/编辑/版本导航。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Coding Agent Workflow

        /// <summary>
        /// 获取最近几轮对话文本作为 Discover 的附加上下文，帮助 ExploreAgent 生成更精准的关键词。
        /// </summary>
        private string GetConversationContextForDiscovery()
        {
            try
            {
                var history = _contextManager?.GetConversationHistory();
                if (history == null || history.Count == 0)
                    return string.Empty;

                var recentTurns = new List<string>();
                int turnCount = 0;
                for (int i = history.Count - 1; i >= 0 && turnCount < 3; i--)
                {
                    var entry = history[i];
                    if (entry.Role == "user" || entry.Role == "assistant")
                    {
                        string text = entry.Content ?? string.Empty;
                        if (text.Length > 500)
                            text = text.Substring(0, 500);
                        recentTurns.Insert(0, $"[{entry.Role}]: {text}");
                        turnCount++;
                    }
                }
                return recentTurns.Count > 0
                    ? "近期对话:\n" + string.Join("\n", recentTurns)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 构建重试/编辑时的富上下文内容。
        /// </summary>
        private string BuildRetryEnrichedContent(ChatMessage userMsg, string currentContent)
        {
            var sb = new StringBuilder();

            string conversationCtx = GetConversationContextForRetry();
            if (!string.IsNullOrEmpty(conversationCtx))
            {
                sb.AppendLine("【对话历史】");
                sb.AppendLine(conversationCtx);
                sb.AppendLine();
            }

            sb.AppendLine("【当前修改后的问题】");
            sb.AppendLine(currentContent);

            return sb.ToString();
        }

        /// <summary>
        /// 获取用于重试/编辑的对话历史上下文摘要（比 Discovery 版本更详细）。
        /// </summary>
        private string GetConversationContextForRetry()
        {
            try
            {
                var history = _contextManager?.GetConversationHistory();
                if (history == null || history.Count == 0)
                    return string.Empty;

                var recentTurns = new List<string>();
                int turnCount = 0;
                for (int i = history.Count - 1; i >= 0 && turnCount < 5; i--)
                {
                    var entry = history[i];
                    if (entry.Role == "user" || entry.Role == "assistant")
                    {
                        string text = entry.Content ?? string.Empty;
                        string prefix = entry.Role == "user" ? "用户" : "AI";
                        if (text.Length > 800)
                            text = text.Substring(0, 800) + "…";
                        recentTurns.Insert(0, $"[{prefix}]: {text}");
                        turnCount++;
                    }
                }
                return recentTurns.Count > 0
                    ? string.Join("\n\n", recentTurns)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Agent 工作流主入口：分解任务 → 显示步骤计划 → 逐步执行 → 显示变更摘要。
        /// 注意：此方法在后台线程中调用，访问 UI 前必须切换到主线程。
        /// </summary>
        private async Task RunAgentWorkflowAsync(string userText, string fileContext = "",
            AgentRoutingResult? routing = null)
        {
            if (_agentDispatcher == null) return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = "🤖 Agent 正在分析任务...";

                // ── 清理上一轮 Agent 执行的追踪状态 ──
                lock (_lock)
                {
                    _createdPlanIds.Clear();
                    _pendingLogEntries.Clear();
                    _agentThinkingContent.Clear();
                }
                _agentStreamingMsgIndex = -1;
                _lastReportedStepIndex = 0;
                _lastReportedStepStatus = string.Empty;

                var context = new AgentContext
                {
                    SolutionPath = _solutionPath,
                    FileContext = fileContext,
                    ConversationHistory = _contextManager.GetConversationHistory(),
                    IsPlanningMode = routing?.NeedsPlanning == true || routing?.TargetAgent == AgentType.Plan,
                    ReadFileAsync = async (path) =>
                    {
                        if (File.Exists(path))
                            return await Task.Run(() => File.ReadAllText(path));
                        return null;
                    },
                };

                // ── 记录 Token 用量日志 ──
                var stats = _contextManager.GetStats();
                Logger.Info($"[TokenUsage] 当前对话 Token: {stats.EstimatedTokens:N0}/{stats.TokenBudget:N0} ({stats.UsagePercent:F1}%) | 轮次: {stats.TurnCount} | 消息: {stats.MessageCount}");

                await TaskScheduler.Default;

                // ── 创建实时思考气泡（AI 回答流式输出）──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _agentThinkingContent.Clear();
                var thinkingMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = "🤖 Agent 正在分析任务…",
                    ReasoningContent = string.Empty,
                    Timestamp = DateTime.Now,
                    IsStreaming = true,
                    IsRendered = false,
                };
                lock (_lock)
                {
                    _messages.Add(thinkingMsg);
                    _agentStreamingMsgIndex = _messages.Count - 1;
                }
                AddMessagesHtml("assistant", thinkingMsg.Content);
                UpdateBrowser();
                await TaskScheduler.Default;

                var editAgent = _agentDispatcher.EditAgent;
                editAgent.PlanUpdated += OnAgentPlanUpdated;
                _agentDispatcher.PlanUpdated += OnAgentDispatcherPlanUpdated;
                _agentDispatcher.LogEntryAdded += OnAgentLogEntryAdded;
                _agentDispatcher.FileChangeNotified += OnAgentFileChangeNotified;

                AgentResult agentResult;
                try
                {
                    agentResult = await _agentDispatcher.ExecuteAsync(userText, context, routing);
                }
                finally
                {
                    editAgent.PlanUpdated -= OnAgentPlanUpdated;
                    _agentDispatcher.PlanUpdated -= OnAgentDispatcherPlanUpdated;
                    _agentDispatcher.LogEntryAdded -= OnAgentLogEntryAdded;
                    _agentDispatcher.FileChangeNotified -= OnAgentFileChangeNotified;
                }

                if (agentResult.Handoff != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"🤖 切换到 {agentResult.Handoff.TargetAgent} Agent...";

                    await TaskScheduler.Default;

                    editAgent.PlanUpdated += OnAgentPlanUpdated;
                    _agentDispatcher.PlanUpdated += OnAgentDispatcherPlanUpdated;
                    _agentDispatcher.LogEntryAdded += OnAgentLogEntryAdded;
                    _agentDispatcher.FileChangeNotified += OnAgentFileChangeNotified;
                    try
                    {
                        agentResult = await _agentDispatcher.ExecuteHandoffAsync(agentResult.Handoff, context);
                    }
                    finally
                    {
                        editAgent.PlanUpdated -= OnAgentPlanUpdated;
                        _agentDispatcher.PlanUpdated -= OnAgentDispatcherPlanUpdated;
                        _agentDispatcher.LogEntryAdded -= OnAgentLogEntryAdded;
                        _agentDispatcher.FileChangeNotified -= OnAgentFileChangeNotified;
                    }
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (agentResult.Success && agentResult.Plan != null)
                {
                    var plan = agentResult.Plan;

                    // ── 更新任务面板为完成状态 ──
                    if (plan.Steps.Count > 0)
                    {
                        try
                        {
                            string completeJs = ChatHtmlService.BuildAgentTaskPanelCompleteJs(plan);
                            await ChatWebView.CoreWebView2.ExecuteScriptAsync(completeJs);
                        }
                        catch { }
                    }

                    // ── 构建最终摘要并更新思考气泡为完成状态 ──
                    var summaryBuilder = new StringBuilder();

                    if (!string.IsNullOrWhiteSpace(agentResult.Content))
                    {
                        summaryBuilder.Append(agentResult.Content);
                    }
                    else
                    {
                        int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                        int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                        summaryBuilder.AppendLine(plan.IsCancelled
                            ? "## ⚠️ 任务已取消"
                            : failed > 0
                                ? $"## ⚠️ 任务完成 — {completed}/{plan.Steps.Count} 步成功，{failed} 步失败"
                                : $"## ✅ 任务完成 — {completed}/{plan.Steps.Count} 步全部成功");
                        summaryBuilder.AppendLine();

                        if (plan.ChangedFiles.Count > 0)
                        {
                            summaryBuilder.AppendLine($"**文件变更**: {plan.ChangedFiles.Count} 个文件");
                            foreach (var f in plan.ChangedFiles)
                            {
                                string fname = System.IO.Path.GetFileName(f.FilePath);
                                summaryBuilder.AppendLine($"- `{fname}` (+{f.LinesAdded} -{f.LinesRemoved})");
                            }
                            summaryBuilder.AppendLine();
                        }
                    }

                    // 追加思考过程到摘要后面（折叠显示）——作为独立 HTML 注入，不经过 Markdown 渲染
                    string thinkingText;
                    lock (_lock) { thinkingText = _agentThinkingContent.ToString(); }
                    string thinkingDetailsHtml = string.Empty;
                    if (!string.IsNullOrWhiteSpace(thinkingText))
                    {
                        // 将思考内容渲染为纯文本 HTML（保留换行）
                        string escapedThinking = System.Net.WebUtility.HtmlEncode(thinkingText)
                            .Replace("\n", "<br>");
                        thinkingDetailsHtml =
                            "<details class='reasoning-panel' style='margin-top:12px'>" +
                            "<summary>📋 执行过程</summary>" +
                            "<div class='reasoning-content'>" + escapedThinking + "</div>" +
                            "</details>";
                    }

                    string finalContent = summaryBuilder.ToString().TrimEnd();

                    // ── 更新现有的流式思考气泡为最终内容 ──
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = finalContent;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                        }
                    }
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, finalContent, string.Empty, isComplete: true);
                    // ── 最终渲染用 Markdown → HTML（执行过程作为独立 HTML 注入，不经过 Markdown）──
                    try
                    {
                        string finalRenderJs = ChatHtmlService.BuildFinalRenderJs(
                            _agentStreamingMsgIndex, finalContent, string.Empty, thinkingDetailsHtml);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalRenderJs);
                    }
                    catch { }

                    StatusLabel.Text = plan.IsCancelled
                        ? "⚠️ Agent 任务已取消"
                        : plan.ChangedFiles.Count > 0
                            ? $"✅ Agent 任务完成 ({plan.ChangedFiles.Count} 个文件变更)"
                            : $"✅ Agent 计划完成 ({plan.Steps.Count} 个步骤)";

                    if (plan.ChangedFiles.Count > 0)
                    {
                        _pendingAgentFileChanges = new List<FileChangeSummary>(plan.ChangedFiles);
                    }
                }
                else if (agentResult.Success && !string.IsNullOrWhiteSpace(agentResult.Content))
                {
                    // 将思考气泡更新为最终内容
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = agentResult.Content;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                        }
                    }
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, agentResult.Content, string.Empty, isComplete: true);
                    try
                    {
                        string frJs = ChatHtmlService.BuildFinalRenderJs(
                            _agentStreamingMsgIndex, agentResult.Content, string.Empty);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(frJs);
                    }
                    catch { }
                    StatusLabel.Text = "就绪";
                }
                else if (!agentResult.Success)
                {
                    string errorContent = $"❌ Agent 执行失败: {agentResult.ErrorMessage}";
                    lock (_lock)
                    {
                        if (_agentStreamingMsgIndex >= 0 && _agentStreamingMsgIndex < _messages.Count)
                        {
                            var msg = _messages[_agentStreamingMsgIndex];
                            msg.Content = errorContent;
                            msg.IsStreaming = false;
                            msg.IsRendered = true;
                        }
                    }
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, errorContent, string.Empty, isComplete: true);
                    try
                    {
                        string frJs = ChatHtmlService.BuildFinalRenderJs(
                            _agentStreamingMsgIndex, errorContent, string.Empty);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(frJs);
                    }
                    catch { }
                    StatusLabel.Text = $"❌ Agent 错误: {agentResult.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AgentDispatcher] 工作流异常: {ex.Message}", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusLabel.Text = $"❌ Agent 错误: {ex.Message}";
            }
            finally
            {
                _agentDispatcher.ActivePlan = null;
            }
        }

        /// <summary>
        /// AgentDispatcher 层面的 PlanUpdated 回调。
        /// 创建/更新底部任务流程面板（替代独立计划消息气泡）。
        /// </summary>
        private void OnAgentDispatcherPlanUpdated(AgentTaskPlan plan)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;
                try
                {
                    string pid = plan.PlanId;

                    // ── C# 层面防重：已创建过面板的，只做进度更新 ──
                    bool alreadyCreated;
                    lock (_lock) { alreadyCreated = _createdPlanIds.Contains(pid); }

                    if (!alreadyCreated)
                    {
                        lock (_lock) { _createdPlanIds.Add(pid); }
                        // 创建底部任务面板
                        string createJs = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(createJs);

                        // ── 输出规划信息到思考气泡 ──
                        AppendAgentThinking($"📋 **规划完成**: {plan.Title}");
                        AppendAgentThinking($"   共 {plan.Steps.Count} 个步骤");
                        foreach (var s in plan.Steps)
                            AppendAgentThinking($"   {s.Index}. {s.Title}");
                    }
                    else
                    {
                        // 更新任务面板进度
                        string updateJs = ChatHtmlService.BuildAgentTaskPanelUpdateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(updateJs);
                    }

                    StatusLabel.Text = $"🤖 Plan Agent: {plan.Steps.Count} 个步骤已规划";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AgentDispatcher] Plan UI 更新失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Agent 步骤状态变更回调：更新 WebView 中的步骤进度。
        /// 如果计划 HTML 尚未创建（单步计划场景），则先创建再更新。
        /// 通过 _createdPlanIds 在 C# 层面防止重复创建计划消息。
        /// </summary>
        private void OnAgentPlanUpdated(AgentTaskPlan plan)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string pid = plan.PlanId;

                    // ── C# 层面防重 ──
                    bool alreadyCreated;
                    lock (_lock) { alreadyCreated = _createdPlanIds.Contains(pid); }

                    if (!alreadyCreated)
                    {
                        lock (_lock) { _createdPlanIds.Add(pid); }
                        // 创建底部任务面板
                        string createJs = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(createJs);
                    }
                    else
                    {
                        // 更新任务面板进度
                        string updateJs = ChatHtmlService.BuildAgentTaskPanelUpdateJs(plan);
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(updateJs);
                    }

                    // ── 输出步骤状态变更到思考气泡 ──
                    if (plan.CurrentStepIndex > 0 && plan.CurrentStepIndex <= plan.Steps.Count)
                    {
                        var step = plan.Steps[plan.CurrentStepIndex - 1];
                        string statusKey = $"{step.Index}:{step.Status}";
                        if (step.Index != _lastReportedStepIndex || statusKey != _lastReportedStepStatus)
                        {
                            _lastReportedStepIndex = step.Index;
                            _lastReportedStepStatus = statusKey;
                            if (step.Status == AgentStepStatus.Completed)
                                AppendAgentThinking($"✅ 步骤 {step.Index} 完成: {step.Title}");
                            else if (step.Status == AgentStepStatus.Failed)
                                AppendAgentThinking($"❌ 步骤 {step.Index} 失败: {step.ResultSummary ?? step.Title}");
                            else if (step.Status == AgentStepStatus.InProgress)
                                AppendAgentThinking($"🔄 步骤 {step.Index}: {step.Title}");
                        }
                    }

                    StatusLabel.Text = $"🤖 Agent: 步骤 {plan.CurrentStepIndex}/{plan.Steps.Count}";
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] UI 更新失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 向实时思考气泡追加一行内容（Markdown 格式），并更新 DOM。
        /// </summary>
        private void AppendAgentThinking(string line)
        {
            lock (_lock)
            {
                if (_agentStreamingMsgIndex < 0) return;
                if (_agentThinkingContent.Length > 0)
                    _agentThinkingContent.AppendLine();
                _agentThinkingContent.Append(line);
            }
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null || _agentStreamingMsgIndex < 0) return;
                try
                {
                    string content;
                    lock (_lock) { content = _agentThinkingContent.ToString(); }
                    await UpdateStreamingMessageAsync(_agentStreamingMsgIndex, content, string.Empty, isComplete: false);
                }
                catch { }
            });
        }

        /// <summary>
        /// 将日志条目格式化为思考气泡中的可读行。
        /// 过滤掉过于技术性的日志，保留用户关心的信息。
        /// </summary>
        private static string FormatLogForThinking(AgentLogEntry entry)
        {
            string msg = entry.Message ?? string.Empty;

            // 过滤纯内部日志
            if (msg.StartsWith("[TokenUsage]") || msg.StartsWith("[Retry") || msg.StartsWith("[AgentDispatcher]"))
                return string.Empty;
            if (msg.Contains("上下文已累积") || msg.Contains("Planning 模式"))
                return string.Empty;

            // 格式化为可读的思考内容
            if (msg.StartsWith("📄") || msg.StartsWith("📖") || msg.Contains("已读取"))
                return msg;
            if (msg.StartsWith("✅") || msg.StartsWith("❌") || msg.StartsWith("⚠️"))
                return msg;
            if (msg.StartsWith("🔨") || msg.StartsWith("🔧"))
                return msg;
            if (msg.StartsWith("阶段") || msg.Contains("/3:"))
                return $"🔍 {msg}";
            if (msg.StartsWith("执行步骤") || msg.Contains("个步骤已规划"))
                return msg;
            if (msg.StartsWith("Plan Agent 开始规划"))
                return "🔍 开始分析任务，探索项目结构…";
            if (msg.StartsWith("计划创建完成"))
                return msg;
            if (msg.StartsWith("无计划"))
                return "📋 单步任务，直接执行代码修改…";
            if (msg.Contains("编译通过"))
                return "✅ 编译验证通过";
            if (msg.Contains("编译") && (msg.Contains("失败") || msg.Contains("错误")))
                return $"⚠️ {msg}";

            // ── ExploreAgent 委托和发现日志 ──
            if (msg.StartsWith("[EditAgent] 委托 ExploreAgent"))
                return $"🔍 {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[EditAgent] ExploreAgent 返回"))
                return $"📁 {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[EditAgent]"))
                return $"📝 {msg.Replace("[EditAgent] ", "")}";
            if (msg.StartsWith("[Explore] [Discover]"))
                return string.Empty; // Explore 内部发现日志不展示
            if (msg.StartsWith("[Explore]"))
                return $"🔍 {msg.Replace("[Explore] ", "")}";

            // 其他日志：以 INFO 级别展示简要信息
            if (entry.Level == "ERROR")
                return $"❌ {msg}";
            if (entry.Level == "WARN")
                return $"⚠️ {msg}";

            return string.Empty; // INFO 级别默认不展示，避免刷屏
        }

        /// <summary>
        /// Agent 日志条目回调：仅更新实时思考气泡。
        /// </summary>
        private void OnAgentLogEntryAdded(AgentLogEntry entry)
        {
            // ── 更新实时思考气泡 ──
            string thinkingLine = FormatLogForThinking(entry);
            if (!string.IsNullOrEmpty(thinkingLine))
                AppendAgentThinking(thinkingLine);
        }

        /// <summary>
        /// Agent 权限请求回调：在 WebView 中注入确认/拒绝按钮。
        /// 针对不同 ActionType 渲染不同的 UI：
        /// - "file_delete" → 文件删除确认卡片（含文件列表、确认/取消按钮）
        /// - 其他 → 通用权限确认弹窗
        /// </summary>
        private void OnAgentPermissionRequested(AgentPermissionRequest request)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null) return;

                try
                {
                    string js;
                    if (request.ActionType == "file_delete")
                    {
                        js = ChatHtmlService.BuildFileDeleteConfirmationJs(request);
                        StatusLabel.Text = $"🗑️ 等待确认删除: {request.Title}";
                    }
                    else
                    {
                        js = ChatHtmlService.BuildPermissionRequestJs(request);
                        StatusLabel.Text = $"🔐 等待确认: {request.Title}";
                    }

                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Agent] 权限 UI 注入失败: {ex.Message}");
                    _agentDispatcher?.RespondToPermission(request.RequestId, false);
                }
            });
        }

        /// <summary>
        /// Agent 文件变更实时通知回调：仅更新实时思考气泡。
        /// </summary>
        private void OnAgentFileChangeNotified(AgentFileChangeEventArgs args)
        {
            // ── 更新实时思考气泡 ──
            string icon = args.ChangeType.ToLowerInvariant() switch
            {
                "create" => "📄 新建",
                "delete" => "🗑️ 删除",
                _ => "✏️ 修改",
            };
            string fileName = System.IO.Path.GetFileName(args.FilePath);
            AppendAgentThinking($"{icon} `{fileName}` ({args.Detail})");
        }

        #endregion

        #region Retry / Edit / Version Navigation

        /// <summary>
        /// 记录一轮对话中的文件变更，用于后续重试/编辑时的回退判断。
        /// </summary>
        internal void RecordFileChangesForTurn(int userMsgIndex, List<FileChangeSummary> changedFiles)
        {
            if (changedFiles == null || changedFiles.Count == 0) return;

            lock (_lock)
            {
                var merged = changedFiles
                    .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList();

                _fileChangeHistory[userMsgIndex] = merged;
                Logger.Info($"[FileHistory] 记录第 {userMsgIndex} 轮文件变更: {merged.Count} 个文件");
            }
        }

        /// <summary>
        /// 从 _pendingAgentFileChanges 中消费并记录最近一次 Agent 的文件变更。
        /// </summary>
        private void RecordAgentFileChanges(int userMsgIndex)
        {
            List<FileChangeSummary>? changes = _pendingAgentFileChanges;
            _pendingAgentFileChanges = null;
            if (changes != null && changes.Count > 0)
            {
                RecordFileChangesForTurn(userMsgIndex, changes);
            }
        }

        /// <summary>
        /// 检查指定轮次是否有文件变更，如果有则询问用户是否回退。
        /// </summary>
        private async Task<bool> CheckAndRevertFileChangesAsync(int userMsgIndex)
        {
            List<FileChangeSummary>? changes;
            lock (_lock)
            {
                if (!_fileChangeHistory.TryGetValue(userMsgIndex, out changes) || changes.Count == 0)
                    return true;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var fileList = string.Join("\n", changes.Select(c =>
                $"  • {Path.GetFileName(c.FilePath)} (+{c.LinesAdded} -{c.LinesRemoved})"));

            var result = MessageBox.Show(
                $"此轮对话曾修改了 {changes.Count} 个文件：\n\n{fileList}\n\n" +
                "是否回退这些更改后再重新生成？\n\n" +
                "• 「是」— 回退文件到修改前的状态，然后重新生成\n" +
                "• 「否」— 保留当前文件状态，基于现有代码重新生成\n" +
                "• 「取消」— 不执行任何操作",
                "文件变更回退确认",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                StatusLabel.Text = "已取消";
                return false;
            }

            if (result == MessageBoxResult.Yes)
            {
                StatusLabel.Text = "正在回退文件变更…";
                int revertedCount = 0;
                int failedCount = 0;

                foreach (var change in changes)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(change.OriginalContent))
                        {
                            await Task.Run(() =>
                                File.WriteAllText(change.FilePath, change.OriginalContent, Encoding.UTF8));
                            revertedCount++;
                            Logger.Info($"[FileHistory] ✅ 已回退: {Path.GetFileName(change.FilePath)}");
                        }
                        else if (change.LinesAdded > 0 && change.LinesRemoved == 0)
                        {
                            if (File.Exists(change.FilePath))
                            {
                                await Task.Run(() => File.Delete(change.FilePath));
                                revertedCount++;
                                Logger.Info($"[FileHistory] ✅ 已删除新建文件: {Path.GetFileName(change.FilePath)}");
                            }
                        }
                        else
                        {
                            Logger.Warn($"[FileHistory] 无法回退（缺少原始内容）: {Path.GetFileName(change.FilePath)}");
                            failedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[FileHistory] 回退失败: {change.FilePath} - {ex.Message}", ex);
                        failedCount++;
                    }
                }

                lock (_lock) { _fileChangeHistory.Remove(userMsgIndex); }

                StatusLabel.Text = revertedCount > 0
                    ? $"✅ 已回退 {revertedCount} 个文件" + (failedCount > 0 ? $"，{failedCount} 个失败" : "")
                    : "未回退任何文件";
                Logger.Info($"[FileHistory] 回退完成: {revertedCount} 成功, {failedCount} 失败");
            }

            return true;
        }

        /// <summary>
        /// 重试某个助手消息：找到其对应的用户消息，重新发送请求。
        /// </summary>
        private async Task RetryMessageAsync(int assistantMsgIndex)
        {
            lock (_lock)
            {
                if (_isGenerating) return;
                if (assistantMsgIndex < 0 || assistantMsgIndex >= _messages.Count) return;
                var assistantMsg = _messages[assistantMsgIndex];
                if (assistantMsg.Role != "assistant") return;
            }

            try
            {
                int userMsgIndex = -1;
                ChatMessage? userMsg = null;
                lock (_lock)
                {
                    for (int i = assistantMsgIndex - 1; i >= 0; i--)
                    {
                        if (_messages[i].Role == "user")
                        {
                            userMsgIndex = i;
                            userMsg = _messages[i];
                            break;
                        }
                    }
                }

                if (userMsg == null) return;

                bool canProceed = await CheckAndRevertFileChangesAsync(userMsgIndex);
                if (!canProceed) return;

                ChatMessage oldAssistantMsg;
                lock (_lock)
                {
                    oldAssistantMsg = _messages[assistantMsgIndex];

                    if (!_assistantVersionHistory.ContainsKey(userMsgIndex))
                    {
                        _assistantVersionHistory[userMsgIndex] = new List<ChatMessage>();
                        _activeVersionIndex[userMsgIndex] = 0;
                    }

                    var history = _assistantVersionHistory[userMsgIndex];
                    int activeIdx = _activeVersionIndex.TryGetValue(userMsgIndex, out var idx) ? idx : 0;

                    if (activeIdx < history.Count)
                        history[activeIdx] = oldAssistantMsg;
                    else
                        history.Add(oldAssistantMsg);

                    TrimContextAfterUserMessage(userMsgIndex);
                }

                await ResendUserMessageAsync(userMsgIndex, userMsg);
            }
            catch (Exception ex)
            {
                Logger.Error($"RetryMessageAsync 异常: {ex.Message}", ex);
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
            }
        }

        /// <summary>
        /// 编辑某条用户消息后重新发送。
        /// </summary>
        private async Task EditMessageAsync(int userMsgIndex)
        {
            lock (_lock)
            {
                if (_isGenerating) return;
                if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;
                var msg = _messages[userMsgIndex];
                if (msg.Role != "user") return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string? originalContent = null;
            ChatMessage? userMsg = null;
            lock (_lock)
            {
                userMsg = _messages[userMsgIndex];
                originalContent = userMsg.OriginalContent ?? userMsg.Content;
            }

            InputTextBox.Text = originalContent ?? string.Empty;
            InputTextBox.Focus();
            InputTextBox.SelectAll();

            _attachedFilePaths.Clear();
            if (userMsg != null && userMsg.AttachedFiles.Count > 0)
            {
                foreach (var file in userMsg.AttachedFiles)
                {
                    if (!string.IsNullOrEmpty(file.FilePath) && File.Exists(file.FilePath))
                        _attachedFilePaths.Add(file.FilePath);
                }
            }
            if (_attachedFilePaths.Count == 0 && userMsg != null && userMsg.AttachedFileNames.Count > 0)
            {
                Logger.Info($"[Edit] AttachedFiles 路径不可用，已恢复 {userMsg.AttachedFileNames.Count} 个文件名到 UI（文件需重新上传)");
            }
            RefreshAttachedFilesUI();

            _pendingEditMsgIndex = userMsgIndex;
            StatusLabel.Text = $"✏️ 编辑消息（按 Enter 发送，Esc 取消）";
        }

        /// <summary>
        /// 待编辑的用户消息索引，-1 表示无。
        /// </summary>
        private int _pendingEditMsgIndex = -1;

        /// <summary>
        /// 处理编辑后重新发送：保存版本历史、回退上下文、重新发送。
        /// </summary>
        private async Task HandleEditResendAsync(int userMsgIndex, string newContent)
        {
            int editIndex = _pendingEditMsgIndex;
            _pendingEditMsgIndex = -1;

            lock (_lock) { _isGenerating = true; }
            UpdateButtonsState();
            InputTextBox.Text = string.Empty;
            StatusLabel.Text = "正在重新生成…";

            bool canProceed = await CheckAndRevertFileChangesAsync(userMsgIndex);
            if (!canProceed)
            {
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                StatusLabel.Text = "就绪";
                return;
            }

            try
            {
                ChatMessage? userMsg = null;
                lock (_lock)
                {
                    if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;
                    userMsg = _messages[userMsgIndex];
                    if (userMsg.Role != "user") return;

                    int assistantMsgIndex = -1;
                    if (userMsgIndex + 1 < _messages.Count && _messages[userMsgIndex + 1].Role == "assistant")
                        assistantMsgIndex = userMsgIndex + 1;

                    if (assistantMsgIndex >= 0)
                    {
                        var oldAssistant = _messages[assistantMsgIndex];
                        if (!_assistantVersionHistory.ContainsKey(userMsgIndex))
                        {
                            _assistantVersionHistory[userMsgIndex] = new List<ChatMessage>();
                            _activeVersionIndex[userMsgIndex] = 0;
                        }
                        var history = _assistantVersionHistory[userMsgIndex];
                        int activeIdx = _activeVersionIndex.TryGetValue(userMsgIndex, out var aidx) ? aidx : 0;
                        if (activeIdx < history.Count)
                            history[activeIdx] = oldAssistant;
                        else
                            history.Add(oldAssistant);
                    }

                    if (string.IsNullOrEmpty(userMsg.OriginalContent))
                        userMsg.OriginalContent = userMsg.Content;

                    userMsg.Content = newContent;

                    TrimContextAfterUserMessage(userMsgIndex);
                    _contextManager.AddUserMessage(newContent);
                }

                if (userMsg != null)
                    await ResendUserMessageAsync(userMsgIndex, userMsg);
            }
            catch (Exception ex)
            {
                Logger.Error($"HandleEditResendAsync 异常: {ex.Message}", ex);
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                StatusLabel.Text = "就绪";
            }
        }

        /// <summary>
        /// 导航到某个助手消息的不同版本。
        /// </summary>
        private async Task NavigateVersionAsync(int assistantMsgIndex, int direction)
        {
            try
            {
                int userMsgIndex = -1;
                lock (_lock)
                {
                    if (assistantMsgIndex < 0 || assistantMsgIndex >= _messages.Count) return;
                    if (_messages[assistantMsgIndex].Role != "assistant") return;

                    for (int i = assistantMsgIndex - 1; i >= 0; i--)
                    {
                        if (_messages[i].Role == "user")
                        {
                            userMsgIndex = i;
                            break;
                        }
                    }
                }

                if (userMsgIndex < 0) return;

                lock (_lock)
                {
                    if (!_assistantVersionHistory.TryGetValue(userMsgIndex, out var history) || history.Count == 0)
                        return;

                    int currentActive = _activeVersionIndex.TryGetValue(userMsgIndex, out var cidx) ? cidx : 0;
                    int newActive = currentActive + (direction > 0 ? 1 : -1);

                    if (newActive < 0) newActive = history.Count - 1;
                    if (newActive >= history.Count) newActive = 0;

                    if (newActive == currentActive) return;

                    var currentMsg = _messages[assistantMsgIndex];
                    if (currentActive < history.Count)
                        history[currentActive] = currentMsg;
                    else
                        history.Add(currentMsg);

                    var targetVersion = history[newActive];
                    targetVersion.VersionIndex = newActive + 1;
                    targetVersion.TotalVersions = history.Count;
                    targetVersion.MessageGroupId = $"ver_{userMsgIndex}";

                    _messages[assistantMsgIndex] = targetVersion;
                    _activeVersionIndex[userMsgIndex] = newActive;
                }

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Error($"NavigateVersionAsync 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从对话上下文中移除最后一个用户消息及其之后的所有条目。
        /// </summary>
        private void TrimContextAfterUserMessage(int userMsgIndex)
        {
            lock (_lock)
            {
                if (userMsgIndex < 0 || userMsgIndex >= _messages.Count) return;

                var userMsg = _messages[userMsgIndex];
                if (userMsg.Role != "user") return;

                _contextManager.TrimAfterLastUserMessage();
                Logger.Info($"[Retry/Edit] 已从对话上下文截断（用户消息索引: {userMsgIndex}）");
            }
        }

        /// <summary>
        /// 重新发送用户消息的核心逻辑。
        /// </summary>
        private async Task ResendUserMessageAsync(int userMsgIndex, ChatMessage userMsg)
        {
            if (_options == null || string.IsNullOrEmpty(_options.ApiKey))
            {
                StatusLabel.Text = "⚠️ 请先配置 API 密钥";
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                return;
            }

            InitializeApiService();
            if (_apiService == null)
            {
                lock (_lock) { _isGenerating = false; }
                UpdateButtonsState();
                return;
            }

            lock (_lock) { _isGenerating = true; }
            UpdateButtonsState();

            // ── 记录 Token 用量日志 ──
            var tokenStats = _contextManager.GetStats();
            Logger.Info($"[TokenUsage] 当前对话 Token: {tokenStats.EstimatedTokens:N0}/{tokenStats.TokenBudget:N0} ({tokenStats.UsagePercent:F1}%) | 轮次: {tokenStats.TurnCount} | 消息: {tokenStats.MessageCount}");

            lock (_lock)
            {
                int removeFrom = userMsgIndex + 1;
                if (removeFrom < _messages.Count)
                {
                    int removedCount = _messages.Count - removeFrom;
                    _messages.RemoveRange(removeFrom, removedCount);
                    Logger.Info($"[Retry/Edit] 已从消息列表移除 {removedCount} 条后续消息 (从索引 {removeFrom})");
                }
            }

            _currentStreamingCts?.Cancel();
            _currentStreamingCts?.Dispose();
            _currentStreamingCts = new CancellationTokenSource();

            ChatMessage? assistantMsg = null;
            int newAssistantIdx = -1;

            try
            {
                lock (_lock)
                {
                    bool userExistsInHistory = false;
                    string searchContent = userMsg.Content ?? string.Empty;
                    var history = _contextManager.GetConversationHistory();
                    foreach (var m in history)
                    {
                        if (m.Role == "user" &&
                            ((m.Content?.Contains(searchContent) == true) ||
                             (searchContent.Contains(m.Content ?? string.Empty)) ||
                             string.Equals((m.Content ?? string.Empty).Trim(), searchContent.Trim(), StringComparison.Ordinal)))
                        {
                            userExistsInHistory = true;
                            break;
                        }
                    }

                    if (!userExistsInHistory)
                    {
                        string fullUserContent = searchContent;
                        if (userMsg.AttachedFiles.Count > 0)
                        {
                            string fileContext = FileParserService.FormatParseResultsForContext(userMsg.AttachedFiles);
                            if (!string.IsNullOrEmpty(fileContext))
                                fullUserContent = fileContext + "\n" + fullUserContent;
                        }
                        _contextManager.AddUserMessage(fullUserContent);
                        Logger.Info("[Retry] 已将用户消息重新加入对话历史");
                    }
                }

                StatusLabel.Text = "DeepSeek 思考中…";

                string userContent = userMsg.Content ?? string.Empty;
                string enrichedContent = BuildRetryEnrichedContent(userMsg, userContent);

                if (_agentDispatcher != null && !string.IsNullOrEmpty(userContent) && !userContent.StartsWith("/"))
                {
                    var routing = await _agentDispatcher.RouteAsync(enrichedContent);

                    bool needsAgent = routing.TargetAgent == AgentType.Plan
                        || routing.TargetAgent == AgentType.Edit
                        || routing.NeedsPlanning;

                    if (needsAgent)
                    {
                        Logger.Info($"[Retry] 重新路由到 Agent: {routing.TargetAgent}" +
                            $", NeedsPlanning={routing.NeedsPlanning}");

                        string fileContext = string.Empty;
                        if (userMsg.AttachedFiles.Count > 0)
                            fileContext = FileParserService.FormatParseResultsForContext(userMsg.AttachedFiles);

                        string conversationContext = GetConversationContextForRetry();
                        if (!string.IsNullOrEmpty(conversationContext))
                        {
                            fileContext = string.IsNullOrEmpty(fileContext)
                                ? conversationContext
                                : conversationContext + "\n\n" + fileContext;
                        }

                        await RunAgentWorkflowAsync(enrichedContent, fileContext, routing);
                        RecordAgentFileChanges(userMsgIndex);

                        lock (_lock)
                        {
                            if (_assistantVersionHistory.TryGetValue(userMsgIndex, out var history) && history.Count > 0)
                            {
                                ChatMessage? firstNewAssistant = null;
                                for (int i = userMsgIndex + 1; i < _messages.Count; i++)
                                {
                                    if (_messages[i].Role == "assistant")
                                    {
                                        firstNewAssistant = _messages[i];
                                        break;
                                    }
                                }

                                if (firstNewAssistant != null)
                                {
                                    history.Add(firstNewAssistant);
                                    int totalVersions = history.Count;
                                    for (int i = userMsgIndex + 1; i < _messages.Count; i++)
                                    {
                                        if (_messages[i].Role == "assistant")
                                        {
                                            _messages[i].VersionIndex = totalVersions;
                                            _messages[i].TotalVersions = totalVersions;
                                            _messages[i].MessageGroupId = $"ver_{userMsgIndex}";
                                        }
                                    }
                                    _activeVersionIndex[userMsgIndex] = history.Count - 1;
                                    Logger.Info($"[Retry] Agent 版本历史: 共 {history.Count} 个版本, 当前版本 {totalVersions}");
                                }
                            }
                        }

                        RebuildMessagesHtml();
                        _browserInitialized = false;
                        UpdateBrowser();

                        lock (_lock) { _isGenerating = false; }
                        UpdateButtonsState();
                        StatusLabel.Text = "就绪";
                        return;
                    }
                }

                assistantMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ReasoningContent = string.Empty,
                    Timestamp = DateTime.Now,
                    IsStreaming = true,
                    IsRendered = false,
                };
                lock (_lock)
                {
                    _messages.Add(assistantMsg);
                    newAssistantIdx = _messages.Count - 1;
                }

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                var requestMessages = await BuildRequestMessagesAsync();
                var apiService = _apiService!;

                var reasoningBuffer = new StringBuilder();
                var contentBuffer = new StringBuilder();
                int streamRenderTick = 0;
                int lastReasoningLength = 0;

                await foreach (var chunk in apiService.ChatStreamAsync(requestMessages, null, _currentStreamingCts.Token))
                {
                    if (chunk.StartsWith("[THINKING]"))
                    {
                        var thinking = chunk.Substring(10);
                        reasoningBuffer.Append(thinking);
                        StatusLabel.Text = "DeepSeek 深度思考中…";

                        if (reasoningBuffer.Length - lastReasoningLength >= 80)
                        {
                            assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                            lastReasoningLength = reasoningBuffer.Length;
                            await UpdateStreamingMessageAsync(newAssistantIdx,
                                contentBuffer.ToString(), reasoningBuffer.ToString(), isComplete: false);
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
                            await UpdateStreamingMessageAsync(newAssistantIdx,
                                contentBuffer.ToString(), reasoningBuffer.ToString(), isComplete: false);
                        }
                    }
                }

                assistantMsg.ReasoningContent = reasoningBuffer.ToString();
                assistantMsg.Content = contentBuffer.ToString();
                assistantMsg.IsStreaming = false;

                Logger.Info($"[Retry] 流式结束: 内容长度={contentBuffer.Length}, 思考长度={reasoningBuffer.Length}");

                string finalJs = ChatHtmlService.BuildFinalRenderJs(
                    newAssistantIdx, contentBuffer.ToString(), reasoningBuffer.ToString());
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);

                _contextManager.AddAssistantMessage(
                    contentBuffer.ToString(),
                    reasoningBuffer.Length > 0 ? reasoningBuffer.ToString() : null);

                lock (_lock)
                {
                    if (_assistantVersionHistory.TryGetValue(userMsgIndex, out var history))
                    {
                        history.Add(assistantMsg);
                        assistantMsg.VersionIndex = history.Count;
                        assistantMsg.TotalVersions = history.Count;
                        assistantMsg.MessageGroupId = $"ver_{userMsgIndex}";
                        _activeVersionIndex[userMsgIndex] = history.Count - 1;
                        Logger.Info($"[Retry] 版本历史: 共 {history.Count} 个版本, 当前版本 {assistantMsg.VersionIndex}");
                    }
                }

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                var capturedMsg = assistantMsg;
                _ = Task.Run(() =>
                {
                    capturedMsg.HtmlContent = "rendered";
                    capturedMsg.IsRendered = true;
                    SaveCurrentSession();
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[Retry] 用户停止生成");
                if (assistantMsg != null)
                {
                    assistantMsg.Content += "\n\n*[已停止]*";
                    assistantMsg.IsStreaming = false;
                    string finalJs = ChatHtmlService.BuildFinalRenderJs(
                        newAssistantIdx, assistantMsg.Content, assistantMsg.ReasoningContent);
                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(finalJs);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Retry] API 出错", ex);
                if (assistantMsg != null)
                {
                    assistantMsg.Content = $"抱歉，发生了错误，请重试。\n\n```\n{ex.Message}\n```";
                    assistantMsg.IsStreaming = false;
                    await UpdateStreamingMessageAsync(newAssistantIdx,
                        assistantMsg.Content, assistantMsg.ReasoningContent, isComplete: true);
                }
            }
            finally
            {
                if (assistantMsg != null)
                    assistantMsg.IsStreaming = false;
                lock (_lock) { _isGenerating = false; }
                StatusLabel.Text = string.Empty;
                _currentStreamingCts?.Dispose();
                _currentStreamingCts = null;
                UpdateButtonsState();
            }
        }

        #endregion
    }
}
