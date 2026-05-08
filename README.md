<div align="center">

# DeepSeek v4 for Visual Studio

**DeepSeek V4 · 深度思考 · MCP 协议 · Skills 技能系统 · 联网搜索 · OCR 图像识别**

*将 DeepSeek V4 大模型深度集成到 Visual Studio 2022 的全能 AI 编程助手*

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple.svg)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet.svg)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green.svg)]()

</div>

---

## 这是什么？

离开 IDE 去网页问 AI 的日子结束了。

**DeepSeek v4 for Visual Studio** 把 DeepSeek V4 模型直接嵌入你的编辑器。选中代码、粘贴截图、拖入文件——AI 就在旁边，随时响应。

它不只是聊天窗口，更是一套完整的 **AI 工作流系统**：Skills 技能引擎让你定义可复用的 AI 工作流，MCP 协议让你接入任意工具生态，三大 OCR 引擎能读懂你的报错截图。

---

## 能力一览

```
🧠 DeepSeek V4          流式对话 · 深度思考 (Reasoning) · 双模型可选
🔧 MCP 协议             多服务器连接 · Function Calling · 自定义工具扩展
📐 Skills 技能系统       斜杠命令 · 项目/用户/内置三级 · YAML 前置元数据
🌐 联网搜索              百度千帆 + DuckDuckGo 双引擎 · 额度耗尽自动切换
📄 文件解析              50+ 格式 · 代码/文档/PDF/Office 全支持
🔍 图像 OCR              Windows 内置 · PaddleOCR · MCP OCR  三引擎
💬 聊天窗口              WebView2 渲染 · Markdown 高亮 · 会话持久化
⚙️ 可视化配置            Tools → Options  一站式设置
```

---

## Skills 技能系统

> 这是本扩展区别于普通 AI 插件的核心特性。

### 什么是 Skill？

Skill 就是一个 Markdown 文件 (`SKILL.md`)，用 YAML 前置元数据描述"何时触发、怎么做"：

```markdown
---
name: code-review
description: '审查代码质量、安全性、性能。Use when: code review, PR review, 代码审查'
argument-hint: '[file path or code]'
user-invocable: true
---

# 代码审查

## 流程
1. 从正确性、安全性、性能、可维护性、最佳实践五个维度分析
2. 🔴 严重 → 🟡 中等 → 🟢 建议  按优先级列出问题
3. 为每个问题提供修复方案和代码示例
```

### 三级技能来源

| 级别 | 路径 | 说明 |
|------|------|------|
| 📁 **项目级** | `.github/skills/` `.agents/skills/` `.claude/skills/` | 随项目版本管理，团队共享 |
| 👤 **用户级** | `~/.copilot/skills/` `~/.agents/skills/` | 个人偏好，跨项目通用 |
| 🏭 **内置级** | `BuiltInSkills/`（随扩展发布） | 开箱即用，如 `code-review` |

### 使用方式

在聊天窗口输入 `/` 即可触发斜杠命令自动补全，选择技能后 AI 会加载对应的工作流指令。

```text
/code-review  UserService.cs
```

---

## 安装

### 推荐：下载 VSIX 安装

1. [**Releases**](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) → 下载 `DeepSeek_v4_for_VisualStudio.vsix`
2. 关闭 VS → 双击 `.vsix` → 安装
3. 重启 Visual Studio

### 进阶：源码编译

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# 用 VS 2022 打开 .slnx → Ctrl+Shift+B 编译 → F5 调试
```

---

## 快速上手

### ① 获取 API Key

[platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) → 创建 Key → 复制

### ② 配置

`工具` → `选项` → `DeepSeek Chat` → 粘贴 Key → 选模型

| 设置项 | 推荐值 |
|--------|--------|
| Selected Model | `deepseek-v4-pro` |
| Enable Deep Thinking | ✅ 开启 |
| Reasoning Effort | `high` |
| Search Provider | `DuckDuckGo`（免费） |

### ③ 开始对话

`视图` → `其他窗口` → `DeepSeek Chat`，或者点击工具栏 🧠 图标。

### ④ 常用操作

| 操作 | 方式 |
|------|------|
| 问代码问题 | 直接输入，AI 可读取当前打开的文件 |
| 解析文件内容 | 拖拽文件到聊天窗口 |
| 截图识别报错 | `Ctrl+V` 粘贴截图，自动 OCR |
| 联网查最新资料 | 勾选 🌐 联网搜索 |
| 调用 Skill | 输入 `/` 选择技能命令 |
| 配置 MCP 服务器 | 点击 🔌 MCP 按钮 |

---

## 项目结构

```
DeepSeek_v4_for_VisualStudio/
├── DeepSeek_v4_for_VisualStudioPackage.cs    VS 扩展入口 (AsyncPackage)
├── source.extension.vsixmanifest             VSIX 清单
├── VSCommandTable.vsct                       菜单/工具栏命令表
│
├── Commands/
│   └── ShowChatWindowCommand.cs              窗口命令
│
├── Models/
│   ├── DeepSeekModels.cs                     API 请求/响应 · 流式 · Function Calling
│   ├── McpTypes.cs                           MCP JSON-RPC 2.0 协议类型
│   ├── SkillDefinition.cs                    Skill 定义 · 来源枚举 · 发现结果
│   └── SkillSuggestionItem.cs                斜杠命令自动补全项
│
├── Services/
│   ├── DeepSeekApiService.cs                 API 通信（流式 + 思考模式）
│   ├── SkillService.cs                       ★ Skills 发现/解析/缓存/事件
│   ├── McpManagerService.cs                  MCP 多服务器管理 & 工具聚合
│   ├── McpStdioClient.cs                     stdio 传输客户端
│   ├── McpConfigStore.cs                     MCP 配置 JSON 持久化
│   ├── WebSearchService.cs                   百度千帆 + DuckDuckGo 搜索
│   ├── FileParserService.cs                  50+ 文件格式解析
│   ├── OcrService.cs                         Windows/PaddleOCR/MCP 三引擎
│   ├── ChatHtmlService.cs                    WebView2 HTML 模板
│   ├── ChatPersistenceService.cs             聊天记录本地持久化
│   └── AiPrompts.cs                          Prompt 集中管理
│
├── Settings/
│   ├── DeepSeekOptionsPage.cs                Tools→Options 配置页
│   └── DownloadLinkEditor.cs                 UI 编辑器
│
├── View/
│   ├── DeepSeekChatWindowPane.cs             VS ToolWindow 面板
│   ├── DeepSeekChatControl.xaml/.cs          WPF 主控件
│   ├── DeepSeekChatControl.Events.cs         事件处理（分部类）
│   ├── DeepSeekChatControl.Messaging.cs      消息收发（分部类）
│   ├── DeepSeekChatControl.Rendering.cs      界面渲染（分部类）
│   ├── DeepSeekChatControl.Sessions.cs       会话管理（分部类）
│   ├── DeepSeekChatControl.Clipboard.cs      剪贴板 OCR（分部类）
│   └── McpConfigDialog.xaml/.cs              MCP 配置对话框
│
├── Utils/
│   └── Logger.cs                             日志工具
│
└── Resources/                                图标/样式资源
```

---

## 技术栈

| 层 | 选型 |
|---|------|
| 运行时 | .NET Framework 4.7.2 · WPF |
| VS SDK | Microsoft.VisualStudio.SDK 17.14 |
| 聊天 UI | WebView2 (Chromium) |
| Markdown | Markdig 1.1.3 |
| 文档解析 | NPOI 2.8.0 · PdfPig 0.1.14 |
| OCR | Windows.Media.Ocr · PaddleOCR 3.0.1 · OpenCvSharp 4.10 |
| 序列化 | System.Text.Json |
| MCP | JSON-RPC 2.0 over stdio |

---

## 开发

### 环境

- Visual Studio 2022 v17.14+
- .NET Framework 4.7.2 SDK
- **Visual Studio Extension Development** 工作负载

### 调试

`F5` → 启动实验性 VS 实例 → 扩展自动加载

### 打包

```powershell
msbuild DeepSeek_v4_for_VisualStudio.csproj /p:Configuration=Release
# → bin/Release/net472/DeepSeek_v4_for_VisualStudio.vsix
```

---

## 常见问题

<details>
<summary><b>找不到聊天窗口？</b></summary>

重启 VS → `视图` → `其他窗口` → `DeepSeek Chat`。检查 `扩展` → `管理扩展` 是否已启用。
</details>

<details>
<summary><b>API Key 无效？</b></summary>

从 [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) 重新获取，确认有余额。配置路径：`工具` → `选项` → `DeepSeek Chat`。
</details>

<details>
<summary><b>OCR 中文不准？</b></summary>

`工具` → `选项` → `DeepSeek Chat` → OCR Settings → 切换到 `PaddleOCR`。模型随 NuGet 包自动部署。
</details>

<details>
<summary><b>怎么创建自定义 Skill？</b></summary>

在项目的 `.github/skills/my-skill/SKILL.md` 创建文件，写入 YAML 前置元数据 + Markdown 指令。在聊天窗口输入 `/my-skill` 即可调用。详见上方 [Skills 技能系统](#skills-技能系统)。
</details>

<details>
<summary><b>怎么接入 MCP 服务器？</b></summary>

点击聊天窗口 🔌 按钮 → 添加服务器配置（名称、启动命令、参数）→ 保存后自动连接加载工具。
</details>

<details>
<summary><b>WebView2 报错？</b></summary>

扩展已内置 x64 Runtime。如仍有问题：[下载 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。
</details>

---

## 贡献

1. Fork → 创建分支 → 修改 → Push → 提交 PR
2. Commit 格式使用 [Conventional Commits](https://www.conventionalcommits.org/)：`feat:` / `fix:` / `docs:` / `refactor:` / `chore:`

---

## 许可证

[MIT](LICENSE) © [zmy15](https://github.com/zmy15)

---

<div align="center">

**Issues** · [github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)

Powered by [DeepSeek](https://www.deepseek.com/)

</div>
