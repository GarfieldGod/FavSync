#!/usr/bin/env bash
# 一键发布：自包含 · 单文件 · win-x64
#   产物目录 publish/FavSync-win-x64/  →  FavSync.exe + tools\{7z\, adb\}（共 6 个文件）
#   分发包   publish/FavSync-win-x64.zip（扁平结构，解压即可用）
#
# 为什么不是直接在 csproj 里写死 SelfContained：Server 是 Sdk.Web（OutputType=Exe），
# 自包含的可执行文件不能引用非自包含的可执行文件（NETSDK1150），
# 而 csproj 里的属性不会传给被引用的项目——只有命令行全局属性会。
set -euo pipefail
cd "$(dirname "$0")"

OUT="publish/FavSync-win-x64"
ZIP="publish/FavSync-win-x64.zip"

unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy ALL_PROXY all_proxy

echo "[1/3] 清理旧产物"
rm -rf "$OUT"
rm -f "$ZIP"

echo "[2/3] 发布（Release · win-x64 · self-contained · single-file）"
dotnet publish src/FavSync.Desktop/FavSync.Desktop.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "$OUT" -v q --nologo

echo "[3/3] 打包 zip（扁平结构：zip 根即 FavSync.exe）"
( cd "$OUT" && ../../tools/7z/7za.exe a -tzip -mx=5 -mm=Deflate "../../$ZIP" '*' -r > /dev/null )

echo
echo "完成："
echo "  目录  $OUT  ($(find "$OUT" -type f | wc -l) 个文件)"
find "$OUT" -type f | sort | sed 's/^/    /'
echo "  zip   $ZIP  ($(du -h "$ZIP" | cut -f1))"
