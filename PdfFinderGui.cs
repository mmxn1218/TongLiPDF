using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
using System.ComponentModel;

namespace PdfFinder {
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            // 自检开关：--dump-version <输出文件>
            // 把本二进制"真实"的版本号写进文件后退出。供 build.bat 在编译后断言：
            //   编译出来的 exe 真的就是 version.json 声称的那一版。
            // （曾经踩过"对外声称 0008、发出去的二进制其实还是 0007"的坑，故加此自证。）
            // 必须是 Main 里第一件事，且不创建任何窗口。
            if (args != null && args.Length >= 2 && args[0] == "--dump-version") {
                try { System.IO.File.WriteAllText(args[1], MainForm.APP_VERSION, System.Text.Encoding.ASCII); } catch { }
                return;
            }
            // 更新器模式：本程序把自己复制成 _pdf_updater.exe 后，用本参数启动该副本。
            // 此时只做"等主进程退出 → 换文件 → 重启"，不创建任何窗口。
            if (args != null && args.Length >= 3 && args[0] == "--apply-update") {
                int pid;
                if (int.TryParse(args[1], out pid))
                    Updater.Run(pid, args[2]);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // 带超时的 WebClient。
    // 【2026-09-23 改】超时从 15 秒改回 60 秒，对齐 MES_每日执行 的 REQUEST_TIMEOUT_MS
    // （mes_report.h:77 就是 60000，那是现场 3 个点位长期跑下来的值）。
    // 之前压到 15 秒是我在没有实测前提下自己拍的：本机实测 raw 的 HTTPS 响应会在
    // 0.5s~40s 之间乱跳，15 秒会把一批"慢但活着"的响应直接掐掉，客户端随后回落到
    // jsDelivr 边缘节点（那边 version.json 是滞后的旧版本）→ 判成"已是最新" → 永远不更新。
    // 这正是"打开软件没反应"的症状来源。.NET 默认 100 秒太长（死源要干等），
    // MES 的 60 秒则是被现场验证过的折中值。
    // 配合 raw 命中即早退（源1成功就不再试后面），正常路径依然是一秒内出结果。
    class TimeoutWebClient : System.Net.WebClient {
        public int TimeoutMs = 60000;
        protected override System.Net.WebRequest GetWebRequest(Uri address) {
            var r = base.GetWebRequest(address);
            if (r != null) {
                r.Timeout = TimeoutMs;
                var h = r as System.Net.HttpWebRequest;
                if (h != null) h.ReadWriteTimeout = TimeoutMs;
            }
            return r;
        }
    }

    class MainForm : Form {
        private TextBox txtPath, txtNumber, txtResult;
        private Button btnBrowse, btnRun;
        private Label lblResult;
        private volatile bool s_cancel = false;   // 暂停/取消扫描标志

        public MainForm() {
            Text = "仓管员PDF查找器 v" + APP_VERSION;
            // 窗口标题栏/任务栏图标：从自身 exe 内嵌图标加载
            try { this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }
            Font = new System.Drawing.Font("Microsoft YaHei", 10F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(620, 460);
            FormBorderStyle = FormBorderStyle.FixedSingle;   // 固定窗口尺寸：不能拖边角缩放、不能双击标题栏最大化
            MaximizeBox = false;
            MinimizeBox = true;

            // 简介
            var intro = new TextBox {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = "【仓管员PDF查找器】\r\n\r\n使用步骤：\r\n1. 填写或浏览选择文件夹位置。\r\n2. 输入编号（如 W080300-104240）。\r\n3. 点击“执行”，下方实时列出所有正文含该编号的 PDF 路径。\r\n4. 双击结果中的某条路径，可直接打开该文件；若其所在文件夹当前没打开，会一并打开文件夹并选中该文件。",
                Location = new System.Drawing.Point(12, 12), Size = new System.Drawing.Size(596, 120),
                Font = new System.Drawing.Font("Microsoft YaHei", 10F)
            };
            var lbl1 = new Label { Text = "文件夹位置：", AutoSize = true, Location = new System.Drawing.Point(15, 150), Font = Font };
            var w1 = 380; var bx = 150;
            txtPath = new TextBox { Location = new System.Drawing.Point(bx, 147), Size = new System.Drawing.Size(w1, 24), Font = Font, Text = @"\\192.168.5.252\下载" };
            btnBrowse = new Button { Text = "浏览...", Location = new System.Drawing.Point(bx + w1 + 6, 146), Size = new System.Drawing.Size(80, 27), Font = Font };

            var lbl2 = new Label { Text = "输入编号：", AutoSize = true, Location = new System.Drawing.Point(15, 190), Font = Font };
            txtNumber = new TextBox { Location = new System.Drawing.Point(bx, 187), Size = new System.Drawing.Size(w1, 24), Font = Font };
            btnRun = new Button { Text = "执行", Location = new System.Drawing.Point(bx + w1 + 6, 186), Size = new System.Drawing.Size(80, 27), Font = Font };

            lblResult = new Label { Text = "查找结果：", AutoSize = true, Location = new System.Drawing.Point(15, 228), Font = Font };
            txtResult = new TextBox {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                AcceptsReturn = true, WordWrap = false,
                Location = new System.Drawing.Point(15, 250), Size = new System.Drawing.Size(590, 195),
                Font = new System.Drawing.Font("Microsoft YaHei", 10F)
            };

            Controls.AddRange(new Control[] { intro, lbl1, txtPath, btnBrowse, lbl2, txtNumber, btnRun, lblResult, txtResult });

            btnBrowse.Click += (s, e) => {
                using (var dlg = new FolderBrowserDialog()) {
                    dlg.Description = "选择文件夹位置";
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        txtPath.Text = dlg.SelectedPath;
                }
            };
            btnRun.Click += DoRun;
            // 双击结果里的某条文件地址 → 打开它。
            // 规则（潘工 2026-09-23）：双击直接打开该文件；若它所在文件夹当前没在资源管理器里打开，
            // 则一并打开该文件夹并在其中选中它；若该文件夹已经开着，就只打开文件、不重复开文件夹。
            txtResult.MouseDoubleClick += (s, e) => {
                if (e.Button != MouseButtons.Left) return;
                try {
                    int ci = txtResult.GetCharIndexFromPosition(e.Location);
                    int li = txtResult.GetLineFromCharIndex(ci);
                    string[] lines = txtResult.Lines;
                    if (li < 0 || li >= lines.Length) return;
                    string path = ExtractHitPath(lines[li]);
                    if (path == null) return;
                    OpenHit(path);
                } catch (System.Exception ex) {
                    MessageBox.Show("打开失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            this.Shown += (s, e) => {
                string ed = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                // 启动就留一条痕（对标 MES 的 upd_log）。现场"打开软件没反应"时，
                // 先看有没有这一行：没有 = 程序根本没跑起来；有 = 至少更新逻辑开始了。
                Updater.Log(ed, "--- 启动 PdfFinder v" + APP_VERSION + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                    + " 工作目录=" + ed);
                Updater.Cleanup(ed);
                CheckForUpdateAsync();
            };
        }

        // ================= 自动更新（对标 update.c 机制）=================
        public const string APP_VERSION = "2026.09.23.0013";   // 本地版本（YYYY.MM.DD.SEQ），唯一版本来源
        // 更新源（顺序即优先级），严格对齐 mes_每日执行 的 update.c / update.h。
        // 【2026-09-23】源列表与顺序一改之前"我自己挑的 4 个"，直接照抄 MES 的 5 个：
        //   raw → fastly → gcore → testingcf → cdn
        // cdn 本机实测在国内是 TLS 直连被重置（0.18s 就断），但 MES 现场跑通时它就是留着的，
        // 且排在最后——只在前面 4 个全挂时才会轮到，多花的时间只出现在"本来就已经失败"的路径上。
        // 原则：先跟成功案例完全一致，跑通了再谈精简。
        // 2026-09-22 本机实测（同一仓库、同一时刻，各源按自己正确的路径格式请求）：
        //   raw.githubusercontent.com        → 200 / 0.79s，**且 .exe 也能下发**（raw 是纯文件服务，不做类型拦截）
        //   fastly / gcore / testingcf .jsdelivr.net → 200 / 0.3~3s，但 **.exe 一律 403**，只认 .dat
        static readonly string[] UPDATE_BASE = new string[] {
            "https://raw.githubusercontent.com/mmxn1218/TongLiPDF/main",
            "https://fastly.jsdelivr.net/gh/mmxn1218/TongLiPDF@main",
            "https://gcore.jsdelivr.net/gh/mmxn1218/TongLiPDF@main",
            "https://testingcf.jsdelivr.net/gh/mmxn1218/TongLiPDF@main",
            "https://cdn.jsdelivr.net/gh/mmxn1218/TongLiPDF@main",
        };
        // 发布文件名必须用 .dat，不能是 .exe：
        // jsDelivr 对 .exe 一律返回 403（三个节点实测全 403），对 .dat 正常 200。
        // raw 两种都能给，所以本客户端对四个源统一只认 PDFFinder.dat。
        // 发布仓里**同时**保留一份同名 仓管员PDF查找器.exe（内容完全相同）——
        // 那是专门留给"已经装在客户机上的旧版"的：旧版按老代码只会问 raw 要 .exe，
        // 而 raw 不拦 .exe，于是它们照样能自动升上来，不必再人工拷机器。
        const string UPDATE_FILE = "PDFFinder.dat";
        // ===== 可选的外部更新源（2026-09-22 新增）=====
        // 程序同目录放一个 更新源.txt，里面【第一行有效内容】就是最高优先级的更新源。
        // 允许填内网共享（\\192.168.5.252\下载\_程序更新）或 URL。
        // 做成配置文件而不是写死：工厂网络里公网不稳时，换源只要改这一行，不用重新编译发版。
        const string SOURCE_CFG = "更新源.txt";
        // 共享里允许用这几种文件名，内容一致，方便直接把 exe 拷进去。
        // 网络源只认 UPDATE_FILE（jsDelivr 对 .exe 一律 403）。
        static readonly string[] PAYLOAD_LEAVES = new string[] {
            UPDATE_FILE, "仓管员PDF查找器.exe", "通力PDF查找器.exe"
        };

        // 本地源（UNC 共享 / 盘符路径）不能用 WebClient，要走 File.*
        static bool IsLocalSource(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("\\\\") || s.StartsWith("//")) return true;   // \\host\share
            if (s.StartsWith("\\")) return true;                           // \share
            if (s.Length >= 2 && s[1] == ':') return true;                 // D:\dir
            return false;
        }

        static string AppendLeaf(string bas, string leaf) {
            if (IsLocalSource(bas)) {
                if (bas.EndsWith("\\") || bas.EndsWith("/")) return bas + leaf;
                return bas + "\\" + leaf;
            }
            if (bas.EndsWith("/")) return bas + leaf;
            return bas + "/" + leaf;
        }

        // 读 更新源.txt 的第一行有效内容。
        // 编码不猜：先按 GBK(936) 解，解出来的本地路径若不存在，再按 UTF-8 解一遍 ——
        // 用"目录是否真的存在"来判定哪种解码是对的，比猜编码可靠。
        static string ReadSourceCfg(string cfg, out bool hadLine) {
            hadLine = false;
            byte[] b;
            try { b = System.IO.File.ReadAllBytes(cfg); } catch { return null; }
            if (b.Length == 0) return null;
            for (int pass = 0; pass < 2; pass++) {
                string txt;
                try {
                    var enc = System.Text.Encoding.GetEncoding(pass == 0 ? 936 : 65001);
                    int off = 0;
                    if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) off = 3;
                    txt = enc.GetString(b, off, b.Length - off);
                } catch { continue; }
                foreach (string raw in txt.Split('\n')) {
                    string s = raw.Trim();
                    if (s.Length == 0) continue;
                    if (s.StartsWith("#") || s.StartsWith(";")) continue;
                    hadLine = true;
                    if (s.StartsWith("http")) return s.TrimEnd('/');
                    if (IsLocalSource(s)) {
                        string d = s.TrimEnd('\\', '/');
                        if (System.IO.Directory.Exists(d)) return d;
                    }
                    break;   // 这一行解出来不可用 → 换下一种编码重试
                }
            }
            return null;
        }

        // 源列表 = 配置文件里的（最高优先）+ 内置的兜底源
        static string[] GetSources(string exeDir, out string cfgWarn) {
            cfgWarn = null;
            var list = new System.Collections.Generic.List<string>();
            try {
                string cfg = System.IO.Path.Combine(exeDir, SOURCE_CFG);
                if (System.IO.File.Exists(cfg)) {
                    bool hadLine;
                    string s = ReadSourceCfg(cfg, out hadLine);
                    if (s != null) list.Add(s);
                    // 整篇都是注释 = 没配置，不算问题；写了行却用不了才提醒
                    else if (hadLine) cfgWarn = "更新源.txt 里有内容但用不了（路径不存在？），已忽略并退回内置源";
                }
            } catch (System.Exception ex) { cfgWarn = "读 更新源.txt 失败：" + ex.Message; }
            foreach (string s in UPDATE_BASE) list.Add(s);
            return list.ToArray();
        }

        // 取文本：本地源用 File.ReadAllText(UTF-8)，网络源用 WebClient（带 ?v= 破缓存）
        static string FetchText(TimeoutWebClient wc, string bas, string leaf, string bust) {
            if (IsLocalSource(bas)) return System.IO.File.ReadAllText(AppendLeaf(bas, leaf), System.Text.Encoding.UTF8);
            return wc.DownloadString(AppendLeaf(bas, leaf) + bust);
        }

        // 下载载荷：本地源用 File.Copy，网络源用 WebClient。失败一律抛异常，由调用方统一 catch。
        static void FetchFile(TimeoutWebClient wc, string bas, string leaf, string bust, string dest) {
            if (IsLocalSource(bas)) {
                string src = AppendLeaf(bas, leaf);
                if (!System.IO.File.Exists(src)) throw new System.Exception("共享上没有 " + leaf);
                System.IO.File.Copy(src, dest, true);
                return;
            }
            wc.DownloadFile(AppendLeaf(bas, leaf) + bust, dest);
        }

        // 逐段比较 YYYY.MM.DD.SEQ，返回 -1/0/1
        private static int CmpVersion(string a, string b) {
            string[] pa = a.Split('.'), pb = b.Split('.');
            int n = System.Math.Min(pa.Length, pb.Length);
            for (int i = 0; i < n; i++) {
                int x = int.Parse(pa[i]), y = int.Parse(pb[i]);
                if (x != y) return x < y ? -1 : 1;
            }
            return pa.Length < pb.Length ? -1 : (pa.Length > pb.Length ? 1 : 0);
        }

        // 对标 update.c 的 is_md5_hex()：必须正好 32 位小写 hex，否则清单视为不可用。
        private static bool IsMd5Hex(string s) {
            if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
            foreach (char c in s) {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private void CheckForUpdateAsync() {
            string exeDir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            var t = new System.Threading.Thread(() => {
                try {
                    string cfgWarn;
                    string[] sources = GetSources(exeDir, out cfgWarn);
                    Status("查找结果：正在检查更新（" + sources.Length + " 个源）...");
                    // 逐个源取 version.json，取"所有源里版本号最高的那个"（对标 update.c 的 upd_check）。
                    // 为什么不"第一个成功就用"：jsDelivr 边缘节点可能回源滞后，
                    // 某个节点给出的还是旧版本号，只取一个会漏掉更新；取最大值最稳。
                    // 加 ?v=<秒级时间戳> 破 jsDelivr 边缘缓存（本地共享源会忽略这个参数）。
                    string bust = "?v=" + Epoch();
                    var wc = new TimeoutWebClient();
                    wc.Encoding = System.Text.Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "PdfFinder-Updater");
                    var errs = new System.Collections.Generic.List<string>();
                    string bestJson = null, bestVer = null;
                    // 【2026-09-23 对齐 MES】日志粒度提到 update.c 的水平：每个源单列一行，
                    // 带耗时与结论（采用/忽略/失败原因）。现场拿 _pdf_update.log 就能直接定位
                    // 卡在哪一层，不需要任何额外工具。MES 现场能诊断，靠的就是这个。
                    Updater.Log(exeDir, "===== 检查更新开始：本地=" + APP_VERSION + " 源数=" + sources.Length);
                    if (cfgWarn != null) Updater.Log(exeDir, "WARN " + cfgWarn);
                    for (int si = 0; si < sources.Length; si++) {
                        string baseUrl = sources[si];
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try {
                            string json = FetchText(wc, baseUrl, "version.json", bust);
                            sw.Stop();
                            var mv = System.Text.RegularExpressions.Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                            if (!mv.Success) {
                                Updater.Log(exeDir, "源" + (si + 1) + "/" + sources.Length + " " + baseUrl
                                    + " -> 清单里没有 version 字段（" + sw.ElapsedMilliseconds + "ms）");
                                errs.Add(baseUrl + " 清单里没有 version 字段");
                                continue;
                            }
                            string v = mv.Groups[1].Value;
                            // 【2026-09-23】这里补上 update.c 的形状校验：md5 必须 32 位 hex、
                            // size 必须 > 50000，否则这个源清单作废、跳过（MES 就是这么写的）。
                            // ⚠️ 捕获组下标务必用 Groups[1]：正则里只有 1 个组，
                            // 写成 Groups[2] 越界不会报错，但会返回空串，
                            // 后面的 long.Parse("") 才抛 FormatException —— 这正是
                            // 0009/0010 一直升不上去的根因（一走到"发现新版本"就断）。
                            var mvMd5 = System.Text.RegularExpressions.Regex.Match(json, "\"md5\"\\s*:\\s*\"([^\"]+)\"");
                            var mvSize = System.Text.RegularExpressions.Regex.Match(json, "\"size\"\\s*:\\s*(\\d+)");
                            string candMd5 = mvMd5.Success ? mvMd5.Groups[1].Value.Trim().ToLowerInvariant() : "";
                            long candSize = -1;
                            if (mvSize.Success) long.TryParse(mvSize.Groups[1].Value, out candSize);
                            if (!IsMd5Hex(candMd5) || candSize <= 50000) {
                                Updater.Log(exeDir, "源" + (si + 1) + "/" + sources.Length + " " + baseUrl
                                    + " -> 清单里 md5/size 不可用（md5=" + (candMd5.Length == 0 ? "(空)" : candMd5)
                                    + " size=" + candSize + "），跳过该源（" + sw.ElapsedMilliseconds + "ms）");
                                errs.Add(baseUrl + " 清单里 md5/size 不可用");
                                continue;
                            }
                            bool better = (bestVer == null || CmpVersion(v, bestVer) > 0);
                            Updater.Log(exeDir, "源" + (si + 1) + "/" + sources.Length + " " + baseUrl
                                + " -> 远端=" + v
                                + (better ? "（当前最大，采用）" : "（不高于已取到的 " + bestVer + "，忽略）")
                                + " " + sw.ElapsedMilliseconds + "ms");
                            if (better) { bestVer = v; bestJson = json; }
                        } catch (System.Exception ex) {
                            sw.Stop();
                            Updater.Log(exeDir, "源" + (si + 1) + "/" + sources.Length + " " + baseUrl
                                + " -> 取失败：" + ex.Message + "（" + sw.ElapsedMilliseconds + "ms）");
                            errs.Add(baseUrl + " -> " + ex.Message);
                        }
                    }
                    if (bestJson == null) {
                        // 【2026-09-22 修正】以前这里是静默 return：现场表现为"打开软件没反应、
                        // 日志里也没有任何记录"，排查时只能瞎猜。现在必须落日志 + 界面回一句。
                        Updater.Log(exeDir, "FAIL 检查更新结论：所有更新源都不可达，无法判断有无新版本");
                        Updater.Log(exeDir, "     逐源结果：" + string.Join(" | ", errs.ToArray()));
                        Status("查找结果：检查更新失败（不影响使用，详见 _pdf_update.log）");
                        return;
                    }
                    string remoteVer = bestVer;
                    if (CmpVersion(remoteVer, APP_VERSION) <= 0) {
                        Updater.Log(exeDir, "检查更新结论：已是最新（远端 " + remoteVer + "，本地 " + APP_VERSION + "）");
                        // 程序自己把结论说出来——"检查过了、网络正常、确实是最新的"，
                        // 这是现场唯一能自证"更新功能没坏"的方式（对标 MES 的 upd_check 返回值）。
                        Status("查找结果：已是最新 v" + APP_VERSION);
                        return;
                    }

                    // 循环里已对每个候选源校验过 md5/size 形状，bestJson 一定带可用值；
                    // 这里仍用 Groups[1] + TryParse 兜底 —— 解析问题在任何情况下都不允许把更新路径打断。
                    var mMd5 = System.Text.RegularExpressions.Regex.Match(bestJson, "\"md5\"\\s*:\\s*\"([^\"]+)\"");
                    var mSize = System.Text.RegularExpressions.Regex.Match(bestJson, "\"size\"\\s*:\\s*(\\d+)");
                    string remoteMd5 = mMd5.Success ? mMd5.Groups[1].Value.Trim().ToLowerInvariant() : "";
                    long remoteSize = -1;
                    if (mSize.Success) long.TryParse(mSize.Groups[1].Value, out remoteSize);
                    if (!IsMd5Hex(remoteMd5) || remoteSize <= 50000) {
                        Updater.Log(exeDir, "FAIL 远端清单不可用：md5=" + (remoteMd5.Length == 0 ? "(空)" : remoteMd5)
                            + " size=" + remoteSize + " —— 拒装，保留当前版本");
                        Status("查找结果：更新清单不可用，已保留当前版本");
                        return;
                    }
                    Updater.Log(exeDir, "检查更新结论：发现新版本 " + remoteVer + "（本地 " + APP_VERSION
                        + "）md5=" + remoteMd5 + " size=" + remoteSize + " → 开始下载");

                    // 静默自动更新：发现有新版直接下载覆盖重启，不弹窗（对标 update.c）
                    DownloadAndApply(remoteVer, remoteMd5, remoteSize);
                } catch (System.Exception ex) {
                    Updater.Log(exeDir, "FAIL 检查更新异常：" + ex.Message);
                    Status("查找结果：检查更新异常（详见 _pdf_update.log）");
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // 秒级 Unix 时间戳，仅用于破缓存
        private static long Epoch() {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private void DownloadAndApply(string ver, string md5, long size) {
            this.BeginInvoke((Action)(() => {
                txtResult.AppendText("\r\n发现新版本 " + ver + "，正在自动更新...\r\n");
                lblResult.Text = "正在更新...";
            }));
            string exePath = Application.ExecutablePath;                       // 当前 exe 完整路径
            string exeDir = System.IO.Path.GetDirectoryName(exePath);
            System.Threading.Thread t = new System.Threading.Thread(() => {
                // 1) 下载 + 逐源校验。
                // 【2026-09-23 对齐 MES update.c】改成"下载后立刻算 MD5/size，不符就换下一个源"。
                // 原来我是"下载成功→整批校验一次→不过就放弃"，比 MES 弱一档：
                // 只要第一个源（哪怕它只是个缓存错位的镜像）把包下下来了，后面 4 个好源就再也轮不到。
                // MES 的注释原话是"该源缓存错位，换源"——这是现场验证过的做法，照抄。
                // 下载到临时目录（失败不留垃圾在程序目录）
                string tmpNew = System.IO.Path.Combine(System.IO.Path.GetTempPath(), UPDATE_FILE);
                bool ok = false;
                string lastErr = "";
                string bust2 = "?v=" + Epoch();
                string cfgWarn2;
                string[] srcs = GetSources(exeDir, out cfgWarn2);
                Updater.Log(exeDir, "===== 开始下载 " + ver + " 期望 size=" + size + " md5=" + md5);
                for (int si = 0; si < srcs.Length && !ok; si++) {
                    string baseUrl = srcs[si];
                    // 本地共享允许用几种文件名（方便直接把 exe 拷进去）；网络源只认 UPDATE_FILE
                    string[] leaves = IsLocalSource(baseUrl) ? PAYLOAD_LEAVES : new string[] { UPDATE_FILE };
                    foreach (string leaf in leaves) {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try {
                            var wc = new TimeoutWebClient();
                            wc.Headers.Add("User-Agent", "PdfFinder-Updater");
                            FetchFile(wc, baseUrl, leaf, bust2, tmpNew);
                        } catch (System.Exception ex) {
                            sw.Stop();
                            Updater.Log(exeDir, "下载 源" + (si + 1) + "/" + srcs.Length + " " + baseUrl + " / " + leaf
                                + " -> 失败：" + ex.Message + "（" + sw.ElapsedMilliseconds + "ms）");
                            lastErr = baseUrl + " / " + leaf + " -> " + ex.Message;
                            continue;
                        }
                        sw.Stop();
                        long got = -1;
                        try { got = new System.IO.FileInfo(tmpNew).Length; } catch { }
                        string gotMd5 = "";
                        try {
                            using (var fs = System.IO.File.OpenRead(tmpNew))
                            using (var mc = System.Security.Cryptography.MD5.Create())
                                gotMd5 = Md5Hex(fs, mc);
                        } catch (System.Exception ex) {
                            Updater.Log(exeDir, "下载 源" + (si + 1) + " " + baseUrl + " -> 实得 " + got
                                + " 字节，但算 MD5 失败：" + ex.Message + "（本地文件系统问题，换源无用）");
                            lastErr = baseUrl + " -> 算 MD5 失败";
                            continue;
                        }
                        Updater.Log(exeDir, "下载 源" + (si + 1) + "/" + srcs.Length + " " + baseUrl + " / " + leaf
                            + " -> " + got + " 字节 md5=" + gotMd5 + "（" + sw.ElapsedMilliseconds + "ms）");
                        if (size >= 0 && got != size) {
                            Updater.Log(exeDir, "     校验不通过：大小不符（实得 " + got + "，期望 " + size
                                + "）→ 该源缓存错位，换下一个源");
                            lastErr = baseUrl + " 大小不符（实得 " + got + "）";
                            continue;
                        }
                        if (md5.Length > 0 && !gotMd5.Equals(md5, System.StringComparison.OrdinalIgnoreCase)) {
                            Updater.Log(exeDir, "     校验不通过：MD5 不符（实得 " + gotMd5 + "，期望 " + md5
                                + "）→ 该源缓存错位，换下一个源");
                            lastErr = baseUrl + " MD5 不符";
                            continue;
                        }
                        Updater.Log(exeDir, "     校验通过：源" + (si + 1) + " " + baseUrl
                            + " size=" + got + " md5=" + gotMd5);
                        ok = true;
                        break;
                    }
                }
                if (!ok) {
                    Updater.Log(exeDir, "FAIL 下载失败：所有更新源都没能拿到通过校验的包（最后错误：" + lastErr + "）");
                    try { System.IO.File.Delete(tmpNew); } catch { }
                    Notify("下载失败，已保留当前版本");
                    return;
                }
                // 2) 落到程序目录（与目标同盘，替换才是原子的）
                string newp = System.IO.Path.Combine(exeDir, Updater.NEW_LEAF);
                string updater = System.IO.Path.Combine(exeDir, Updater.UPD_LEAF);
                try {
                    if (System.IO.File.Exists(newp)) System.IO.File.Delete(newp);
                    System.IO.File.Copy(tmpNew, newp, true);
                    Updater.Log(exeDir, "已落盘到程序目录 " + Updater.NEW_LEAF);
                } catch (System.Exception ex) {
                    Updater.Log(exeDir, "FAIL 无法写入新版本到程序目录：" + ex.Message);
                    Notify("程序目录不可写，更新中止");
                    return;
                } finally {
                    try { System.IO.File.Delete(tmpNew); } catch { }
                }

                // 3) 自我复制成更新器
                try {
                    if (System.IO.File.Exists(updater)) System.IO.File.Delete(updater);
                    System.IO.File.Copy(exePath, updater, true);
                    Updater.Log(exeDir, "已生成更新器 " + Updater.UPD_LEAF);
                } catch (System.Exception ex) {
                    Updater.Log(exeDir, "FAIL 无法生成更新器：" + ex.Message);
                    Notify("无法生成更新器，更新中止");
                    return;
                }
                Updater.Log(exeDir, "已下载并校验 " + ver + "，交给更新器接管");
                // 4) 启动更新器 → 立刻退出自身，把文件锁让出来
                this.BeginInvoke((Action)(() => {
                    txtResult.AppendText("更新包校验通过，正在重启替换...\r\n");
                    lblResult.Text = "正在更新...";
                    try {
                        var psi = new System.Diagnostics.ProcessStartInfo(updater,
                            "--apply-update " + System.Diagnostics.Process.GetCurrentProcess().Id + " \"" + exePath + "\"");
                        psi.WorkingDirectory = exeDir;
                        psi.UseShellExecute = false;
                        System.Diagnostics.Process.Start(psi);
                    } catch (System.Exception ex) {
                        Updater.Log(exeDir, "FAIL 启动更新器失败：" + ex.Message);
                        Notify("启动更新器失败，更新中止");
                        return;
                    }
                    Environment.Exit(0);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        // 在线程里安全地回一句提示（不弹窗、不打断使用）
        private void Notify(string msg) {
            try {
                this.BeginInvoke((Action)(() => {
                    txtResult.AppendText("[自动更新] " + msg + "\r\n");
                }));
            } catch { }
        }

        // 更新状态栏那一行。没有它的后果：用户分不清"在检查"和"已经死了"——
        // 2026-09-22 现场就因为这个白等了一分钟。正在扫描时不抢这行，避免打断使用。
        private void Status(string msg) {
            try {
                this.BeginInvoke((Action)(() => {
                    if (s_scanning) return;
                    lblResult.Text = msg;
                }));
            } catch { }
        }

        private static string Md5Hex(System.IO.Stream s, System.Security.Cryptography.MD5 md5) {
            byte[] h = md5.ComputeHash(s);
            var sb = new System.Text.StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private volatile bool s_scanning = false;  // 是否正在扫描
        private void DoRun(object sender, EventArgs e) {
            // 若正在扫描，此时按的是"暂停"
            if (s_scanning) {
                s_cancel = true;
                btnRun.Text = "执行";
                btnRun.Enabled = false;   // 等待线程收尾再恢复
                txtResult.AppendText("\r\n正在暂停...（本次结果保留）\r\n");
                lblResult.Text = "查找结果：正在暂停...";
                return;
            }
            string dir = txtPath.Text.Trim();
            string needle = txtNumber.Text.Trim();
            if (dir.Length == 0 || needle.Length == 0) {
                MessageBox.Show("请先填写文件夹位置和编号！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            btnRun.Enabled = false;
            btnBrowse.Enabled = false;
            s_cancel = false;
            s_scanning = true;
            txtResult.Text = "正在读取文件列表并扫描，请稍候...\r\n（最新优先，命中即实时显示）\r\n";
            lblResult.Text = "查找结果：正在读取 PDF 文件列表...";
            // 2 秒后执行按钮变为"暂停"（可点击取消扫描）
            var pauseTimer = new System.Windows.Forms.Timer();
            pauseTimer.Interval = 2000;
            pauseTimer.Tick += (s3, e3) => {
                pauseTimer.Stop();
                if (s_scanning) { btnRun.Text = "暂停"; btnRun.Enabled = true; }
            };
            pauseTimer.Start();

            var bw = new BackgroundWorker();
            bw.WorkerReportsProgress = true;
            bw.DoWork += (s2, e2) => {
                System.IO.FileSystemInfo[] ent;
                var di = new System.IO.DirectoryInfo(dir);
                if (di.Exists) {
                    // 一次性枚举，LastWriteTime 已在枚举时缓存，避免逐个文件二次网络 stat
                    ent = di.GetFiles("*.pdf");
                } else {
                    ent = new System.IO.FileInfo[0];
                }
                // 优先级(rank 越小越靠前): 1=report(无短横) 2=report - 3=DL 其余=9
                // 同档内按最后修改时间倒序(最新在前)。潘工要求: report > report - > DL 三种命名都可能。
                var list = new System.Collections.Generic.List<System.IO.FileInfo>();
                foreach (var f in ent) if (f is System.IO.FileInfo) list.Add((System.IO.FileInfo)f);
                list.Sort((a, b) => {
                    int ra = NameRank(a.Name), rb = NameRank(b.Name);
                    if (ra != rb) return ra.CompareTo(rb);   // 低 rank 在前
                    return b.LastWriteTime.CompareTo(a.LastWriteTime); // 同档:日期倒序(最新在前)
                });
                bw.ReportProgress(0, "COUNT:" + list.Count);
                int total = 0, hits = 0;
                foreach (var f in list) {
                    if (s_cancel) break;   // 用户点了暂停
                    total++;
                    try {
                        byte[] data = System.IO.File.ReadAllBytes(f.FullName);
                        string text = ExtractText(data);
                        if (text != null && text.Contains(needle)) {
                            hits++;
                            // 命中一个立即输出，不等全部扫完；附创建/修改时间便于辨认被改过或重命名的文件
                            bw.ReportProgress(0, "MATCH:" + FileLine(f));
                        }
                    } catch { }
                    if (total % 10 == 0)
                        bw.ReportProgress(0, "SCAN:" + total + "/" + list.Count);
                }
                bw.ReportProgress(0, "DONE:" + hits + ":" + total + ":" + needle);
                e2.Result = null;
            };
            bw.ProgressChanged += (s2, e2) => {
                string msg = (string)e2.UserState;
                if (msg.StartsWith("MATCH:")) {
                    txtResult.AppendText(msg.Substring(6) + "\r\n");
                } else if (msg.StartsWith("COUNT:")) {
                    txtResult.AppendText("找到 " + msg.Substring(6) + " 个 PDF，开始扫描...\r\n");
                    lblResult.Text = "查找结果：正在扫描 " + msg.Substring(6) + " 个 PDF...";
                } else if (msg.StartsWith("SCAN:")) {
                    lblResult.Text = "查找结果：正在扫描 " + msg.Substring(5) + " ...";
                } else if (msg.StartsWith("DONE:")) {
                    var parts = msg.Substring(5).Split(':');
                    int hits = int.Parse(parts[0]), total = int.Parse(parts[1]);
                    string needle2 = parts.Length > 2 ? parts[2] : "";
                    if (s_cancel) {
                        txtResult.AppendText("\r\n已暂停，扫描了 " + total + " 个 PDF，命中 " + hits + " 个。（可重新点执行继续新一轮）\r\n");
                    } else {
                        if (hits == 0) {
                            txtResult.AppendText("\r\n未找到正文包含编号 \"" + needle2 + "\" 的 PDF 文件。");
                        }
                        txtResult.AppendText("\r\n扫描完成，共 " + total + " 个 PDF，命中 " + hits + " 个。" + (hits == 0 ? "" : "（按最新优先，已实时列出）") + "\r\n");
                    }
                    lblResult.Text = "查找结果：";
                }
            };
            bw.RunWorkerCompleted += (s2, e2) => {
                s_scanning = false;          // 扫描结束（含暂停）
                btnRun.Text = "执行";
                btnRun.Enabled = true;
                btnBrowse.Enabled = true;
                lblResult.Text = "查找结果：";
                if (e2.Error != null) {
                    MessageBox.Show("执行出错：" + e2.Error.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    txtResult.Text = "";
                }
            };
            bw.RunWorkerAsync();
        }

        // 文件名优先级 rank: 1=report(无短横) 2=report - 3=DL 9=其余。
        // 潘工要求三种命名都可能(report / report - / DL)，优先顺序 report > report - > DL。
        private static int NameRank(string path) {
            string n = System.IO.Path.GetFileName(path);
            if (n.StartsWith("report -", System.StringComparison.OrdinalIgnoreCase)) return 2;
            if (n.StartsWith("report",   System.StringComparison.OrdinalIgnoreCase)) return 1;
            if (n.StartsWith("DL",       System.StringComparison.OrdinalIgnoreCase)) return 3;
            return 9;
        }

        // 命中结果行：路径 + 创建时间 + 修改时间。
        // 文件会被修改/重命名，把修改时间带上，仓管员一眼能看出哪个是最新的。
        private static string FileLine(System.IO.FileInfo f) {
            return f.FullName
                + "  - 创建时间:" + FmtTime(f.CreationTime)
                + "  修改时间:" + FmtTime(f.LastWriteTime);
        }
        private static string FmtTime(DateTime dt) {
            return dt.ToString("yyyy年M月d日 HH:mm:ss");
        }

        // 从一行结果里取出文件路径：该行形如
        //   \\host\share\xxx.pdf  - 创建时间:2026年9月22日 16:03:48  修改时间:2026年9月22日 16:03:49
        // 取 "  - 创建时间:" 之前的整段（即 FullName）。不是命中行的（状态/统计文字）返回 null。
        private static string ExtractHitPath(string line) {
            if (string.IsNullOrEmpty(line)) return null;
            string p;
            int k = line.IndexOf("  - 创建时间:", System.StringComparison.Ordinal);
            if (k > 0) p = line.Substring(0, k).Trim();
            else p = line.Trim();
            if (p.Length == 0) return null;
            if (!p.EndsWith(".pdf", System.StringComparison.OrdinalIgnoreCase)) return null;
            if (p.IndexOf('\\') < 0 && p.IndexOf('/') < 0) return null;   // 必须是带分隔符的路径
            return p;
        }

        // 双击某个命中文件：始终打开该文件本身；其所在文件夹若当前没开着，则一并打开（并选中该文件）。
        private static void OpenHit(string file) {
            if (!System.IO.File.Exists(file)) {
                MessageBox.Show("文件已不存在（可能已被重命名或移动）：\r\n" + file,
                    "打不开", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string folder = null;
            try { folder = System.IO.Path.GetDirectoryName(file); } catch { }
            if (!string.IsNullOrEmpty(folder) && !IsFolderWindowOpen(folder)) {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); }
                catch { }
            }
            try {
                var psi = new System.Diagnostics.ProcessStartInfo(file);
                psi.UseShellExecute = true;    // 交给系统按 .pdf 关联程序打开
                System.Diagnostics.Process.Start(psi);
            } catch (System.Exception ex) {
                MessageBox.Show("无法打开文件：" + ex.Message + "\r\n" + file,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 判断某文件夹是否已在资源管理器里打开：用 Shell.Application 枚举已开窗口的路径比对。
        // 用晚绑定反射（InvokeMember）实现，避免引入 interop 程序集、也不依赖 dynamic 编译开关。
        private static bool IsFolderWindowOpen(string folder) {
            try {
                string target = System.IO.Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant();
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return false;
                object shell = Activator.CreateInstance(t);
                object windows = t.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return false;
                Type wt = windows.GetType();
                int count = (int)wt.InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null);
                for (int i = 0; i < count; i++) {
                    try {
                        object win = wt.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                        if (win == null) continue;
                        object doc = win.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, win, null);
                        if (doc == null) continue;
                        object fol = doc.GetType().InvokeMember("Folder", System.Reflection.BindingFlags.GetProperty, null, doc, null);
                        if (fol == null) continue;
                        object self = fol.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, fol, null);
                        if (self == null) continue;
                        string p = self.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, self, null) as string;
                        if (string.IsNullOrEmpty(p)) continue;
                        if (System.IO.Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant() == target) return true;
                    } catch { }
                }
            } catch { }
            return false;
        }

        // 提取 PDF 中所有 FlateDecode 流解压后的文本（Latin1），用于编号检索
        private static string ExtractText(byte[] data) {
            var sb = new StringBuilder();
            int i = 0;
            var sstr = Encoding.ASCII.GetBytes("stream");
            var estr = Encoding.ASCII.GetBytes("endstream");
            while (i + 6 <= data.Length) {
                int si = IndexOf(data, sstr, i);
                if (si < 0) break;
                int ds = si + 6;
                while (ds < data.Length && (data[ds] == (byte)'\r' || data[ds] == (byte)'\n')) ds++;
                int ei = IndexOf(data, estr, ds);
                if (ei < 0) break;
                int dataEnd = ei;
                while (dataEnd > ds && (data[dataEnd-1] == (byte)'\r' || data[dataEnd-1] == (byte)'\n')) dataEnd--;
                int len = dataEnd - ds;
                i = ei + 9;
                if (len < 2) continue;
                var payload = new byte[len];
                Array.Copy(data, ds, payload, 0, len);
                try {
                    using (var ms = new MemoryStream(payload, 2, payload.Length - 2, false))
                    using (var ds2 = new DeflateStream(ms, CompressionMode.Decompress)) {
                        var buf = new byte[256 * 1024];
                        int n;
                        while ((n = ds2.Read(buf, 0, buf.Length)) > 0)
                            sb.Append(Encoding.GetEncoding(28591).GetString(buf, 0, n));
                    }
                } catch { }
            }
            return sb.ToString();
        }

        private static int IndexOf(byte[] data, byte[] pat, int start) {
            for (int i = start; i <= data.Length - pat.Length; i++) {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++)
                    if (data[i+j] != pat[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }

    // ================= 更新器（对标 update.c：自我复制 + MoveFile 替换 + 回滚）=================
    // 由主程序把自己复制成 _pdf_updater.exe 后带 --apply-update 启动。
    // 全流程宽字符（.NET string → CreateProcessW / File.MoveW），不经过任何 bat，
    // 因此中文路径、空格路径都不会踩代码页的坑。
    static class Updater {
        public const string NEW_LEAF = "_pdf_new.bin";       // 下载好的新版本
        public const string OLD_LEAF = "_pdf_old.bin";       // 旧版本备份（回滚用）
        public const string UPD_LEAF = "_pdf_updater.exe";   // 自我复制出来的更新器
        public const string LOG_LEAF = "_pdf_update.log";    // 更新日志

        // 更新器主流程：等旧进程退出 → 旧 exe 挪走 → 新版本就位 → 启动 → 清理
        public static void Run(int pid, string target) {
            string dir = System.IO.Path.GetDirectoryName(target);
            string newp = System.IO.Path.Combine(dir, NEW_LEAF);
            string oldp = System.IO.Path.Combine(dir, OLD_LEAF);

            Log(dir, "更新器接管：等待主进程 pid=" + pid + " 退出");
            if (!System.IO.File.Exists(newp)) {
                Log(dir, "FAIL 找不到新版本文件 " + NEW_LEAF);
                return;
            }

            WaitPid(pid);

            try { if (System.IO.File.Exists(oldp)) System.IO.File.Delete(oldp); } catch { }

            // 刚退出的进程有时还占着文件，重试 50 × 200ms
            bool moved = false;
            for (int i = 0; i < 50; i++) {
                try { System.IO.File.Move(target, oldp); moved = true; break; }
                catch { System.Threading.Thread.Sleep(200); }
            }
            if (!moved) {
                Log(dir, "FAIL 旧程序仍占用文件，替换中止（请手动关掉程序后重新打开）");
                return;
            }

            try {
                System.IO.File.Move(newp, target);
            } catch (System.Exception ex) {
                try { System.IO.File.Move(oldp, target); } catch { }   // 回滚
                Log(dir, "FAIL 写入新版本失败：" + ex.Message + "（已回滚到旧版本）");
                return;
            }

            try {
                var psi = new System.Diagnostics.ProcessStartInfo(target);
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            } catch (System.Exception ex) {
                Log(dir, "WARN 新版本启动失败：" + ex.Message + "，请手动打开程序");
            }

            Log(dir, "OK 更新完成");
            try { System.IO.File.Delete(oldp); } catch { }
            // 更新器自己是运行中的 exe，删不掉自己；留给新版本启动时 Cleanup 清掉
        }

        // 等指定进程退出（最多 30 秒）
        private static void WaitPid(int pid) {
            try {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                    p.WaitForExit(30000);
            } catch { }
        }

        // 主程序启动时清理上一轮更新留下的残留（更新器可能还在退出过程中，所以重试几次）
        public static void Cleanup(string dir) {
            if (string.IsNullOrEmpty(dir)) return;
            var t = new System.Threading.Thread(() => {
                for (int i = 0; i < 10; i++) {
                    bool all = true;
                    foreach (string leaf in new string[] { OLD_LEAF, UPD_LEAF }) {
                        string p = System.IO.Path.Combine(dir, leaf);
                        try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
                        catch { all = false; }
                    }
                    if (all) break;
                    System.Threading.Thread.Sleep(500);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // 日志一律用系统 ANSI（中文 Windows 即 GBK）追加，记事本可直接打开。
        // 【2026-09-23 对齐 MES】程序目录写不进去时（例如程序装在 Program Files，
        // 普通仓管员账号无写权限）自动退到 %TEMP% —— MES 就是写 %TEMP%\mes_update.log。
        // 原来的实现只写程序目录，写失败被 catch 吞掉：日志这一层本身就成了盲区，
        // 而"没日志"和"没检查"在现场长得一模一样，这正是排查被卡住的原因。
        public static void Log(string dir, string msg) {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n";
            foreach (string d in new string[] { dir, System.IO.Path.GetTempPath() }) {
                if (string.IsNullOrEmpty(d)) continue;
                try {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(d, LOG_LEAF), line, System.Text.Encoding.Default);
                    return;
                } catch { }
            }
        }
    }
}
