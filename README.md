# 仓管员PDF查找器（自动更新发布仓）

本仓库用于发布 **仓管员PDF查找器** 的自动更新文件。

## 文件说明
| 文件 | 作用 |
|------|------|
| `仓管员PDF查找器.exe` | 最新版程序（客户端启动时拉取） |
| `通力PDF查找器.exe` | 兼容旧文件名，内容必须与上面完全一致 |
| `version.json` | 版本清单 `{"version":"YYYY.MM.DD.SEQ","md5":"...","size":...}`，客户端比对用 |
| `README.md` | 本说明 |

## 客户端更新机制

程序启动时静默检查更新，不弹窗、不打断使用：

1. 依次请求 `version.json`（`raw.githubusercontent.com` 优先，`cdn.jsdelivr.net` 兜底）。
2. 按 `YYYY.MM.DD.SEQ` 逐段比较，远端不高于本地则直接返回。
3. 有新版本 → 下载程序文件 → 校验 **文件大小 + MD5**，任一不符立即丢弃。
4. 校验通过 → 程序把自己复制成 `_pdf_updater.exe`，带 `--apply-update` 参数启动它，随后主程序退出。
5. 更新器等主程序退出后，用 `MoveFile` 把旧程序挪成 `_pdf_old.bin`、新版本就位，再启动新程序。
   写入失败会自动把旧程序移回原位（回滚），不会留下打不开的程序。
6. 新程序启动时清理上一轮更新留下的残留。

全过程不经过任何 `.bat`，替换走宽字符文件 API，因此**程序放在含中文或空格的路径下也不会失败**。

### 更新过程产生的临时文件

| 文件 | 说明 |
|------|------|
| `_pdf_updater.exe` | 更新器本体，下一次启动时自动删除 |
| `_pdf_new.bin` | 下载好的新版本，替换完成后自动消失 |
| `_pdf_old.bin` | 旧版本备份，成功后被删除；失败时用于回滚 |
| `_pdf_update.log` | 更新日志（按系统 ANSI 追加，中文 Windows 即 GBK），排查更新问题先看它 |

## 发布流程

1. 修改 `PdfFinderGui.cs` 中的 `APP_VERSION`（唯一版本来源，格式 `YYYY.MM.DD.SEQ`）。
2. 双击 `build.bat`：编译 → 从源码读出版本号 → 计算 MD5 与大小 → 生成 `version.json` → 同步到本仓库目录。
3. 确认 `version.json` 与程序文件一致后提交推送：

   ```
   cd publish_repo
   git add -A
   git commit -m "vYYYY.MM.DD.SEQ"
   git push origin main
   ```

4. 各点位的程序下次启动会自动升级。

> `仓管员PDF查找器.exe` 与 `通力PDF查找器.exe` 必须始终是同一份内容，`build.bat` 会自动拷贝两份。

## 版本号

- 客户端 `PdfFinderGui.cs` 内 `APP_VERSION` 常量为唯一来源。
- 每次发布前先改该常量 → 再编译 → 生成 exe 与 version.json，三者保持一致。
- 若发现客户端版本号不涨，先看程序目录下的 `_pdf_update.log`。

## 查找逻辑

- 优先检索文件名以 `DL` 或 `report -` 开头的 PDF，并按日期（最后修改时间）最新优先。
- 命中即实时显示，不等待全部扫描完成。
