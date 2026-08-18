using System;
using System.IO;
using System.Text;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(MOD_dbgAgent.ModMain), "DebugAgent", "1.0.0", "ZCode")]

namespace MOD_dbgAgent
{
    /// <summary>
    /// 实时调试代理：轮询 游戏目录\DebugAgent\cmd.txt（外部写入，读后清空），
    /// 主线程执行查询命令，结果追加写 out.txt 并打日志。查询只读，不改任何游戏数据。
    /// 命令：
    ///   find 关键字        按名字搜单位（输出 unitID|名字|性别|年龄|配偶|孩子数）
    ///   unit 单位ID        单位详情
    ///   rel 单位ID         关系数据：配偶/孩子列表（每个孩子的年龄与parent数组）
    ///   log 单位ID [条数]  最近N条经历日志（月份+id+参数），默认10
    ///   obj 单位ID         objData 全部命名空间与键
    ///   count              allUnit/allUnits 容量统计
    ///   month N [auto]     时间快进：连续推进N个月（每30天一轮，等上轮结算完成再推下一轮）
    ///   stop               停止快进
    /// </summary>
    public class ModMain : MelonMod
    {
        private string _cmdPath;
        private string _outPath;
        private int _frame = 0;
        public static bool CfgFast = true;   // 快进状态机+快捷键
        public static bool CfgCmd = true;    // 命令轮询
        private int _cfgTick = 0;
        private string _cfgPath = null;

        private void LoadCfg()
        {
            try
            {
                if (_cfgPath == null)
                {
                    string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent");
                    _cfgPath = Path.Combine(dir, "config.txt");
                }
                if (!File.Exists(_cfgPath)) return;
                foreach (string line in File.ReadAllLines(_cfgPath, Encoding.UTF8))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).Trim().ToLower();
                    string v = t.Substring(eq + 1).Trim();
                    if (k == "fast") CfgFast = v == "1";
                    else if (k == "cmd") CfgCmd = v == "1";
                }
            }
            catch { }
        }

        private int _pendingMonths = 0;
        private bool _autoMode = false;
        private int _uiCloseCd = 0;
        private int _uiClosedCount = 0;
        internal static MapWorldMgr _mapWorld = null; // Harmony hook构造函数捕获的实例

        public override void OnApplicationLateStart()
        {
            string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent");
            Directory.CreateDirectory(dir);
            _cmdPath = Path.Combine(dir, "cmd.txt");
            _outPath = Path.Combine(dir, "out.txt");
            try { File.WriteAllText(_cmdPath, ""); } catch { }
            try { File.WriteAllText(_outPath, "== DebugAgent 输出 ==\r\n"); } catch { }
            MelonLogger.Msg("DebugAgent 已启动：命令写入 DebugAgent\\cmd.txt，每0.5秒轮询一次");
            WireDiag();
            try
            {
                var h = new HarmonyLib.Harmony("MOD_dbgAgent");
                // 诊断：CtorPatch(MapWorldMgr构造) 暂不注册——已确认它是进档卡死元凶(Il2Cpp构造函数hook触发EventsMgr::Init NRE)
                // var ctor = HarmonyLib.AccessTools.Constructor(typeof(MapWorldMgr));
                // var post = new HarmonyLib.HarmonyMethod(typeof(CtorPatch).GetMethod("Post"));
                // h.Patch(ctor, postfix: post);
                // CustomLogPatch 已排除嫌疑（卡死元凶是CtorPatch），恢复注册：id=999999 自由叙事直出
                var ml = HarmonyLib.AccessTools.Method(typeof(DataUnitLog.LogData.Data), "GetLogString");
                var cp = new HarmonyLib.HarmonyMethod(typeof(CustomLogPatch).GetMethod("Prefix"));
                h.Patch(ml, prefix: cp);
                MelonLogger.Msg("DebugAgent: Harmony钩子已挂(GetLogString自定义叙事；CtorPatch保持禁用)");
            }
            catch (Exception pe) { MelonLogger.Msg("DebugAgent钩子失败: " + pe.Message); }
        }

        public override void OnUpdate()
        {
            _cfgTick++;
            if (_cfgTick >= 300) { _cfgTick = 0; LoadCfg(); TryAutoExport(); }
            if (!CfgFast) return; // 快进/快捷键已停用（config: fast=0）
            // 快捷键：F6=快进1月 F7=快进1年 F8=快进10年 F10=停止（可累加，如F6按3次=3月）
            try
            {
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F6)) StartFF(1);
                else if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F7)) StartFF(12);
                else if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F8)) StartFF(120);
                else if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F10))
                {
                    if (_pendingMonths > 0) { _pendingMonths = 0; Out("[快进] 已停止(F10)"); }
                }
            }
            catch { }
            // 快进状态机：每帧检查，等上轮结算完成后推下一月
            try
            {
                if (_pendingMonths > 0)
                {
                    var run = g.world.run;
                    if (run != null && !run.isRunning && !run.oneRuning)
                    {
                        if (run.IsCanRun())
                        {
                            bool ok = run.AddDay(30, _autoMode);
                            _pendingMonths--;
                            Out("[快进] 推进1月 剩余" + _pendingMonths + " AddDay=" + ok);
                            if (!ok)
                            {
                                Out("[快进] AddDay返回false，已暂停（手动处理后再按热键继续）");
                                _pendingMonths = 0;
                            }
                        }
                        else
                        {
                            // 结算空闲但不能推进 = 被UI挡住：每0.5秒自动关一次UI
                            _uiCloseCd--;
                            if (_uiCloseCd <= 0)
                            {
                                _uiCloseCd = 30;
                                try
                                {
                                    var empty = new UnhollowerBaseLib.Il2CppReferenceArray<UIType.UITypeBase>(0);
                                    if (_mapWorld != null) _mapWorld.CloseAllUI(true, empty);
                                    _uiClosedCount++;
                                    if (_uiClosedCount % 10 == 1) Out("[快进] 自动关UI中(第" + _uiClosedCount + "次)，剩余" + _pendingMonths + "月");
                                }
                                catch (Exception ce)
                                {
                                    Out("[快进] CloseAllUI失败: " + ce.Message);
                                    _uiCloseCd = 120;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Out("[快进]异常: " + ex.Message);
                _pendingMonths = 0;
            }
            if (!CfgCmd) return; // 命令轮询已停用（config: cmd=0）
            _frame++;
            if (_frame < 30) return;
            _frame = 0;
            try
            {
                if (!File.Exists(_cmdPath)) return;
                string text;
                try { text = File.ReadAllText(_cmdPath, Encoding.UTF8); } catch { return; }
                if (string.IsNullOrEmpty(text.Trim())) return;
                try { File.WriteAllText(_cmdPath, ""); } catch { }
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    try { Exec(line); }
                    catch (Exception ex) { Out("命令异常 " + line + " => " + ex); }
                }
            }
            catch { }
        }

        private void Out(string msg)
        {
            try { File.AppendAllText(_outPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n", Encoding.UTF8); } catch { }
            MelonLogger.Msg(msg);
        }

        private void WireDiag()
        {
            UnitIO.Diag = Out;
        }

        private void StartFF(int months)
        {
            _autoMode = true; // 实测：isAutoRun=true 才是完整月度结算（NPC行动+年龄增长），false只推日历
            _pendingMonths += months;
            if (_pendingMonths > 1200) _pendingMonths = 1200;
            Out("[快进] +" + months + "月，当前待推 " + _pendingMonths + " 月（F10停止）");
        }

        private void Exec(string line)
        {
            Out(">>> " + line);
            string[] parts = line.Split(' ');
            string cmd = parts[0].ToLower();
            try
            {
                if (cmd == "count")
                {
                    Out("allUnit=" + g.world.unit.allUnit.Count + " allUnits=" + g.world.unit.allUnits.Count);
                    return;
                }
                if (parts.Length < 2) { Out("缺少参数"); return; }
                string arg = parts[1];
                if (cmd == "stop")
                {
                    _pendingMonths = 0;
                    Out("[快进] 已手动停止");
                    return;
                }
                if (cmd == "month")
                {
                    int n;
                    if (int.TryParse(arg, out n) && n > 0 && n <= 1200)
                    {
                        _pendingMonths = n;
                        _autoMode = parts.Length >= 3 && parts[2] == "auto";
                        Out("[快进] 开始快进 " + n + " 个月 auto=" + _autoMode + "（每轮30天，等结算完成自动续推，stop可中止）");
                    }
                    else Out("参数无效：month 1~1200");
                    return;
                }
                if (cmd == "export")
                {
                    var u0 = FindUnit(arg);
                    if (u0 == null) { Out("查无此单位: " + arg); return; }
                    try
                    {
                        string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "units");
                        Directory.CreateDirectory(dir);
                        string json = UnitIO.SerializeUnit(u0);
                        string file = Path.Combine(dir, u0.data.unitData.unitID + ".json");
                        File.WriteAllText(file, json, Encoding.UTF8);
                        string lid = u0.data.unitData.unitID;
                        ExportLogs(u0, dir, lid);
                        // 记录源世界时间点（导入时按"出生算起"修正年龄用）
                        try
                        {
                            int srcMonth = g.world.run.roundMonth;
                            File.WriteAllText(Path.Combine(dir, lid + "_meta.json"),
                                "{\"srcMonth\":" + srcMonth + "}", Encoding.UTF8);
                        }
                        catch { }
                        Out("导出完成: " + file + " (" + json.Length + " 字符) 名字=" + u0.data.unitData.propertyData.GetName());
                    }
                    catch (Exception ex) { Out("导出失败: " + ex); }
                    return;
                }
                if (cmd == "fixmonths")
                {
                    // fixmonths <实际单位ID> <导出文件ID>：重写持久层原始串为真实月份
                    if (parts.Length < 3) { Out("格式: fixmonths 实际单位ID 导出文件ID"); return; }
                    try
                    {
                        var u0 = FindUnit(arg);
                        if (u0 == null) { Out("查无此单位: " + arg); return; }
                        string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "units");
                        string fileId = parts[2];
                        int shift = 0;
                        string mfile = Path.Combine(dir, fileId + "_meta.json");
                        if (File.Exists(mfile))
                        {
                            string mj = File.ReadAllText(mfile, Encoding.UTF8);
                            int eq = mj.IndexOf(':');
                            int sm;
                            if (eq > 0 && int.TryParse(mj.Substring(eq + 1).Replace("}", "").Trim(), out sm))
                                shift = g.world.run.roundMonth - sm;
                        }
                        string res = UnitIO.FixMonthsRaw(u0.data.unitData.unitID, fileId, dir, shift);
                        var ld = UnitIO.GetStoreLogData(u0.data.unitData.unitID);
                        string verify = ld == null ? "持久层无此单位" : ("视图故事=" + SafeCount(ld, false) + " 大事=" + SafeCount(ld, true));
                        Out("fixmonths: " + res + " " + verify);
                    }
                    catch (Exception ex) { Out("fixmonths失败: " + ex); }
                    return;
                }
                if (cmd == "writestore")
                {
                    // writestore <实际单位ID> <导出文件ID>：文件→缓冲(故事平移)→WriteLogData强制合并→持久层
                    if (parts.Length < 3) { Out("格式: writestore 实际单位ID 导出文件ID"); return; }
                    try
                    {
                        var u0 = FindUnit(arg);
                        if (u0 == null) { Out("查无此单位: " + arg); return; }
                        string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "units");
                        string fileId = parts[2];
                        int shift = 0;
                        string mfile = Path.Combine(dir, fileId + "_meta.json");
                        if (File.Exists(mfile))
                        {
                            string mj = File.ReadAllText(mfile, Encoding.UTF8);
                            int eq = mj.IndexOf(':');
                            int sm;
                            if (eq > 0 && int.TryParse(mj.Substring(eq + 1).Replace("}", "").Trim(), out sm))
                                shift = g.world.run.roundMonth - sm;
                        }
                        string lid = u0.data.unitData.unitID;
                        int drained = UnitIO.DrainBuffers(lid);
                        bool removed = UnitIO.RemoveStoreLogData(lid);
                        int n1 = 0, n2 = 0;
                        string logfile = Path.Combine(dir, fileId + "_log.json");
                        string r1 = "0/0", r2 = "0/0";
                        if (File.Exists(logfile))
                        {
                            var items = UnitIO.ParseLogItems(File.ReadAllText(logfile, Encoding.UTF8));
                            if (items != null) r1 = UnitIO.MergeLogsPerItem(u0, lid, items, false, shift);
                        }
                        string vitfile = Path.Combine(dir, fileId + "_vital.json");
                        if (File.Exists(vitfile))
                        {
                            var items = UnitIO.ParseLogItems(File.ReadAllText(vitfile, Encoding.UTF8));
                            if (items != null) r2 = UnitIO.MergeLogsPerItem(u0, lid, items, true, 0);
                        }
                        var ld = UnitIO.GetStoreLogData(lid);
                        string verify = ld == null ? "持久层仍无此单位" : ("持久层故事=" + SafeCount(ld, false) + " 大事=" + SafeCount(ld, true));
                        Out("writestore: 清缓冲" + drained + " 移除旧记录=" + removed + " 故事A/B=" + r1 + "(+" + shift + "月) 大事A/B=" + r2 + "(真实月份) " + verify);
                    }
                    catch (Exception ex) { Out("writestore失败: " + ex); }
                    return;
                }
                if (cmd == "ghost")
                {
                    // 诊断：找出半初始化的幽灵单位（ID空/data空/propertyData空）——失败注入的残骸
                    int found = 0;
                    var all = g.world.unit.allUnit;
                    foreach (var kv in all)
                    {
                        var u = kv.Value;
                        if (u == null) { Out("幽灵: key=" + kv.Key + " 值=null"); found++; continue; }
                        try
                        {
                            string id = u.data.unitData.unitID;
                            var pd = u.data.unitData.propertyData;
                            if (string.IsNullOrEmpty(id) || pd == null)
                            { Out("幽灵: key=" + kv.Key + " id=[" + (id ?? "null") + "] 名字=" + (pd == null ? "?" : pd.GetName())); found++; }
                        }
                        catch (Exception ge) { Out("幽灵: key=" + kv.Key + " 访问异常 " + ge.Message); found++; }
                        if (found >= 20) { Out("(只列前20)"); break; }
                    }
                    Out("幽灵单位数=" + found + " / 总数=" + all.Count);
                    return;
                }
                if (cmd == "logs")
                {
                    // 诊断：持久层日志（人物故事/生涯大事）查找路径与条数
                    var u0 = FindUnit(arg);
                    if (u0 == null) { Out("查无此单位: " + arg); return; }
                    var diag = new StringBuilder();
                    var ld = UnitIO.GetPersistLogData(u0.data.unitData.unitID, diag);
                    Out(diag.ToString());
                    if (ld == null) { Out("持久层无此单位日志"); return; }
                    int ns = 0, nv = 0;
                    try { ns = ld.allLogData.Count; } catch { }
                    try { nv = ld.allVitalLogData.Count; } catch { }
                    Out("人物故事条数=" + ns + " 生涯大事条数=" + nv);
                    var sb3 = new StringBuilder();
                    try { UnitIO.RenderItems(ld.allVitalLogData, "生涯大事", sb3); } catch { }
                    Out(sb3.ToString());
                    return;
                }
                if (cmd == "addvital")
                {
                    // addvital <unitID> <日志id> <年> <月> 参数1|参数2|...
                    if (parts.Length < 5) { Out("格式: addvital unitID 日志id 年 月 参数1|参数2"); return; }
                    try
                    {
                        int logId; if (!int.TryParse(parts[2], out logId)) { Out("日志id无效"); return; }
                        int year; if (!int.TryParse(parts[3], out year)) { Out("年无效"); return; }
                        int mon; if (!int.TryParse(parts[4], out mon)) { Out("月无效"); return; }
                        var u1 = FindUnit(arg);
                        if (u1 == null) { Out("查无此单位"); return; }
                                string pms = parts.Length >= 6 ? parts[5] : "";
                                string[] vals = pms.Split('|');
                                int monthAbs = year * 12 + (mon - 1);
                                int cnt = UnitIO.AddVitalLog(u1, logId, vals, monthAbs);
                                Out("已写入生涯大事: 第" + year + "年" + mon + "月 (绝对月" + monthAbs + ") 日志id=" + logId + " 返回" + cnt);
                    }
                    catch (Exception ex) { Out("addvital失败: " + ex); }
                    return;
                }
                if (cmd == "import")
                {
                    try
                    {
                        string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "units");
                        string file = Path.Combine(dir, arg + ".json");
                        if (!File.Exists(file)) { Out("文件不存在: " + file); return; }
                        string json = File.ReadAllText(file, Encoding.UTF8);
                        string newName = parts.Length >= 3 ? parts[2] : null;
                        bool useNewId = parts.Length >= 4 && parts[3] == "new";
                        string newId = useNewId ? RandomId() : null; // 默认保留原ID：同世界快照下旧关系自动恢复
                        var added = UnitIO.ImportUnit(json, newName, newId);
                        if (added != null)
                        {
                            // 年龄修正：出生算起 = 源年龄 + (目标世界月 - 源世界月)
                            try
                            {
                                string mfile = Path.Combine(dir, arg + "_meta.json");
                                if (File.Exists(mfile))
                                {
                                    string mj = File.ReadAllText(mfile, Encoding.UTF8);
                                    int eq = mj.IndexOf(':');
                                    if (eq > 0)
                                    {
                                        string rest = mj.Substring(eq + 1).Replace("}", "").Trim();
                                        int sm;
                                        if (int.TryParse(rest, out sm))
                                        {
                                            int curMonth = g.world.run.roundMonth;
                                            int shift = curMonth - sm;
                                            if (shift > 0 && shift < 6000)
                                            {
                                                var pd = added.data.unitData.propertyData;
                                                pd.age = pd.age + shift;
                                                Out("年龄修正: 源月" + sm + "->当前月" + curMonth + " 补" + shift + "月 (新年龄" + (pd.age / 12) + "岁)");
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                            Out("注入成功: " + UnitSummary(added));
                            // 人物故事导入：直接写持久层；故事月份平移(源->当前)，大事保留原月
                            int logShift = 0;
                            try
                            {
                                string mfile0 = Path.Combine(dir, arg + "_meta.json");
                                if (File.Exists(mfile0))
                                {
                                    string mj0 = File.ReadAllText(mfile0, Encoding.UTF8);
                                    int eq0 = mj0.IndexOf(':');
                                    int sm0;
                                    if (eq0 > 0 && int.TryParse(mj0.Substring(eq0 + 1).Replace("}", "").Trim(), out sm0))
                                        logShift = g.world.run.roundMonth - sm0;
                                }
                            }
                            catch { }
                            try
                            {
                                string logfile = Path.Combine(dir, arg + "_log.json");
                                if (File.Exists(logfile))
                                {
                                    string ljson = File.ReadAllText(logfile, Encoding.UTF8);
                                    int cnt = UnitIO.ImportLogsToBuffer(ljson, added, false, logShift);
                                    Out("人物故事入缓冲(月份+" + logShift + "): " + cnt + " 条");
                                }
                                string vitfile = Path.Combine(dir, arg + "_vital.json");
                                if (File.Exists(vitfile))
                                {
                                    string vjson = File.ReadAllText(vitfile, Encoding.UTF8);
                                    int vcnt = UnitIO.ImportLogsToBuffer(vjson, added, true, 0);
                                    Out("生涯大事入缓冲: " + vcnt + " 条");
                                }
                                string mr = UnitIO.ForceMergeLogs();
                                var ld = UnitIO.GetStoreLogData(added.data.unitData.unitID);
                                string verify = ld == null ? "持久层仍无此单位" : ("持久层故事=" + SafeCount(ld, false) + " 大事=" + SafeCount(ld, true));
                                Out("强制合并=" + mr + " " + verify);
                            }
                            catch (Exception le) { Out("日志导入失败: " + le.Message); }
                        }
                        else Out("注入失败(AddUnit返回null)，详见日志");
                    }
                    catch (Exception ex) { Out("导入失败: " + ex); }
                    return;
                }
                if (cmd == "top")
                {
                    // top age 10 / top life 10 / top residue 10（年龄/寿元/剩余寿元 排行）
                    int n = 10;
                    if (parts.Length >= 3) { int t; if (int.TryParse(parts[2], out t)) n = t; }
                    var list2 = g.world.unit.GetUnits(true);
                    if (list2 == null) { Out("世界未就绪"); return; }
                    var rows = new System.Collections.Generic.List<string[]>();
                    foreach (WorldUnitBase u in list2)
                    {
                        if (u == null) continue;
                        try
                        {
                            var pd = u.data.unitData.propertyData;
                            long v;
                            if (arg == "life") v = pd.life;
                            else if (arg == "residue") v = (long)pd.life - pd.age;
                            else v = pd.age;
                            rows.Add(new string[] { v.ToString(), UnitSummary(u) });
                        }
                        catch { }
                    }
                    // 按v降序选前n
                    for (int i = 0; i < rows.Count - 1 && i < n; i++)
                    {
                        int maxJ = i; long maxV = long.Parse(rows[i][0]);
                        for (int j = i + 1; j < rows.Count; j++)
                        {
                            long v = long.Parse(rows[j][0]);
                            if (v > maxV) { maxV = v; maxJ = j; }
                        }
                        var tmp = rows[i]; rows[i] = rows[maxJ]; rows[maxJ] = tmp;
                    }
                    Out("== " + arg + " 排行前" + Math.Min(n, rows.Count) + "（单位:月） ==");
                    for (int i = 0; i < rows.Count && i < n; i++) Out((i + 1) + ". " + rows[i][1] + " [" + arg + "=" + (long.Parse(rows[i][0]) / 12) + "年]");
                    return;
                }
                if (cmd == "find")
                {
                    int hits = 0;
                    var list = g.world.unit.GetUnits(true);
                    if (list != null)
                    {
                        foreach (WorldUnitBase u in list)
                        {
                            if (u == null) continue;
                            string name = "";
                            try { name = u.data.unitData.propertyData.GetName(); } catch { }
                            if (name == null || !name.Contains(arg)) continue;
                            Out(UnitSummary(u));
                            hits++;
                            if (hits >= 20) { Out("(只显示前20条)"); break; }
                        }
                    }
                    Out("匹配 " + hits + " 个单位");
                    return;
                }
                WorldUnitBase unit = FindUnit(arg);
                if (unit == null) { Out("查无此单位: " + arg); return; }
                if (cmd == "unit")
                {
                    Out(UnitSummary(unit));
                }
                else if (cmd == "rel")
                {
                    var rel = unit.data.unitData.relationData;
                    Out("配偶: " + (rel.married ?? "无"));
                    DumpIds("师傅master", rel.master);
                    DumpIds("徒弟student", rel.student);
                    DumpIds("情人lover", rel.lover);
                    DumpChildren("children", rel.children, unit);
                    DumpChildren("childrenPrivate", rel.childrenPrivate, unit);
                }
                else if (cmd == "log")
                {
                    int n = 10;
                    if (parts.Length >= 3) int.TryParse(parts[2], out n);
                    var allAdd = g.world.unitLog.allAddLogData;
                    Il2CppSystem.Collections.Generic.List<WorldUnitLogMgr.LogData> logs = null;
                    string lid = unit.data.unitData.unitID;
                    if (allAdd == null || !allAdd.ContainsKey(lid)) { Out("无日志数据(allAddLogData无此单位)"); return; }
                    try { logs = allAdd[lid]; } catch (Exception le) { Out("日志索引失败: " + le.Message); return; }
                    if (logs == null) { Out("日志列表为空"); return; }
                    Out("日志包数=" + logs.Count);
                    int shown = 0;
                    foreach (var ld in logs)
                    {
                        if (ld == null || ld.logData == null) continue;
                        var item = ld.logData;
                        if (item.logs == null) continue;
                        foreach (var d in item.logs)
                        {
                            if (d == null || d.id == null) continue;
                            string ids = "";
                            foreach (int i in d.id) ids += i + ",";
                            string vals = "";
                            if (d.values != null) foreach (string v in d.values) vals += v + "|";
                            Out("month=" + item.month + " id=[" + ids + "] values=[" + vals + "]");
                            shown++;
                            if (shown >= n) return;
                        }
                    }
                    Out("共显示 " + shown + " 条");
                }
                else if (cmd == "obj")
                {
                    var all = unit.data.unitData.objData.allObject;
                    if (all == null) { Out("objData为空"); return; }
                    foreach (var kv in all)
                    {
                        var inner = kv.Value as Il2CppSystem.Collections.Generic.Dictionary<string, string>;
                        if (inner != null)
                        {
                            foreach (var ikv in inner) Out(kv.Key + " :: " + ikv.Key + " = " + ikv.Value);
                        }
                        else Out(kv.Key + " => " + kv.Value);
                    }
                    Out("objData组数=" + all.Count);
                }
                else Out("未知命令: " + cmd);
            }
            catch (Exception e) { Out("执行出错: " + e); }
        }

        /// <summary>自动导出：DebugAgent\autoexport.txt 内容为单位ID。世界加载完成且单位存在时导出一次，成功后改名为 autoexport.done.txt。</summary>
        private void TryAutoExport()
        {
            try
            {
                string flag = Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "autoexport.txt");
                if (!File.Exists(flag)) return;
                string id = File.ReadAllText(flag, Encoding.UTF8).Trim();
                if (id.Length == 0) return;
                if (g.world == null || g.world.unit == null || g.world.unit.allUnit == null) return;
                if (!g.world.unit.allUnit.ContainsKey(id)) return; // 世界没加载完或不在该档
                var u = FindUnit(id);
                if (u == null) return;
                string dir = Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "units");
                Directory.CreateDirectory(dir);
                string json = UnitIO.SerializeUnit(u);
                File.WriteAllText(Path.Combine(dir, id + ".json"), json, Encoding.UTF8);
                ExportLogs(u, dir, id);
                int srcMonth = g.world.run.roundMonth;
                File.WriteAllText(Path.Combine(dir, id + "_meta.json"), "{\"srcMonth\":" + srcMonth + "}", Encoding.UTF8);
                File.Delete(flag);
                File.WriteAllText(Path.Combine(MelonUtils.GameDirectory, "DebugAgent", "autoexport.done.txt"),
                    id + " @" + srcMonth, Encoding.UTF8);
                Out("[自动导出] 完成: " + id + " srcMonth=" + srcMonth);
            }
            catch (Exception e) { Out("[自动导出] 失败: " + e.Message); }
        }

        /// <summary>导出单位日志：优先持久层(DataMgr.dataUnitLog)，回退本轮缓冲(allAdd*)。写 _log.json/_vital.json/_readable.txt（格式=List&lt;LogItemData&gt;）</summary>
        private void ExportLogs(WorldUnitBase u, string dir, string lid)
        {
            try
            {
                var ld = UnitIO.GetPersistLogData(lid, null);
                Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData> storyItems, vitalItems;
                var readable = new StringBuilder();
                if (ld != null)
                {
                    storyItems = ld.allLogData;
                    vitalItems = ld.allVitalLogData;
                    try { UnitIO.RenderItems(ld.allLogData, "人物故事", readable); } catch { }
                    try { UnitIO.RenderItems(ld.allVitalLogData, "生涯大事", readable); } catch { }
                }
                else
                {
                    var allAdd = g.world.unitLog.allAddLogData;
                    var allVital = g.world.unitLog.allAddVitalLogData;
                    // 缓冲是包装列表(List<WorldUnitLogMgr.LogData>)，剥壳取 logData 统一成 List<LogItemData> 格式
                    storyItems = UnwrapItems(allAdd != null && allAdd.ContainsKey(lid) ? allAdd[lid] : null);
                    vitalItems = UnwrapItems(allVital != null && allVital.ContainsKey(lid) ? allVital[lid] : null);
                    readable.Append("(持久层未找到，仅导出本轮新增缓冲)\n");
                    var diag = new StringBuilder();
                    try { UnitIO.GetPersistLogData(lid, diag); readable.Append(diag); } catch { }
                }
                if (storyItems != null && storyItems.Count > 0)
                {
                    var sb2 = new StringBuilder();
                    UnitIO.WriteValuePublic(storyItems, sb2);
                    File.WriteAllText(Path.Combine(dir, lid + "_log.json"), sb2.ToString(), Encoding.UTF8);
                }
                if (vitalItems != null && vitalItems.Count > 0)
                {
                    var sb2 = new StringBuilder();
                    UnitIO.WriteValuePublic(vitalItems, sb2);
                    File.WriteAllText(Path.Combine(dir, lid + "_vital.json"), sb2.ToString(), Encoding.UTF8);
                }
                File.WriteAllText(Path.Combine(dir, lid + "_readable.txt"), readable.ToString(), Encoding.UTF8);
                Out("日志导出: 人物故事" + (storyItems == null ? 0 : storyItems.Count) + "条 生涯大事" + (vitalItems == null ? 0 : vitalItems.Count) + "条 可读文本见 _readable.txt");
            }
            catch (Exception ex) { Out("日志导出失败: " + ex.Message); }
        }

        /// <summary>把缓冲包装列表剥成 List&lt;LogItemData&gt;（不构造任何对象，纯收集引用）</summary>
        private Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData> UnwrapItems(Il2CppSystem.Collections.Generic.List<WorldUnitLogMgr.LogData> wrapped)
        {
            var list = new Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData>();
            if (wrapped == null) return list;
            foreach (var w in wrapped)
            {
                if (w == null || w.logData == null) continue;
                list.Add(w.logData);
            }
            return list;
        }

        private static int SafeCount(DataUnitLog.LogData ld, bool vital)
        {
            try { return vital ? ld.allVitalLogData.Count : ld.allLogData.Count; } catch { return -1; }
        }

        private string UnitSummary(WorldUnitBase u)
        {
            try
            {
                var ud = u.data.unitData;
                string sex = "?";
                try { sex = ((int)ud.propertyData.sex) == 2 ? "女" : "男"; } catch { }
                string kids = "0";
                try { kids = "" + ud.relationData.children.Count + "+" + ud.relationData.childrenPrivate.Count; } catch { }
                return "id=" + ud.unitID + " 名字=" + ud.propertyData.GetName() + " 性别=" + sex
                    + " 年龄=" + ud.propertyData.age + " 寿元=" + ud.propertyData.life
                    + " 配偶=" + (ud.relationData.married ?? "无") + " 孩子=" + kids
                    + " 死亡=" + u.isDie;
            }
            catch (Exception e) { return "摘要出错 " + e.Message; }
        }

        private void DumpIds(string tag, Il2CppSystem.Collections.Generic.List<string> ids)
        {
            if (ids == null || ids.Count == 0) { Out(tag + ": 无"); return; }
            Out(tag + ": " + ids.Count + " 个");
            int shown = 0;
            foreach (string id in ids)
            {
                WorldUnitBase u = FindUnit(id);
                if (u == null) { Out("  " + id + " => 查无此单位"); continue; }
                Out("  " + UnitSummary(u));
                shown++;
                if (shown >= 30) { Out("  ...(共" + ids.Count + "个，只列30)"); break; }
            }
        }

        private void DumpChildren(string tag, Il2CppSystem.Collections.Generic.List<string> ids, WorldUnitBase mother)
        {
            if (ids == null || ids.Count == 0) { Out(tag + ": 无"); return; }
            Out(tag + ": " + ids.Count + " 个");
            foreach (string cid in ids)
            {
                WorldUnitBase c = FindUnit(cid);
                if (c == null) { Out("  " + cid + " => 查无此单位"); continue; }
                string ps = "";
                try
                {
                    var arr = c.data.unitData.relationData.parent;
                    if (arr != null) foreach (string p in arr)
                    {
                        WorldUnitBase pu = FindUnit(p);
                        ps += p + (pu != null ? "(" + pu.data.unitData.propertyData.GetName() + ")" : "(未找到)") + ",";
                    }
                }
                catch { }
                Out("  " + UnitSummary(c) + "  parent=[" + ps + "]");
            }
        }

        private static string RandomId()
        {
            var r = new System.Random();
            const string cs = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++) sb.Append(cs[r.Next(cs.Length)]);
            return sb.ToString();
        }

        private WorldUnitBase FindUnit(string id)
        {
            try { if (g.world.unit.allUnit != null && g.world.unit.allUnit.ContainsKey(id)) return g.world.unit.allUnit[id]; } catch { }
            try { if (g.world.unit.allUnits != null && g.world.unit.allUnits.ContainsKey(id)) return g.world.unit.allUnits[id]; } catch { }
            return null;
        }
    }
}

    /// <summary>
    /// 自定义文本日志：id=999999 时直出 values[0] 原文（不查 RoleLog 表），供 addvital 写自由叙事。
    /// </summary>
    public static class CustomLogPatch
    {
        public static bool Prefix(DataUnitLog.LogData.Data __instance, ref string __result)
        {
            try
            {
                if (__instance != null && __instance.id != null && __instance.id.Contains(999999)
                    && __instance.values != null && __instance.values.Length > 0)
                {
                    __result = __instance.values[0];
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>捕获 MapWorldMgr 实例（进存档创建世界时触发）</summary>
    public static class CtorPatch
    {
        public static void Post(MapWorldMgr __instance)
        {
            MOD_dbgAgent.ModMain._mapWorld = __instance;
            try { MelonLogger.Msg("DebugAgent: MapWorldMgr 实例已捕获"); } catch { }
        }
    }
