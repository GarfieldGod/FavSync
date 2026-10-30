#!/usr/bin/env python
"""文档与注释卫生检查：拦住「只有作者本机看得懂」的内容。

六条硬规则：换个人拿到这份仓库，能不能看懂、能不能跑。

    1 不写开发机路径（盘符、用户目录、工具链安装路径）→ 用占位符
    2 不写真实网络地址（家里网段、虚拟组网）→ 示例用 192.0.2.x
    3 不提别的私有项目（读者不认识，零信息）
    4 不引用不入库的目录（读者拿不到 = 没写）
    5 不写内部编号（里程碑、批次、§ 章节号不说是哪份文档）
    6 不写变更历史（git 已记；要留就写「为什么现在这样」）

六类可判定的问题：

    leak      开发机绝对路径 / 用户名 / 私网 IP / 别的私有项目名
    ignored   引用了不入库的目录
    milestone 内部里程碑编号、内部批次编号
    section   §N 没说明是哪份文档的哪一节
    archived  引用已归档或已删除的文档
    link      md 相对链接指向不存在的文件
    history   变更历史日期戳

用法：
    python scripts/check/check_hygiene.py             # 扫全仓，只对 error 级判定失败
    python scripts/check/check_hygiene.py --strict    # 连警告也判定失败
    python scripts/check/check_hygiene.py -q          # 只打印失败项

退出码 0 = 通过，非 0 = 有问题，CI 应视为失败。

行内豁免：在行尾写 ``hygiene:ignore``。只有两种正当理由——
检查器自身的规则表，以及文档里演示「错误写法」的示例。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent

# ── 扫描范围 ────────────────────────────────────────────────
SCAN_EXT = {
    ".md", ".cs", ".csproj", ".props", ".targets", ".slnx", ".js", ".css",
    ".html", ".sh", ".cmd", ".py", ".txt", ".json", ".editorconfig",
    ".gitignore", ".gitattributes",
}
SKIP_DIRS = {
    "bin", "obj", "publish", "artifacts", ".archive", ".workbuddy", ".git",
    ".idea", ".vs", "node_modules", ".favsync", ".incoming", ".trash",
}
# 检查器自身要列出规则清单，天然命中规则，整文件跳过
SELF = Path(__file__).name
# .gitignore 的正文就是忽略规则，必须写目录名
GITIGNORE_OK = {"ignored"}

# ── 规则表 ─────────────────────────────────────────────────
# 私有项目名：同机其它私有项目（本仓自己不算）
PRIVATE_NAMES = ["MediaRack", "MyVideo", "BilibiliVideoCompose"]


def _private_re():
    return re.compile(r"(?:" + "|".join(PRIVATE_NAMES) + r")", re.I)


RULES = [
    # (名称, 级别, 正则, 说明, 仅限后缀)
    ("leak", "error", re.compile(r"[A-Za-z]:[\\/]{1,2}Users[\\/]", re.I), "含用户名的绝对路径", None),
    ("leak", "error", re.compile(r"[A-Za-z]:[\\/]{1,2}Application[\\/]", re.I), "开发机工具链绝对路径", None),
    ("leak", "error", _private_re(), "其它私有项目名", None),
    (
        "leak",
        "error",
        re.compile(
            r"(?<![\d.])("
            r"10\.\d{1,3}\.\d{1,3}\.\d{1,3}"
            r"|192\.168\.\d{1,3}\.\d{1,3}"
            r"|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}"
            r"|100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}"
            r")(?![\d.])"
        ),
        "内网 / 虚拟网 IP",
        None,
    ),
    # 盘符路径只在文档里报警：代码注释里的 D:\视频 是示例，文档里的则可能是真机路径
    ("leak", "warn", re.compile(r"(?<![A-Za-z])[A-Za-z]:[\\/][^\s`'\"|)]*"), "盘符路径（确认是示例而非真机路径）", {".md"}),
    # .favsync / .incoming / .trash 是「库内运行时布局」，属于产品设计，不是仓库目录
    ("ignored", "error", re.compile(r"(?:^|[\s`'\"(（\[/])\.(?:archive|workbuddy)/"), "引用不入库目录", None),
    ("milestone", "error", re.compile(r"\bM\d+(?:\.\d+)?\b"), "内部里程碑编号", None),
    ("milestone", "error", re.compile(r"\bT\d+\b(?=[\s）)、,，:：]|$)"), "内部批次编号", None),
    ("section", "error", re.compile(r"§\d+(?:\.\d+)?"), "章节号未指明哪份文档", None),
    ("archived", "error", re.compile(r"design-report|DESIGN-v\d|docs/archive"), "引用已归档文档", None),
    ("history", "warn", re.compile(r"(?:^|[^\d])20\d{2}-\d{2}(?:-\d{2})?"), "变更历史日期戳（git 已记）", None),
]

# 豁免：同一行里已经写明是哪份文档，§N 就是可解析的
SECTION_QUALIFIER = re.compile(r"DESIGN(?:-COLD-BACKUP)?\.md|agent-rules\.md|README\.md")
LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")


def iter_files():
    for path in ROOT.rglob("*"):
        if not path.is_file():
            continue
        if any(part in SKIP_DIRS for part in path.relative_to(ROOT).parts):
            continue
        if path.name == SELF:
            continue
        if path.suffix.lower() not in SCAN_EXT and path.name not in (
            ".gitignore", ".gitattributes", ".editorconfig"
        ):
            continue
        yield path


def xml_comment_lines(text: str):
    """返回落在 <!-- ... --> 注释块内的行号集合。

    XML 的非注释区是数据（矢量图 path 里的 `M40` 是 moveto 坐标），
    只有注释才是人写的说明，里程碑编号这类规则只该看注释。
    """
    inside, depth = set(), False
    for i, line in enumerate(text.split("\n"), 1):
        start, end = "<!--" in line, "-->" in line
        if depth or start:
            inside.add(i)
        if start and not end:
            depth = True
        elif end:
            depth = False
    return inside


def scan_text(path: Path):
    """逐行跑规则，返回 (级别, 规则名, 行号, 说明, 片段)。"""
    hits = []
    text = path.read_text(encoding="utf-8", errors="replace")
    suffix = path.suffix.lower()
    is_gitignore = path.name == ".gitignore"
    xml_ok = xml_comment_lines(text) if suffix == ".xml" else None

    for lineno, line in enumerate(text.split("\n"), 1):
        if "hygiene:ignore" in line:
            continue
        for name, level, pattern, desc, only in RULES:
            if is_gitignore and name in GITIGNORE_OK:
                continue
            if only is not None and suffix not in only:
                continue
            if xml_ok is not None and name == "milestone" and lineno not in xml_ok:
                continue
            frags = []
            for m in pattern.finditer(line):
                frag = m.group(0).strip()
                # 网段基址（a.b.0.0 这种写法）是代码里定义私网范围用的，不是某台机器的地址
                if name == "leak" and frag.endswith(".0.0"):
                    continue
                if frag not in frags:
                    frags.append(frag)
            if not frags:
                continue
            if name == "section" and SECTION_QUALIFIER.search(line):
                continue
            shown = ", ".join(frags[:3]) + ("…" if len(frags) > 3 else "")
            hits.append((level, name, lineno, desc, shown))

    if suffix == ".md":
        for m in LINK.finditer(text):
            target = m.group(1)
            if target.startswith(("http://", "https://", "mailto:", "#")):
                continue
            target = target.split("#")[0]
            if not target:
                continue
            if not (path.parent / target).exists():
                lineno = text[: m.start()].count("\n") + 1
                hits.append(("error", "link", lineno, "md 链接目标不存在", target))
    return hits


def main(argv):
    strict = "--strict" in argv
    quiet = "-q" in argv

    errors = warns = 0
    for path in sorted(iter_files()):
        rel = path.relative_to(ROOT).as_posix()
        for level, name, lineno, desc, frag in scan_text(path):
            if level == "error":
                errors += 1
            else:
                warns += 1
            if quiet and level != "error":
                continue
            mark = "✗" if level == "error" else "!"
            print(f"{mark} {name:<9} {rel}:{lineno}  {desc}  → {frag[:70]}")

    print(f"\n{errors} 个错误，{warns} 个警告")
    if errors or (strict and warns):
        print("按本文件顶部的六条规则逐条清理；改完重跑本脚本。")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
