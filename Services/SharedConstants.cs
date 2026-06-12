using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 项目中多处共用的常量定义，消除分散在各文件中的重复哈希集。
    /// </summary>
    internal static class SharedConstants
    {
        /// <summary>
        /// 源代码文件扩展名（用于项目发现、文件搜索、可点击文件名识别）。
        /// ExploreAgent 发现 + ChatHtmlService 链接 + SymbolSearchTool 回退共用。
        /// </summary>
        public static readonly HashSet<string> SourceFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // C# / .NET
            ".cs", ".vb", ".fs", ".fsx",
            ".xaml", ".xml", ".json", ".config", ".csproj", ".vbproj",
            ".razor", ".cshtml", ".vbhtml",
            // C/C++
            ".cpp", ".c", ".h", ".hpp",
            // Web
            ".js", ".ts", ".jsx", ".tsx", ".css", ".scss", ".less",
            ".html", ".htm",
            // Scripts
            ".py", ".ps1", ".psm1",
            // Other languages
            ".java", ".go", ".rs", ".swift", ".kt", ".php", ".rb", ".lua",
            ".proto", ".sql",
            // Docs / config
            ".md", ".txt", ".yml", ".yaml",
        };

        /// <summary>
        /// 文档/解决方案文件扩展名（ChatHtmlService 可点击链接专用追加）。
        /// 这些不是源代码，但在总结中常被引用，需要支持点击打开。
        /// </summary>
        public static readonly HashSet<string> DocFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".sln", ".slnx",
        };

        /// <summary>
        /// 项目目录排除列表（bin, obj, node_modules 等构建/缓存目录）。
        /// GrepSearchTool, ExploreAgent, EditAgent, SymbolSearchTool 共用。
        /// </summary>
        public static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "packages", ".vs",
            "Debug", "Release", "__pycache__", ".venv", "venv",
            "dist", "build", ".next", ".nuget", "out", ".github",
        };
    }
}
