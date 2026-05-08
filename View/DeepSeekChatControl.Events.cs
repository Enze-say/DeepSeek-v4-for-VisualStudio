using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// UI 事件处理器：输入框键盘、按钮点击、下拉框选择、WebView2 事件等。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Event Handlers - Input

        /// <summary>
        /// 输入框键盘事件：Enter 直接发送消息，Ctrl+Enter 插入换行，Ctrl+V 粘贴图片。
        /// Ctrl+V 在隧道阶段拦截，优先于 TextBox 内部命令绑定，确保图片粘贴可靠触发。
        /// </summary>
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ── Ctrl+V: 优先检查剪贴板图片，在隧道阶段拦截 ──
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Logger.Info("PreviewKeyDown 检测到 Ctrl+V，检查剪贴板...");
                bool hasImage = TryPasteClipboardImage();
                if (hasImage)
                {
                    Logger.Info("Ctrl+V 已作为图片粘贴处理，拦截事件。");
                    e.Handled = true; // 拦截，阻止 TextBox 的默认文本粘贴
                    return;
                }
                Logger.Info("Ctrl+V 剪贴板无图片，交由 TextBox 默认文本粘贴处理。");
                // 无图片时放行，让 TextBox 默认行为处理文本粘贴
                return;
            }

            // ── Skill 弹出框键盘导航 ──
            if (SkillSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateSkillSuggestion(1);
                    return;
                }
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateSkillSuggestion(-1);
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                    AcceptSkillSuggestion();
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SkillSuggestionPopup.IsOpen = false;
                    return;
                }
            }

            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                // 如果弹出框打开，Enter 优先选择技能
                if (SkillSuggestionPopup.IsOpen)
                {
                    e.Handled = true;
                    AcceptSkillSuggestion();
                    return;
                }

                // 普通 Enter: 发送消息
                e.Handled = true;
                SendMessage();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // 如果弹出框打开，先关闭
            if (SkillSuggestionPopup.IsOpen)
            {
                SkillSuggestionPopup.IsOpen = false;
            }
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

        /// <summary>
        /// 文件上传按钮点击：打开文件选择对话框，将选中文件添加到附件列表。
        /// </summary>
        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要上传的文件",
                Filter = FileParserService.GetFileFilter(),
                Multiselect = true,
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string filePath in dlg.FileNames)
                {
                    if (!_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                    {
                        if (FileParserService.IsSupportedFormat(filePath))
                        {
                            _attachedFilePaths.Add(filePath);
                        }
                        else
                        {
                            StatusLabel.Text = $"⚠️ 不支持的文件格式: {System.IO.Path.GetExtension(filePath)}";
                        }
                    }
                }
                RefreshAttachedFilesUI();
            }
        }

        /// <summary>
        /// 移除单个已上传文件。
        /// </summary>
        private void RemoveAttachedFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string fileName)
            {
                // 根据文件名找到对应路径并移除
                var pathToRemove = _attachedFilePaths.FirstOrDefault(
                    p => string.Equals(System.IO.Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
                if (pathToRemove != null)
                {
                    _attachedFilePaths.Remove(pathToRemove);
                    RefreshAttachedFilesUI();
                }
            }
        }

        /// <summary>
        /// 刷新附件文件标签 UI。
        /// </summary>
        private void RefreshAttachedFilesUI()
        {
            AttachedFilesControl.ItemsSource = null;
            AttachedFilesControl.ItemsSource = _attachedFilePaths
                .Select(p => System.IO.Path.GetFileName(p))
                .ToList();
        }

        /// <summary>
        /// 清空已上传的文件列表。
        /// </summary>
        private void ClearAttachedFiles()
        {
            _attachedFilePaths.Clear();
            RefreshAttachedFilesUI();
        }

        #endregion

        #region Event Handlers - Skill Suggestions

        /// <summary>
        /// 输入框文本变更：检测 / 触发 Skill 自动补全弹出框。
        /// </summary>
        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var text = InputTextBox.Text;

                // 不以 / 开头 → 关闭弹出框
                if (string.IsNullOrEmpty(text) || !text.StartsWith("/"))
                {
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                // 命令已包含空格 → 用户正在输入参数，关闭弹出框
                // 例如: "/code-review " 或 "/create-skill my-skill" 不应触发弹出框
                if (text.Contains(' '))
                {
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                // 提取 / 后的文本用于过滤
                var filterText = text.Length > 1 ? text.Substring(1).ToLowerInvariant() : string.Empty;
                UpdateSkillSuggestions(filterText);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Skill] 文本变更处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新 Skill 建议列表。
        /// </summary>
        private void UpdateSkillSuggestions(string filterText)
        {
            try
            {
                if (_skillDiscoveryResult == null)
                {
                    // 同步获取（首次使用时已有缓存）
                    _skillDiscoveryResult = SkillService.Instance.DiscoverSkillsAsync(_solutionPath).Result;
                }

                var allSkills = _skillDiscoveryResult?.Skills ?? new List<SkillDefinition>();

                // 添加内置元命令
                var metaCommands = new List<SkillSuggestionItem>
                {
                    new SkillSuggestionItem
                    {
                        Name = "help",
                        Description = "显示所有可用技能和帮助信息",
                        Source = "⚙️ 内置",
                        IsMeta = true,
                    },
                    new SkillSuggestionItem
                    {
                        Name = "create-skill",
                        Description = "创建新的自定义 Skill 模板文件",
                        Source = "⚙️ 内置",
                        IsMeta = true,
                    },
                    new SkillSuggestionItem
                    {
                        Name = "refresh-skills",
                        Description = "强制刷新技能缓存",
                        Source = "⚙️ 内置",
                        IsMeta = true,
                    },
                };

                // 添加用户/项目技能
                var skillItems = allSkills
                    .Select(s => new SkillSuggestionItem
                    {
                        Name = s.Name,
                        Description = s.Description,
                        Source = s.Source switch
                        {
                            SkillSource.Project => "📁 项目",
                            SkillSource.User => "👤 用户",
                            SkillSource.BuiltIn => "📦 内置",
                            _ => "❓"
                        },
                        IsMeta = false,
                        SkillDefinition = s,
                    })
                    .ToList();

                var allItems = metaCommands.Concat(skillItems).ToList();

                // 按过滤文本筛选
                if (!string.IsNullOrEmpty(filterText))
                {
                    var parts = filterText.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    var commandPart = parts.Length > 0 ? parts[0] : filterText;

                    allItems = allItems
                        .Where(item => item.Name.StartsWith(commandPart, StringComparison.OrdinalIgnoreCase)
                                       || item.Description.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // 更新 ListBox
                SkillSuggestionListBox.ItemsSource = allItems;

                if (allItems.Count > 0)
                {
                    SkillSuggestionListBox.SelectedIndex = 0;
                    SkillSuggestionPopup.IsOpen = true;

                    // 动态调整弹出框宽度
                    var maxNameLen = allItems.Max(item => item.Name.Length);
                    // 不显式设置宽度，让 WPF 自动布局
                }
                else
                {
                    SkillSuggestionPopup.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Skill] 更新建议列表失败: {ex.Message}");
                SkillSuggestionPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 导航 Skill 建议列表（上下键）。
        /// </summary>
        private void NavigateSkillSuggestion(int direction)
        {
            if (!SkillSuggestionPopup.IsOpen || SkillSuggestionListBox.Items.Count == 0)
                return;

            var newIndex = SkillSuggestionListBox.SelectedIndex + direction;
            if (newIndex < 0)
                newIndex = SkillSuggestionListBox.Items.Count - 1;
            else if (newIndex >= SkillSuggestionListBox.Items.Count)
                newIndex = 0;

            SkillSuggestionListBox.SelectedIndex = newIndex;
            SkillSuggestionListBox.ScrollIntoView(SkillSuggestionListBox.SelectedItem);
        }

        /// <summary>
        /// 接受当前选中的 Skill 建议（Enter/Tab 键）。
        /// </summary>
        private void AcceptSkillSuggestion()
        {
            if (!SkillSuggestionPopup.IsOpen || SkillSuggestionListBox.SelectedItem == null)
                return;

            if (SkillSuggestionListBox.SelectedItem is SkillSuggestionItem item)
            {
                // 替换输入框文本为 /skill-name
                var skillName = item.Name;

                // ── 日志：记录用户从弹出框选择了技能 ──
                if (item.IsMeta)
                    Logger.Info($"[Skill] 用户从弹出框选择元命令: /{skillName}");
                else
                    Logger.Info($"[Skill] 用户从弹出框选择技能: /{skillName} | 描述: {item.Description} | 来源: {item.Source}");

                // 保留 / 后的其他参数（如果用户在 /skill-name 后输入了额外文本）
                var currentText = InputTextBox.Text;
                var spaceIndex = currentText.IndexOf(' ');
                var extraArgs = spaceIndex > 0 ? currentText.Substring(spaceIndex) : string.Empty;

                if (item.IsMeta)
                {
                    // 元命令直接执行
                    InputTextBox.Text = $"/{skillName}{extraArgs}";
                }
                else
                {
                    // 技能命令：格式为 /skill-name [description hint]
                    var hint = item.SkillDefinition?.ArgumentHint;
                    InputTextBox.Text = $"/{skillName}{extraArgs}";
                }

                // 将光标移到末尾
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                InputTextBox.Focus();
            }

            SkillSuggestionPopup.IsOpen = false;
        }

        /// <summary>
        /// ListBox 选中项变更。
        /// </summary>
        private void SkillSuggestionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 预留：选中项变更时的处理
        }

        /// <summary>
        /// ListBox 键盘事件：Enter/Tab 接受选择，Escape 关闭弹出框。
        /// 解决鼠标点击选中后按 Enter 无反应的问题。
        /// </summary>
        private void SkillSuggestionListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                AcceptSkillSuggestion();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SkillSuggestionPopup.IsOpen = false;
                InputTextBox.Focus();
                return;
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // 让 ListBox 默认处理导航
                e.Handled = false;
            }
        }

        /// <summary>
        /// ListBox 双击选中技能。
        /// </summary>
        private void SkillSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            AcceptSkillSuggestion();
        }

        /// <summary>
        /// 输入框失去焦点时关闭弹出框（延迟关闭，允许点击 ListBox 项）。
        /// </summary>
        private async void InputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 延迟关闭，确保 ListBox 的 MouseDoubleClick 能先触发
            await System.Threading.Tasks.Task.Delay(200);
            if (SkillSuggestionPopup.IsOpen && !SkillSuggestionListBox.IsKeyboardFocusWithin)
            {
                SkillSuggestionPopup.IsOpen = false;
            }
        }

        #endregion

        #region Event Handlers - Session & Model

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

        #endregion

        #region Event Handlers - Web Search

        /// <summary>
        /// 联网搜索开关按钮点击：切换开启/关闭，联动下拉框可见性。
        /// </summary>
        private void WebSearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 切换状态
                if (_webSearchEngine == "Off")
                {
                    _webSearchEngine = "Baidu";
                    WebSearchEngineComboBox.SelectedIndex = 0; // 默认百度
                }
                else
                {
                    _webSearchEngine = "Off";
                }

                Logger.Info($"联网搜索状态切换为: {_webSearchEngine}");
                UpdateWebSearchToggleAppearance();

                if (_webSearchEngine != "Off")
                {
                    ApplyWebSearchConfig();
                }

                // 提示百度未配置 Key 的情况
                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = "⚠️ 百度搜索需要 API Key，请在 工具→选项→DeepSeek Chat→Web Search 中配置";
                }
                else
                {
                    StatusLabel.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSearchToggleButton_Click 异常: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// MCP 配置按钮点击：打开 MCP 服务器配置对话框。
        /// </summary>
        private void McpConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 从文件加载当前配置
                var currentConfigs = McpConfigStore.Load();

                var dialog = new McpConfigDialog(currentConfigs, savedServers =>
                {
                    // 保存到文件
                    McpConfigStore.Save(savedServers);
                    Logger.Info($"[MCP Config] 已保存 {savedServers.Count} 个 MCP 服务器配置");
                });

                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();

                // 重新初始化 MCP 连接
                InitializeMcp();

                // 刷新 MCP 按钮状态
                UpdateMcpButtonAppearance();
            }
            catch (Exception ex)
            {
                Logger.Error($"McpConfigButton_Click 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更新 MCP 配置按钮的外观，反映连接状态。
        /// </summary>
        private void UpdateMcpButtonAppearance()
        {
            try
            {
                if (_mcpManager == null || _mcpManager.AllTools.Count == 0)
                {
                    McpConfigButton.ToolTip = "配置 MCP 服务器（未连接）";
                    McpConfigButton.Foreground = new SolidColorBrush(
                        Color.FromRgb(0x88, 0x88, 0x88));
                }
                else
                {
                    int toolCount = _mcpManager.AllTools.Count;
                    McpConfigButton.ToolTip = $"MCP 已连接: {toolCount} 个工具可用 (点击配置)";
                    McpConfigButton.Foreground = new SolidColorBrush(
                        Color.FromRgb(0x4E, 0xC9, 0xB0));
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[MCP] 更新按钮外观失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据当前搜索引擎更新切换按钮的外观和 ToolTip。
        /// </summary>
        private void UpdateWebSearchToggleAppearance()
        {
            bool isOn = _webSearchEngine != "Off";
            // 按钮颜色与 Tooltip
            if (isOn)
            {
                // 保持激活色（若需区分引擎可再细化）
                WebSearchToggleButton.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x6C, 0xAF, 0xD9));
                WebSearchToggleButton.ToolTip = "联网搜索: 已开启 (点击关闭)";
            }
            else
            {
                WebSearchToggleButton.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x88, 0x88, 0x88));
                WebSearchToggleButton.ToolTip = "联网搜索: 已关闭 (点击开启)";
            }

            // 下拉框可见性：开启时显示，关闭时隐藏
            WebSearchEngineComboBox.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        }


        /// <summary>
        /// 联网搜索引擎选择变更事件（保留兼容，但 UI 已隐藏此控件）。
        /// </summary>
        private void WebSearchEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (WebSearchEngineComboBox.SelectedIndex < 0) return;

                string? selected = WebSearchEngineComboBox.SelectedItem as string;
                string newEngine = selected switch
                {
                    string s when s.Contains("百度") => "Baidu",
                    string s when s.Contains("DuckDuckGo") => "DuckDuckGo",
                    _ => "Off"
                };

                if (_webSearchEngine == newEngine) return; // 避免循环触发

                _webSearchEngine = newEngine;
                Logger.Info($"联网搜索引擎切换为: {_webSearchEngine}");
                UpdateWebSearchToggleAppearance();
                ApplyWebSearchConfig();

                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = "⚠️ 百度搜索需要 API Key，请在 工具→选项→DeepSeek Chat→Web Search 中配置";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSearchEngineComboBox_SelectionChanged 异常: {ex.Message}", ex);
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
    }
}
