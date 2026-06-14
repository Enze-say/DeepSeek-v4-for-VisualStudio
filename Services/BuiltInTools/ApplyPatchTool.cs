using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// apply_patch 工具 — 应用 *** Begin Patch / *** End Patch 格式的补丁。
    /// 解析和应用委托给 EditTools.ApplyPatchTool（OpenAI Codex 兼容的 Chunk 重建引擎）。
    /// 当 ApiService 注入后，支持完整的 Healing 流程（降级模型 → 完整模型 → create_file 兜底）。
    /// </summary>
    public class ApplyPatchTool : BuiltInToolBase
    {
        public override string Name => "apply_patch";

        /// <summary>
        /// API 服务引用（可选注入）。设置后启用 AI Healing 修复失败 Hunk。
        /// </summary>
        public DeepSeekApiService? ApiService { get; set; }

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "apply_patch",
                    Description = L["tool.apply_patch.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            patch = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.applyPatch.description"]
                            }
                        },
                        required = new[] { "patch" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            return LocalizationService.Instance["tool.applyPatch.displayText"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌") || toolResult.StartsWith("⚠️")) return toolResult;
            return LocalizationService.Instance["tool.applyPatch.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string patchText = GetStringArg(args, "patch");

            if (string.IsNullOrEmpty(patchText))
                return LocalizationService.Instance["tool.applyPatch.missingParam"];

            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);

            try
            {
                // ── 统一使用 EditTools 版的解析器（OpenAI Codex 兼容，正则前缀检测）──
                var patches = EditTools.ApplyPatchTool.ParsePatches(patchText);

                if (patches.Count == 0)
                {
                    return LocalizationService.Instance["tool.applyPatch.noPatchBlock"] + "\n"
                        + LocalizationService.Instance["tool.applyPatch.formatHint"] + "\n"
                        + "*** Begin Patch\n"
                        + "*** Update File: /path/to/file\n"
                        + "@@ some context\n"
                        + " context line\n"
                        + "- old line to remove\n"
                        + "+ new line to add\n"
                        + " context line\n"
                        + "*** End Patch";
                }

                // ── 有 ApiService → 使用完整 Healing 流程 ──
                if (ApiService != null)
                {
                    var editTool = new EditTools.ApplyPatchTool(ApiService, workspaceRoot ?? string.Empty);
                    var editResults = await editTool.ExecutePatchesAsync(patches, CancellationToken);

                    var results = new List<string>();
                    for (int i = 0; i < editResults.Count && i < patches.Count; i++)
                    {
                        var result = editResults[i];
                        if (result.Success)
                        {
                            results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied",
                                Path.GetFileName(result.FilePath), patches[i].Hunks.Count));
                        }
                        else
                        {
                            string errorMsg = result.ErrorMessage ?? LocalizationService.Instance["tool.applyPatch.hunkFail"];
                            results.Add(errorMsg);
                        }
                    }

                    return results.Count > 0
                        ? string.Join("\n", results)
                        : LocalizationService.Instance["tool.applyPatch.noAction"];
                }

                // ── 无 ApiService → 降级到静态 ApplySinglePatch（无 Healing）──
                {
                    var results = new List<string>();

                    foreach (var patch in patches)
                    {
                        string filePath = ResolvePath(patch.FilePath, workspaceRoot);

                        var result = EditTools.ApplyPatchTool.ApplySinglePatch(
                            patch, filePath,
                            File.Exists(filePath) ? await Task.Run(() => File.ReadAllText(filePath)) : string.Empty);

                        if (result.Success && !string.IsNullOrEmpty(result.FinalContent))
                        {
                            await Task.Run(() => File.WriteAllText(filePath,
                                EditStringMatcher.NormalizeToCrLf(result.FinalContent)));
                            results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied",
                                Path.GetFileName(filePath), patch.Hunks.Count));
                        }
                        else if (result.Success)
                        {
                            results.Add(LocalizationService.Instance.Format("tool.applyPatch.applied",
                                Path.GetFileName(filePath), patch.Hunks.Count));
                        }
                        else
                        {
                            string errorMsg = result.ErrorMessage ?? LocalizationService.Instance["tool.applyPatch.hunkFail"];
                            results.Add(errorMsg);
                        }
                    }

                    return results.Count > 0
                        ? string.Join("\n", results)
                        : LocalizationService.Instance["tool.applyPatch.noAction"];
                }
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.applyPatch.failed", ex.Message);
            }
        }
    }
}
