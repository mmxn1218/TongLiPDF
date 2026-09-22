# 仓管员PDF查找器（自动更新发布仓）

本仓库用于发布 **仓管员PDF查找器** 的自动更新文件。

## 文件说明

| 文件 | 作用 |
|------|------|
| `PDFFinder.dat` | **新版客户端（≥0009）的自动更新载荷**（内容就是 exe，只是改了扩展名，见下文） |
| `仓管员PDF查找器.exe` | 同一份内容，**给已装在客户机上的旧版用**（旧版只会问 raw 要这个 `.exe`），也可人工下载 |
| `通力PDF查找器.exe` | 兼容旧文件名，内容与上面完全一致 |
| `version.json` | 版本清单 `{"version":"YYYY.MM.DD.SEQ","md5":"...","size":...}`，客户端比对用 |
| `README.md` | 本说明 |

> 三个载荷必须是**同一份字节**，`build.bat` 会自动拷贝并校验落地后的 MD5。

## ⚠️ 为什么载荷要发两份名字（任何一个都不能删）

两个通道的脾气不一样，而 `version.json` 是共用的，所以**必须双名并存**：

| 通道 | `version.json` | `.exe` | `.dat` |
|------|:---:|:---:|:---:|
| `raw.githubusercontent.com` | ✅ | **✅ 能给** | ✅ |
| `fastly` / `gcore` / `testingcf.jsdelivr.net` | ✅ | **❌ 403** | ✅ |
| `cdn.jsdelivr.net` | ❌ 拒连 | — | — |

- **jsDelivr 对 `.exe` 一律返回 403**（实测三个镜像全 403），对 `.dat` 正常 200。
  与文件内容无关，是它的文件类型策略。
- raw 是纯文件服务，**不拦 `.exe`**，所以旧客户端能从 raw 拿到 `.exe`。

**结论：**
- 新客户端（≥0009）统一下载 **`PDFFinder.dat`** → 四个源都能给。
- 旧客户端（0007/0008）按老代码只会下载 **`仓管员PDF查找器.exe`** → 只有 raw 能给。
  **所以 `仓管员PDF查找器.exe` 绝不能从仓库里删掉**，它是老机器唯一的升版通道。

复现命令（同一仓库、同一时刻）：

```bash
curl -sI "https://raw.githubusercontent.com/mmxn1218/TongLiPDF/main/version.json"        # 200
curl -sI "https://fastly.jsdelivr.net/gh/mmxn1218/TongLiPDF@main/PDFFinder.dat"          # 200
curl -sI "https://fastly.jsdelivr.net/gh/mmxn1218/TongLiPDF@main/仓管员PDF查找器.exe"     # 403
curl -sI "https://cdn.jsdelivr.net/gh/mmxn1218/TongLiPDF@main/version.json"              # 连不上
```

## 更新源（顺序即优先级）

| 更新源 | 实测（2026-09-22 本机） | 说明 |
|--------|----------------------|------|
| `raw.githubusercontent.com` | **200 / 0.79s**，`.exe` 也给 | **唯一稳定可用**：最快最新，旧客户端也只认它 |
| `fastly.jsdelivr.net` | 200 / 1.17s，`.exe` 403 | 备用镜像（内容可能滞后） |
| `gcore.jsdelivr.net` | 200 → **后来 000 拒连** | 备用镜像，**时通时断**，见下 ② |
| `testingcf.jsdelivr.net` | 200 → **后来 000 拒连** | 备用镜像，**时通时断**，见下 ② |
| ~~`cdn.jsdelivr.net`~~ | **0.18s 连接被直接重置** | **已弃用**，国内最常被墙的节点 |

- **不要**把 `cdn.jsdelivr.net` 放回列表。
- **不要**删掉 `raw` —— 只有它既永远最新、又能给 `.exe`。
- 检测时按**所有源里版本号最高者**为准（**这条是关键保险**，理由见下）。
- HTTP 超时统一压到 **15 秒**（.NET 默认 100 秒，会把"打开软件后迟迟不更新"拖满 100 秒）。

### 两个别被误导的点（2026-09-22 实测）

**① `?v=<时间戳>` 破不了 jsDelivr 的边缘缓存。**
实测 fastly 上四个请求拿到的都是旧版本，加什么 query 都没用：

| 请求 | 返回 |
|------|------|
| `fastly.../version.json` | 0008（陈旧） |
| `fastly.../version.json?v=1` | 0008 |
| `fastly.../version.json?v=<随机>` | 0008 |
| `fastly.../gh/...@<commit-sha>/version.json` | **0009**（sha 形式才不可变） |
| `raw.../version.json` | **0009**（raw 一直是最新的） |

所以**不能靠破 query 缓存**。真正的保险是两条，缺一不可：
- **多源取最高版本**：只要有一个源是新的（raw 永远是），取 max 就得到正确结论；
- **下载后校验 size + MD5**：即使从某个源拿到陈旧载荷，也会被拒掉、自动换下一个源。

代码里仍带 `?v=`（无害，个别节点可能有效），但**不要把它当成保证**。

**② 镜像站是"时通时断"的，raw 必须放在第一位。**
同一天内测量结果会变：

| 时间 | fastly | gcore | testingcf |
|------|--------|-------|-----------|
| 16:22 | 200 | 200 | 200 |
| 16:44 | 200 | **000 拒连** | **000 拒连** |

所以不能假设"三个镜像总有一个能用"。**老客户端只认 raw，新客户端的第一源也是 raw** ——
这是唯一稳定可用的通道。

**③ 推送后 raw 有约 1 分钟的短缓存。**
推完立刻测，`version.json` 可能已经更新，而 `仓管员PDF查找器.exe` 还是旧的；
实测约 1 分钟后两者都刷新。客户端每次启动都会检查并校验，
**在缓存放开前它会"校验不通过→保留旧版本"，不会装坏包**，放开后自动升级成功。

> ⚠️ 排查更新源时踩过一个坑：测 `raw` 必须用它自己的路径格式
> `/mmxn1218/TongLiPDF/main/xxx`，**不能**套用 jsDelivr 的 `/gh/...@main` 格式。
> 用错格式会得到"超时/404"，从而**误判 raw 不通**（本人就是这么误判过一轮）。

## 客户端更新机制

程序启动时静默检查更新，不弹窗、不打断使用：

1. 逐个源请求 `version.json`，取版本号最高者；远端不高于本地则直接返回。
2. 有新版本 → 按序下载载荷 → 校验 **文件大小 + MD5**，任一不符立即丢弃。
3. 校验通过 → 程序把自己复制成 `_pdf_updater.exe`，带 `--apply-update <pid> "<exe路径>"` 启动它，随后主程序退出。
4. 更新器等主程序退出后，用 `MoveFile` 把旧程序挪成 `_pdf_old.bin`、新版本就位，再启动新程序。
   写入失败会自动把旧程序移回原位（**回滚**），不会留下打不开的程序。
5. 新程序启动时清理上一轮更新留下的残留。

全过程不经过任何 `.bat`，替换走宽字符文件 API，因此**程序放在含中文或空格的路径下也不会失败**。

### 更新过程产生的临时文件

| 文件 | 说明 |
|------|------|
| `_pdf_updater.exe` | 更新器本体，下一次启动时自动删除 |
| `_pdf_new.bin` | 下载好的新版本，替换完成后自动消失 |
| `_pdf_old.bin` | 旧版本备份，成功后被删除；失败时用于回滚 |
| `_pdf_update.log` | 更新日志（按系统 ANSI 追加，中文 Windows 即 GBK），排查更新问题**先看它** |

## 发布流程

1. 修改 `PdfFinderGui.cs` 中的 `APP_VERSION`（唯一版本来源，格式 `YYYY.MM.DD.SEQ`）。
2. 双击 `build.bat`（或 `D:\wbuddy\发布PDF查找器.bat`）。它会按 6 步走，**关键步不过就拒绝发布**：

   | 步骤 | 断言内容 |
   |------|----------|
   | 1/6 | 源码里必须仍有四个更新源与 `PDFFinder.dat`（防回归） |
   | 2/6 | 编译成功 |
   | 3/6 | 能从源码读出 `APP_VERSION` |
   | 4/6 | **编译出来的 exe 自报版本 == 源码版本**（防"声称新版、实为旧版"） |
   | 5/6 | 算出 MD5/大小并写 `version.json` |
   | 6/6 | 拷贝后**再校验一次落地文件的 MD5**，并刷新部署目录的两个文件名 |

3. 提交推送，**然后刷新 jsDelivr 缓存**：

   ```
   cd publish_repo
   git add -A
   git commit -m "vYYYY.MM.DD.SEQ"
   git push origin main

   curl -s "https://purge.jsdelivr.net/gh/mmxn1218/TongLiPDF@main/version.json"
   curl -s "https://purge.jsdelivr.net/gh/mmxn1218/TongLiPDF@main/PDFFinder.dat"
   ```

4. 验证远端确实是新版本（两个通道都要看）：

   ```
   curl -s "https://raw.githubusercontent.com/mmxn1218/TongLiPDF/main/version.json"
   curl -s "https://fastly.jsdelivr.net/gh/mmxn1218/TongLiPDF@main/version.json"
   ```

5. 已装机的客户端下次启动会自动升级，**无需人工再拷**。

## 版本号

- 客户端 `PdfFinderGui.cs` 内 `APP_VERSION` 常量为唯一来源。
- 每次发布前先改该常量 → 再编译 → 生成 exe 与 version.json，三者保持一致。
- 若发现客户端版本号不涨，先看程序目录下的 `_pdf_update.log`。
- **查某台机器上装的是哪一版**，不用打开界面：

  ```
  仓管员PDF查找器.exe --dump-version ver.txt     （ver.txt 里就是该二进制的真实版本号）
  ```

## 查找逻辑

- 优先检索文件名以 `DL` 或 `report -` 开头的 PDF，并按日期（最后修改时间）最新优先。
- 命中即实时显示，不等待全部扫描完成。
