#!/usr/bin/env python3
"""
XAML 绑定体检：找出所有 Binding 路径中，在 C# 源码里找不到对应公开成员的悬空绑定。

WPF 绑定失败时不会抛异常，只会在输出窗口打印一条 Path=... 的错误，UI 上表现为
"文字空白 / 按钮不显示 / 转换器拿到 null"。这类问题靠肉眼看截图很难定位，
用静态扫描可以一次性把清单列出来。

用法：python tools/xaml_binding_audit.py
"""
from __future__ import annotations

import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

XAML_DIRS = [ROOT / "VoWin" / "Views"]
CS_DIRS = [ROOT / "VoWin", ROOT / "voSharp" / "src"]

BINDING_RE = re.compile(r'\{Binding\s+([^{},]+?)(?:[,\s}])', re.IGNORECASE)
CONVERTER_RE = re.compile(r'Converter=\{StaticResource\s+(\w+)\}')

# C# 成员：public/protected 属性、字段、方法，以及 [ObservableProperty]/[RelayCommand]
# 生成的实际成员名（Foo -> FooCommand / FooProperty）
PROP_RE = re.compile(
    r'public\s+(?:virtual\s+|override\s+|static\s+|async\s+|sealed\s+|new\s+|partial\s+)*'
    r'(?:[\w\.<>\?\[\],\s]+?)\s+(\w+)\s*(?:\{|=>|\()'
)
OBSERVABLE_RE = re.compile(
    r'\[ObservableProperty\][^\n]*\n[^\n]*?\b(\w+)\s*(?:=|;)', re.MULTILINE)
RELAY_RE = re.compile(
    r'\[RelayCommand\][^\n]*\n[^\n]*?\b(\w+)\s*\(', re.MULTILINE)


def collect_members() -> set[str]:
    members: set[str] = set()
    for d in CS_DIRS:
        for cs in d.rglob("*.cs"):
            if any(part in {"bin", "obj", "publish"} for part in cs.parts):
                continue
            text = cs.read_text(encoding="utf-8", errors="ignore")
            for m in PROP_RE.finditer(text):
                members.add(m.group(1))
            for m in OBSERVABLE_RE.finditer(text):
                name = m.group(1).lstrip("_")
                members.add(name)
                members.add(name[0].upper() + name[1:])
            for m in RELAY_RE.finditer(text):
                name = m.group(1)
                members.add(name)
                # [RelayCommand] private Task FooAsync()  ->  生成 FooCommand
                base = name[:-5] if name.endswith("Async") else name
                members.add(base + "Command")
    return members


def collect_converters() -> set[str]:
    """收集所有可用的转换器注册名。

    XAML 里用 {StaticResource Xxx} 引用，Xxx 是 x:Key 别名而非类名，
    所以要同时扫描 C# 里的类名和各个 XAML 的 Page.Resources 里注册的 x:Key。
    """
    names: set[str] = set()
    for cs in (ROOT / "VoWin").rglob("*.cs"):
        if any(part in {"bin", "obj", "publish"} for part in cs.parts):
            continue
        text = cs.read_text(encoding="utf-8", errors="ignore")
        for m in re.finditer(r'class\s+(\w*Converter)\b', text):
            names.add(m.group(1))
    for xaml in (ROOT / "VoWin" / "Views").rglob("*.xaml"):
        text = xaml.read_text(encoding="utf-8", errors="ignore")
        for m in re.finditer(r'<[\w\.]+:(\w+)\s+x:Key="([^"]+)"', text):
            names.add(m.group(2))
    return names


def main() -> int:
    members = collect_members()
    converters = collect_converters()

    # 已知由框架/基类提供的成员，不参与告警
    known = {
        "Width", "Height", "Margin", "Padding", "Background", "Foreground", "BorderBrush",
        "BorderThickness", "Opacity", "ToolTip", "Tag", "Content", "Name", "IsEnabled",
        "Visibility", "HorizontalAlignment", "VerticalAlignment", "FontSize", "FontWeight",
        "TextTrimming", "Cursor", "Template", "Style", "Command", "CommandParameter",
        "ItemsSource", "SelectedItem", "SelectedValue", "Value", "Minimum", "Maximum",
        "Grid", "StackPanel", "Text", "TextBlock", "Run", "Source", "Target", "ElementName",
        "RelativeSource", "AncestorType", "DataContext", "Key", "Converter", "ConverterParameter",
        "FallbackValue", "StringFormat", "Mode", "UpdateSourceTrigger", "Delay", "Path",
        "Window", "User", "Profile", "ProfileName", "Inner", "ICCID", "State", "AnnotationText",
        "PhoneToCopy", "StatusText", "RetryText", "IsBusy", "HasPhone", "IsEnabledState",
        "IsCurrentlyAddressable", "Slots", "SelectedContentTemplate", "SelectedContentStringFormat",
        "SelectedDeviceTabIndex", "SelectedEuiccTarget", "Item", "Binding", "Parent",
    }

    dangling: dict[str, list[tuple[Path, int]]] = defaultdict(list)
    missing_converters: dict[str, list[tuple[Path, int]]] = defaultdict(list)
    total = 0

    for d in XAML_DIRS:
        for xaml in d.rglob("*.xaml"):
            lines = xaml.read_text(encoding="utf-8", errors="ignore").splitlines()
            for lineno, line in enumerate(lines, 1):
                for m in BINDING_RE.finditer(line):
                    raw = m.group(1).strip()
                    path = raw.split("=")[0].strip()
                    if not path or path.startswith("{"):
                        continue
                    total += 1
                    # 取最后一段作为成员名；Source/ElementName 绑定单独处理
                    parts = [p for p in re.split(r"[.\[\]]", path) if p]
                    if not parts:
                        continue
                    if parts[0] in {"Source", "ElementName", "RelativeSource", "TemplateBinding"}:
                        continue
                    leaf = parts[-1]
                    # 集合.Count / 索引器 由框架提供，不参与告警
                    if leaf in {"Count", "Length", "Item", "Item[]", "CurrentItem"}:
                        continue
                    if leaf in known or leaf in members:
                        continue
                    dangling[f"{path}  ->  {leaf}"].append((xaml.relative_to(ROOT), lineno))
                for m in CONVERTER_RE.finditer(line):
                    name = m.group(1)
                    if name not in converters:
                        missing_converters[name].append((xaml.relative_to(ROOT), lineno))

    print(f"扫描 XAML 绑定路径 {total} 条；C# 公开成员 {len(members)} 个；转换器 {len(converters)} 个\n")

    if missing_converters:
        print("=== 缺失的转换器 ===")
        for name, locs in sorted(missing_converters.items()):
            print(f"  [{name}]")
            for p, ln in locs[:5]:
                print(f"      {p}:{ln}")
        print()

    if dangling:
        print("=== 悬空绑定（叶子成员在 C# 中找不到）===")
        for path, locs in sorted(dangling.items(), key=lambda kv: -len(kv[1])):
            print(f"  {path}   x{len(locs)}")
            for p, ln in locs[:4]:
                print(f"      {p}:{ln}")
        print(f"\n共 {len(dangling)} 个悬空绑定路径")
        return 1

    print("未发现悬空绑定。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
