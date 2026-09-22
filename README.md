# 仓管员PDF查找器（自动更新发布仓）

本仓库用于发布 **仓管员PDF查找器** 的自动更新文件。

## 文件说明
| 文件 | 作用 |
|------|------|
| `仓管员PDF查找器.exe` | 最新版程序（客户端启动时拉取） |
| `version.json` | 版本清单 `{"version":"YYYY.MM.DD.SEQ","md5":"...","size":...}`，客户端比对用 |
| `README.md` | 本说明 |

## 发布流程（每次发新版）
1. 编译新的 `仓管员PDF查找器.exe` 替换仓库根目录同名文件。
2. 重新生成 `version.json`（md5 / size 必须与新 exe 一致）：
   ```
   certutil -hashfile "仓管员PDF查找器.exe" MD5
   ```
3. 提交并推送 `main` 分支：
   ```
   git add -A
   git commit -m "vYYYY.MM.DD.SEQ"
   git push origin main
   ```
4. 客户端启动会检查 `raw.githubusercontent.com/mmxn1218/TongLiPDF/main/version.json`，版本号更大则自动更新。

## 版本号
- 客户端 `PdfFinderGui.cs` 内 `APP_VERSION` 常量为唯一来源。
- 每次发布前先改该常量 → 再编译 → 生成 exe 与 version.json，三者保持一致。

## 查找逻辑
- 优先检索文件名以 `DL` 或 `report -` 开头的 PDF，并按日期（最后修改时间）最新优先。
- 命中即实时显示，不等待全部扫描完成。