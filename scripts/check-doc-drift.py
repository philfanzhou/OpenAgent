#!/usr/bin/env python3
"""文档漂移检查：活文档引用已删除符号或失效源码路径时失败。

用法：python3 scripts/check-doc-drift.py

范围：仓库根 README.md / AGENTS.md、docs/（overview、modules、integrations、
database、operations 及 docs/README.md）、Backend/tests/README.md、
Frontend/OpenAgent.Chat/README.md。docs/ 下 archive、test-reports、decisions、
planning、architecture 与 images 为历史记录或规划目录，合法描述过去状态，不检查。

规则：
1. FORBIDDEN_* 中的符号/字符串不得出现在活文档（类型名按 ASCII 整词边界匹配，
   其余按字面子串匹配）。刻意声明「某物已删除」的行，行尾加
   `<!-- docguard-allow -->` 豁免。
2. 反引号内以 Backend/ 或 Frontend/ 开头的路径片段必须实际存在
   （忽略通配、省略号与行号后缀；顿号/逗号分隔的多文件列表逐段检查）。

从代码删除类型、Redis 键或工具命名时：先加入禁引清单，再运行本脚本同步文档；
维护规则见 .agent/rules/doc-standards.md。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

EXCLUDED_DOC_DIRS = {"archive", "test-reports", "decisions", "planning", "architecture", "images"}
EXTRA_DOC_FILES = [
    ROOT / "README.md",
    ROOT / "AGENTS.md",
    ROOT / "Backend" / "tests" / "README.md",
    ROOT / "Frontend" / "OpenAgent.Chat" / "README.md",
]

ALLOW_MARKER = "<!-- docguard-allow -->"

# 已从源码删除的类型/组件。使用 ASCII 词边界（(?<![A-Za-z0-9_])）而非 \b，
# 使中文紧邻的符号名也能命中，同时不匹配 RuntimeAgentExecutor 这类长名前缀。
FORBIDDEN_WORDS = [
    "AgentExecutor",
    "AgentFactory",
    "AgentLease",
    "AgentExecutionScope",
    "ToolInvocationScope",
    "IAgentPipeline",
    "AIAgent",
    "ChatClientAgent",
    "McpRegistry",
    "IMcpRegistry",
    "McpTransportFactory",
    "McpConnectionTester",
    "McpToolFactory",
    "McpClientPool",
    "McpClientOptionsFactory",
    "McpExecutionOptions",
    "RedisSkillCatalogStore",
    "RedisMcpRegistrar",
    "DeferredToolInjector",
]
# 已删除的键名/工具命名/包名，按字面子串匹配。
FORBIDDEN_LITERALS = [
    "mcp__",
    "mcp:registry",
    "mcp:published",
    "read_mcp_resource",
    "Microsoft Agent Framework",
    "Microsoft.Agents.AI",
]

PATH_ROOTS = ("Backend/", "Frontend/")
LINE_REF = re.compile(r":\d+(-\d+)?$")
GLOB_CHARS = set("*?[{")
PATH_SPLIT = re.compile(r"[、,;，；\s]+")
BACKTICK = re.compile(r"`([^`\n]+)`")
TRAILING_PUNCT = "。，.;；:：)）(（\"'"


def live_docs() -> list[Path]:
    files = [path for path in EXTRA_DOC_FILES if path.exists()]
    docs_dir = ROOT / "docs"
    for path in sorted(docs_dir.rglob("*.md")):
        rel = path.relative_to(docs_dir)
        if any(part in EXCLUDED_DOC_DIRS for part in rel.parts[:-1]):
            continue
        files.append(path)
    return files


def path_candidates(span: str) -> list[str]:
    candidates = []
    for fragment in PATH_SPLIT.split(span):
        fragment = fragment.strip(TRAILING_PUNCT)
        if not fragment.startswith(PATH_ROOTS):
            continue
        fragment = LINE_REF.sub("", fragment)
        if any(ch in GLOB_CHARS for ch in fragment) or "..." in fragment:
            continue
        candidates.append(fragment)
    return candidates


def main() -> int:
    docs = live_docs()
    findings: list[str] = []
    word_patterns = [
        (symbol, re.compile(r"(?<![A-Za-z0-9_])" + re.escape(symbol) + r"(?![A-Za-z0-9_])"))
        for symbol in FORBIDDEN_WORDS
    ]
    for doc in docs:
        rel = doc.relative_to(ROOT).as_posix()
        text = doc.read_text(encoding="utf-8")
        for lineno, line in enumerate(text.splitlines(), start=1):
            if ALLOW_MARKER in line:
                continue
            for symbol, pattern in word_patterns:
                if pattern.search(line):
                    findings.append(f"{rel}:{lineno}: 引用已删除符号 {symbol}")
            for literal in FORBIDDEN_LITERALS:
                if literal in line:
                    findings.append(f"{rel}:{lineno}: 引用已删除组件/键 {literal}")
            for span in BACKTICK.findall(line):
                for candidate in path_candidates(span):
                    if not (ROOT / candidate).exists():
                        findings.append(f"{rel}:{lineno}: 引用路径不存在 {candidate}")
    for finding in findings:
        print(finding)
    summary = f"检查 {len(docs)} 个文档，发现 {len(findings)} 处问题"
    print(summary)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
