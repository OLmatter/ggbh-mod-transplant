using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace MOD_dbgAgent
{
    /// <summary>
    /// 单位数据的导出/导入：UnitInfoData 全字段反射序列化（JSON），绕过存档加密。
    /// 导出：SerializeUnit -> DebugAgent\units\unitID.json
    /// 导入：BuildFromJson 构造 UnitInfoData -> WorldUnitMgr.AddUnit 注入世界。
    /// </summary>
    internal static class UnitIO
    {
        private static readonly HashSet<string> SkipProps = new HashSet<string>
        {
            "Pointer", "Il2CppType", "WasCollected", "TypeId", "MonoPtr", "CachedPointer"
        };
        private static readonly HashSet<string> SkipTypes = new HashSet<string>
        {
            "WorldUnitBase", "MapBuildSchool", "ConfMgrVariant", "WorldRunMgr", "AutoCallQueue",
            "Il2CppSystem", "Il2Cppmscorlib", "UnhollowerBaseLib"
        };

        // ===== 序列化 =====
        public static string SerializeUnit(WorldUnitBase u)
        {
            var sb = new StringBuilder(1 << 20);
            var seen = new HashSet<long>();
            WriteValue(u.data.unitData, sb, seen, 0);
            return sb.ToString();
        }

        public static void WriteValuePublic(object v, StringBuilder sb)
        {
            WriteValue(v, sb, new HashSet<long>(), 0);
        }

        private static void WriteValue(object v, StringBuilder sb, HashSet<long> seen, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            Type t = v.GetType();
            if (v is string) { WriteString((string)v, sb); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)
            { sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (v is float || v is double)
            { sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (t.IsEnum) { sb.Append(Convert.ToString(Convert.ToInt64(v))); return; }
            if (depth > 10) { sb.Append("null"); return; }

            // il2cpp对象身份（防循环引用）
            long ptr = TryGetPointer(v);
            if (ptr != 0)
            {
                if (seen.Contains(ptr)) { sb.Append("null"); return; }
                seen.Add(ptr);
            }

            string tn = t.FullName ?? t.Name;

            // 可枚举容器（List/Dictionary/数组）先枚举再谈跳过：
            // SkipTypes 里的 "Il2CppSystem" 本意是挡运行时引用，但 List<T> 的全名也是
            // Il2CppSystem.Collections.Generic.List`1[[...]]，先查会整个序列化成 null（法宝/道具丢数据的根因）
            var items = TryEnumerate(v);
            if (items != null)
            {
                sb.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(items[i], sb, seen, depth + 1);
                }
                sb.Append(']');
                return;
            }

            foreach (var st in SkipTypes) { if (tn.Contains(st)) { sb.Append("null"); return; } }

            // 普通il2cpp对象：属性字典
            sb.Append("{\"__t\":");
            WriteString(tn, sb);
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var saved = new List<KeyValuePair<string, object>>();
            foreach (var p in props)
            {
                if (SkipProps.Contains(p.Name)) continue;
                if (p.Name.StartsWith("get_") || p.Name.StartsWith("_")) { }
                try
                {
                    object pv = p.GetValue(v, null);
                    if (pv == null) continue;
                    if (pv is IntPtr) continue;
                    saved.Add(new KeyValuePair<string, object>(p.Name, pv));
                }
                catch { }
            }
            foreach (var kv in saved)
            {
                sb.Append(',');
                WriteString(kv.Key, sb);
                sb.Append(':');
                WriteValue(kv.Value, sb, seen, depth + 1);
            }
            sb.Append('}');
        }

        private static long TryGetPointer(object v)
        {
            try
            {
                var p = v.GetType().GetProperty("Pointer");
                if (p != null)
                {
                    object o = p.GetValue(v, null);
                    if (o is IntPtr) return ((IntPtr)o).ToInt64();
                }
            }
            catch { }
            return 0;
        }

        private static List<object> TryEnumerate(object v)
        {
            try
            {
                Type t = v.GetType();
                if (t.Name.StartsWith("Dictionary`2"))
                {
                    var result = new List<object>();
                    // 反射调GetEnumerator
                    var m = t.GetMethod("GetEnumerator");
                    if (m == null) return null;
                    var en = m.Invoke(v, null);
                    if (en == null) return null;
                    var enT = en.GetType();
                    var moveNext = enT.GetMethod("MoveNext");
                    var curProp = enT.GetProperty("Current");
                    while ((bool)moveNext.Invoke(en, null))
                    {
                        var pair = curProp.GetValue(en, null);
                        if (pair == null) continue;
                        var pk = pair.GetType().GetProperty("Key");
                        var pv = pair.GetType().GetProperty("Value");
                        if (pk == null || pv == null) continue;
                        result.Add(pk.GetValue(pair, null));
                        result.Add(pv.GetValue(pair, null));
                    }
                    return result;
                }
                // List或数组：Length/Count + 索引
                var countProp = t.GetProperty("Count") ?? t.GetProperty("Length");
                if (countProp == null) return null;
                int n = Convert.ToInt32(countProp.GetValue(v, null));
                // 有GetEnumerator就用枚举（List/IEnumerable）
                var gm = t.GetMethod("GetEnumerator");
                if (gm != null)
                {
                    var result = new List<object>();
                    var en = gm.Invoke(v, null);
                    if (en != null)
                    {
                        var enT = en.GetType();
                        var mn = enT.GetMethod("MoveNext");
                        var cp = enT.GetProperty("Current");
                        if (mn != null && cp != null)
                        {
                            while ((bool)mn.Invoke(en, null)) result.Add(cp.GetValue(en, null));
                            return result;
                        }
                    }
                }
                if (n >= 0 && n < 100000)
                {
                    var result2 = new List<object>(n);
                    var itemProp = t.GetProperty("Item");
                    if (itemProp != null && itemProp.GetIndexParameters().Length == 1)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            try { result2.Add(itemProp.GetValue(v, new object[] { i })); } catch { }
                        }
                        return result2;
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>导入人物故事：JSON(List&lt;LogItemData&gt;) 填入过月缓冲（原生 AddLogData/AddVitalLogData），
        /// 再由 ForceMergeLogs 立即合并进持久层。vital=false 时按 monthShift 平移月份（游戏只留近期故事，防被清）。</summary>
        public static int ImportLogsToBuffer(string json, WorldUnitBase unit, bool vital, int monthShift)
        {
            int pos = 0;
            object root = ParseJson(json, ref pos);
            var listType = typeof(Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData>);
            object list = BuildFrom(root, listType);
            if (list == null) return 0;
            var typed = (Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData>)list;
            int ok = 0;
            foreach (var item in typed)
            {
                if (item == null) continue;
                try
                {
                    if (!vital && monthShift != 0) item.month = item.month + monthShift;
                    if (vital) g.world.unitLog.AddVitalLogData(unit, item);
                    else g.world.unitLog.AddLogData(unit, item);
                    ok++;
                }
                catch { }
            }
            return ok;
        }

        /// <summary>构造 il2cpp 对象：先试无参构造（失败会抛异常），再退原生分配。绝不因构造失败抛出。</summary>
        private static object NewAny(Type t)
        {
            try { var o = Activator.CreateInstance(t); if (o != null) return o; } catch { }
            return NewIl2CppRaw(t);
        }

        /// <summary>逐条合并进持久层。路径A: MerageAddLog(uid,[包装],月份,是否大事)——直入持久层且带该条真实月份；
        /// 路径B: AddLogData→UpdateLogData(缓冲→当月暂存)→WriteLogData(该条月份)。A异常自动用B兜底。返回"成功A数/B数"。</summary>
        public static string MergeLogsPerItem(WorldUnitBase unit, string uid, Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData> items, bool vital, int monthShift)
        {
            var wMethod = typeof(WorldUnitLogMgr).GetMethod("WriteLogData");
            var updateMethod = typeof(WorldUnitLogMgr).GetMethod("UpdateLogData");
            int okA = 0, okB = 0;
            foreach (var item in items)
            {
                if (item == null) continue;
                int m = item.month + (vital ? 0 : monthShift);
                item.month = m;
                // 路径A
                try
                {
                    var w = NewAny(typeof(WorldUnitLogMgr.LogData));
                    if (w != null)
                    {
                        var wr = (WorldUnitLogMgr.LogData)w;
                        wr.unitID = uid;
                        wr.logData = item;
                        var list = new Il2CppSystem.Collections.Generic.List<WorldUnitLogMgr.LogData>();
                        list.Add(wr);
                        g.world.unitLog.MerageAddLog(uid, list, m, vital);
                        okA++;
                        continue;
                    }
                }
                catch { }
                // 路径B
                try
                {
                    if (vital) g.world.unitLog.AddVitalLogData(unit, item);
                    else g.world.unitLog.AddLogData(unit, item);
                    if (updateMethod != null) updateMethod.Invoke(g.world.unitLog, null);
                    if (wMethod != null)
                        wMethod.Invoke(g.world.unitLog, new object[] {
                            g.world.unit.allUnit,
                            g.world.unitLog.allAddLogData,
                            g.world.unitLog.allAddVitalLogData,
                            m });
                    DrainBuffers(uid);
                    okB++;
                }
                catch { }
            }
            return okA + "/" + okB;
        }

        /// <summary>从文件解析出 LogItemData 列表（writestore/import 共用）</summary>
        public static Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData> ParseLogItems(string json)
        {
            int pos = 0;
            object root = ParseJson(json, ref pos);
            var listType = typeof(Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData>);
            object list = BuildFrom(root, listType);
            return list as Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData>;
        }

        /// <summary>移除单位在持久层的 LogData（让合并原生重建）</summary>
        public static bool RemoveStoreLogData(string unitId)
        {
            try
            {
                var ul = GetUnitLog(null);
                if (ul == null || ul.data == null) return false;
                var all = ul.data.allLog;
                if (all == null || !all.ContainsKey(unitId)) return false;
                all.Remove(unitId);
                return true;
            }
            catch { return false; }
        }

        /// <summary>重写持久层原始串。实测 raw 条目=2元素数组 [月份字符串, 内容串(DataToString)]。
        /// 自验证：先确认 raw[0][0] 解析为接近当前月的整数才动手；否则只转储结构不动数据。</summary>
        public static string FixMonthsRaw(string uid, string fileId, string dir, int shift)
        {
            var ld = GetStoreLogData(uid);
            if (ld == null) return "持久层无此单位，先跑writestore";
            var rawV = ld.allVitalLog;
            var rawS = ld.allLog;
            if (rawV == null || rawS == null) return "raw列表为空";
            // 结构转储与验证
            if (rawV.Count == 0) return "rawV为空";
            int len0 = rawV[0].Length;
            string e0 = "", e1 = "";
            try { e0 = rawV[0][0] ?? ""; } catch { }
            try { e1 = rawV[0].Length > 1 ? (rawV[0][1] ?? "") : ""; } catch { }
            string dump = "raw[0]长度=" + len0 + " [0]=" + (e0.Length > 40 ? e0.Substring(0, 40) : e0) + " [1]=" + (e1.Length > 60 ? e1.Substring(0, 60) : e1);
            int m0;
            bool monthInE0 = len0 == 2 && int.TryParse(e0, out m0) && Math.Abs(m0 - g.world.run.roundMonth) <= 2;
            if (!monthInE0) return "中止(结构未确认): " + dump + " 当前月=" + g.world.run.roundMonth;
            string vitfile = Path.Combine(dir, fileId + "_vital.json");
            if (!File.Exists(vitfile)) return "缺 _vital.json";
            var itemsV = ParseLogItems(File.ReadAllText(vitfile, Encoding.UTF8));
            if (itemsV == null) return "_vital.json 解析失败";
            int nv = 0;
            rawV.Clear();
            foreach (var it in itemsV)
            {
                if (it == null) continue;
                try
                {
                    string content = it.DataToString();
                    if (string.IsNullOrEmpty(content)) continue;
                    rawV.Add(new UnhollowerBaseLib.Il2CppStringArray(new string[] { it.month.ToString(), content }));
                    nv++;
                }
                catch { }
            }
            int ns = 0;
            string logfile = Path.Combine(dir, fileId + "_log.json");
            if (File.Exists(logfile))
            {
                var itemsS = ParseLogItems(File.ReadAllText(logfile, Encoding.UTF8));
                if (itemsS != null)
                {
                    rawS.Clear();
                    foreach (var it in itemsS)
                    {
                        if (it == null) continue;
                        try
                        {
                            it.month = it.month + shift;
                            string content = it.DataToString();
                            if (string.IsNullOrEmpty(content)) continue;
                            rawS.Add(new UnhollowerBaseLib.Il2CppStringArray(new string[] { it.month.ToString(), content }));
                            ns++;
                        }
                        catch { }
                    }
                }
            }
            // 强制视图按新 raw 重建
            try { ld.lastUpdateVitalLogDataMonth = -1; } catch { }
            try { ld.lastUpdateLogDataMonth = -1; } catch { }
            return dump + " => raw重写: 大事" + nv + "条(真实月份) 故事" + ns + "条(+" + shift + "月)";
        }

        /// <summary>强制执行游戏自己的缓冲→持久层合并（WorldUnitLogMgr.WriteLogData），不用等过月。返回错误文本或"ok"</summary>
        public static string ForceMergeLogs()
        {
            try
            {
                var m = typeof(WorldUnitLogMgr).GetMethod("WriteLogData");
                if (m == null) return "WriteLogData方法不存在";
                // 编译器验证签名：第4参是单个 int（此前反射误显示 Int32[]）
                m.Invoke(g.world.unitLog, new object[] {
                    g.world.unit.allUnit,
                    g.world.unitLog.allAddLogData,
                    g.world.unitLog.allAddVitalLogData,
                    g.world.run.roundMonth });
                return "ok";
            }
            catch (Exception e) { return "异常: " + e.Message; }
        }

        /// <summary>取单位持久 LogData（只读诊断用；不创建）</summary>
        public static DataUnitLog.LogData GetStoreLogData(string unitId)
        {
            try
            {
                var ul = GetUnitLog(null);
                if (ul == null || ul.data == null) return null;
                var all = ul.data.allLog;
                if (all == null || !all.ContainsKey(unitId)) return null;
                return all[unitId];
            }
            catch { return null; }
        }

        /// <summary>清掉单位在过月缓冲(allAdd*)里的条目，防止重复合并</summary>
        public static int DrainBuffers(string unitId)
        {
            int n = 0;
            try
            {
                var d1 = g.world.unitLog.allAddLogData;
                if (d1 != null && d1.ContainsKey(unitId)) { var l = d1[unitId]; n += l.Count; d1.Remove(unitId); }
            }
            catch { }
            try
            {
                var d2 = g.world.unitLog.allAddVitalLogData;
                if (d2 != null && d2.ContainsKey(unitId)) { var l = d2[unitId]; n += l.Count; d2.Remove(unitId); }
            }
            catch { }
            return n;
        }

        /// <summary>追加一条自定义生涯大事（id=999999 自由叙事）。走缓冲+立即合并，UI 即刻可见且持久。</summary>
        public static int AddVitalLog(WorldUnitBase unit, int logId, string[] values, int month)
        {
            var dataType = typeof(DataUnitLog.LogData.Data);
            object data = Activator.CreateInstance(dataType) ?? NewIl2CppRaw(dataType);
            if (data == null) throw new Exception("Data 构造失败");
            var idList = new Il2CppSystem.Collections.Generic.List<int>();
            idList.Add(logId);
            dataType.GetProperty("id").SetValue(data, idList, null);
            dataType.GetProperty("values").SetValue(data, new UnhollowerBaseLib.Il2CppStringArray(values), null);
            var itemType = typeof(DataUnitLog.LogData.LogItemData);
            object item = Activator.CreateInstance(itemType) ?? NewIl2CppRaw(itemType);
            if (item == null) throw new Exception("LogItemData 构造失败");
            var logsList = new Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.Data>();
            logsList.Add((DataUnitLog.LogData.Data)data);
            itemType.GetProperty("month").SetValue(item, month, null);
            itemType.GetProperty("logs").SetValue(item, logsList, null);
            // 单条入缓冲→WriteLogData(该条月份)→清缓冲：真实月份直入持久层
            string uid = unit.data.unitData.unitID;
            g.world.unitLog.AddVitalLogData(unit, (DataUnitLog.LogData.LogItemData)item);
            var w = typeof(WorldUnitLogMgr).GetMethod("WriteLogData");
            if (w == null) throw new Exception("WriteLogData方法不存在");
            w.Invoke(g.world.unitLog, new object[] {
                g.world.unit.allUnit,
                g.world.unitLog.allAddLogData,
                g.world.unitLog.allAddVitalLogData,
                month });
            DrainBuffers(uid);
            return 1;
        }

        // ===== 持久层日志（DataMgr.dataUnitLog.data.allLog[unitID]）=====
        // allAddLogData/allAddVitalLogData 只是本月新增缓冲；历史故事/生涯大事存在 DataMgr 的持久字典里。
        private static DataMgr _dataMgrCache;

        /// <summary>定位 DataMgr 实例（g 的静态属性，含两层反射回退）。diag 可为 null。</summary>
        public static DataMgr FindDataMgr(StringBuilder diag)
        {
            if (_dataMgrCache != null) return _dataMgrCache;
            if (_proxyLog != null) return null;
            const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                foreach (var p in typeof(g).GetProperties(SF))
                {
                    try
                    {
                        if (diag != null) diag.Append("g.").Append(p.Name).Append(":").Append(p.PropertyType.Name).Append('\n');
                        if (p.PropertyType.Name == "DataMgr")
                        {
                            var v = p.GetValue(null, null) as DataMgr;
                            if (v != null) { _dataMgrCache = v; if (diag != null) diag.Append("=> DataMgr = g.").Append(p.Name).Append('\n'); return v; }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            // 第二层：g 的某个属性值自身带 dataUnitLog 成员（可能是 GameMgr 之类）
            try
            {
                foreach (var p in typeof(g).GetProperties(SF))
                {
                    object v = null;
                    try { v = p.GetValue(null, null); } catch { }
                    if (v == null) continue;
                    try
                    {
                        var vp = v.GetType().GetProperty("dataUnitLog");
                        if (vp != null)
                        {
                            var dm = v as DataMgr;
                            if (dm == null)
                            {
                                // 不是 DataMgr 类型但带 dataUnitLog：借道反射拿 DataUnitLog
                                var dul = vp.GetValue(v, null);
                                if (dul is DataUnitLog)
                                {
                                    _proxyLog = (DataUnitLog)dul;
                                    if (diag != null) diag.Append("=> DataUnitLog 借道 g.").Append(p.Name).Append('\n');
                                    return null;
                                }
                            }
                            else { _dataMgrCache = dm; return dm; }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static DataUnitLog _proxyLog;

        /// <summary>取 DataUnitLog（优先 DataMgr.dataUnitLog，回退借道对象）</summary>
        public static DataUnitLog GetUnitLog(StringBuilder diag)
        {
            var dm = FindDataMgr(diag);
            try { if (dm != null && dm.dataUnitLog != null) return dm.dataUnitLog; } catch { }
            return _proxyLog;
        }

        /// <summary>取单位持久 LogData（含 allLogData=人物故事 / allVitalLogData=生涯大事）。找不到返回 null。</summary>
        public static DataUnitLog.LogData GetPersistLogData(string unitId, StringBuilder diag)
        {
            try
            {
                var ul = GetUnitLog(diag);
                if (ul == null || ul.data == null) { if (diag != null) diag.Append("dataUnitLog为空\n"); return null; }
                var all = ul.data.allLog;
                if (all == null) { if (diag != null) diag.Append("allLog字典为空\n"); return null; }
                if (diag != null) diag.Append("allLog单位数=").Append(all.Count).Append('\n');
                if (!all.ContainsKey(unitId)) { if (diag != null) diag.Append("allLog无此单位: ").Append(unitId).Append('\n'); return null; }
                return all[unitId];
            }
            catch (Exception e) { if (diag != null) diag.Append("GetPersistLogData异常: ").Append(e.Message).Append('\n'); return null; }
        }

        /// <summary>渲染 LogItemData 为可读文本（GetLogString 失败时退回 id/values 原文）</summary>
        public static void RenderItems(Il2CppSystem.Collections.Generic.List<DataUnitLog.LogData.LogItemData> items, string tag, StringBuilder sb)
        {
            if (items == null || items.Count == 0) { sb.Append("[").Append(tag).Append(" 无]\n"); return; }
            sb.Append("[").Append(tag).Append(" 共").Append(items.Count).Append("条]\n");
            foreach (var item in items)
            {
                if (item == null) continue;
                int y = item.month / 12, m = item.month % 12 + 1;
                sb.Append("· 第").Append(y).Append("年").Append(m).Append("月 (abs=").Append(item.month).Append(")\n");
                try
                {
                    if (item.logs != null) foreach (var d in item.logs)
                        {
                            if (d == null) continue;
                            string s = null;
                            try { s = d.GetLogString(); } catch { }
                            if (string.IsNullOrEmpty(s))
                            {
                                string ids = "";
                                if (d.id != null) foreach (int i in d.id) ids += i + ",";
                                string vals = "";
                                if (d.values != null) foreach (string v2 in d.values) vals += v2 + "|";
                                s = "(raw id=[" + ids + "] values=[" + vals + "])";
                            }
                            sb.Append("   ").Append(s).Append('\n');
                        }
                }
                catch { }
            }
        }

        // ===== 反序列化 =====
        /// <summary>诊断输出回调（由 ModMain 注入 Out，避免 UnitIO 依赖 MelonLogger）</summary>
        public static Action<string> Diag = delegate { };

        /// <summary>世界结构性字段：来自源世界，直接覆写会令 AddUnit 原生数组越界（schoolID/网格/索引等），保留本世界骨架值</summary>
        private static readonly HashSet<string> WorldStructuralFields = new HashSet<string>
        {
            "indexNum", "pointX", "pointY", "pointGridData", "isChangePoint",
            "dieEscapeBeforePointX", "dieEscapeBeforePointY", "schoolID",
            "createDay", "residueDay", "_unit", "unit"
        };

        /// <summary>用游戏工厂(RandomInitNPCUnit)生成一个本世界合法的 UnitInfoData 骨架。
        /// 优先 g 静态属性找 ConfMgr.roleAttributeCoefficient，退回直接 Activator 构造。</summary>
        private static DataUnit.UnitInfoData CreateSkeleton(int sex)
        {
            object rac = null;
            try
            {
                const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                foreach (var p in typeof(g).GetProperties(SF))
                {
                    try
                    {
                        if (p.PropertyType.Name == "ConfMgr")
                        {
                            var conf = p.GetValue(null, null);
                            if (conf != null)
                            {
                                var rp = conf.GetType().GetProperty("roleAttributeCoefficient");
                                if (rp != null) { rac = rp.GetValue(conf, null); if (rac != null) break; }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            if (rac == null)
            {
                try { rac = Activator.CreateInstance(typeof(ConfRoleAttributeCoefficient)); } catch { }
            }
            if (rac == null) throw new Exception("ConfRoleAttributeCoefficient 实例不可得");
            var m = typeof(ConfRoleAttributeCoefficient).GetMethod("RandomInitNPCUnit");
            if (m == null) throw new Exception("RandomInitNPCUnit 方法不存在");
            var fresh = m.Invoke(rac, new object[] { sex, 0, 0 });
            return fresh as DataUnit.UnitInfoData;
        }

        public static WorldUnitBase ImportUnit(string json, string newName, string newId)
        {
            int pos = 0;
            object root = ParseJson(json, ref pos);
            var d = root as Dictionary<string, object>;
            if (d == null) throw new Exception("JSON根节点不是对象 (root=" + (root == null ? "null" : root.GetType().Name) + ")");
            // 源数据性别（决定骨架工厂参数；默认2=女）
            int sex = 2;
            try
            {
                var pd = d["propertyData"] as Dictionary<string, object>;
                if (pd != null && pd.ContainsKey("sex")) sex = Convert.ToInt32(pd["sex"]);
            }
            catch { }
            // 1) 本世界合法骨架（indexNum/schoolID/pointGrid 等全部本世界有效）
            var info = CreateSkeleton(sex);
            if (info == null) throw new Exception("骨架构造失败");
            // 2) 递归合并她的数据进骨架（子对象保留骨架原生实例只写字段；跳过世界结构性字段）
            int setCount = 0, skipCount = 0;
            MergeInto(d, info, WorldStructuralFields, ref setCount, ref skipCount);
            // 3) ID与名字
            var infoType = info.GetType();
            try
            {
                string oid = d.ContainsKey("unitID") && d["unitID"] is string ? (string)d["unitID"] : null;
                string finalId = !string.IsNullOrEmpty(newId) ? newId : oid;
                if (!string.IsNullOrEmpty(finalId)) infoType.GetProperty("unitID").SetValue(info, finalId, null);
            }
            catch { }
            if (!string.IsNullOrEmpty(newName))
            {
                try
                {
                    object pd2 = infoType.GetProperty("propertyData").GetValue(info, null);
                    if (pd2 != null)
                    {
                        var nameProp = pd2.GetType().GetProperty("name");
                        var ctor = nameProp.PropertyType.GetConstructor(new Type[] { typeof(string[]) });
                        if (ctor != null) nameProp.SetValue(pd2, ctor.Invoke(new object[] { new string[] { newName } }), null);
                    }
                }
                catch { }
            }
            // 4) 出生点放玩家旁边（方便找到她）
            try
            {
                var pu = g.world.playerUnit;
                if (pu != null)
                {
                    var pud = pu.data.unitData;
                    infoType.GetProperty("pointX").SetValue(info, pud.pointX, null);
                    infoType.GetProperty("pointY").SetValue(info, pud.pointY, null);
                }
            }
            catch { }
            // 5) 注入
            var addMethod = typeof(WorldUnitMgr).GetMethod("AddUnit");
            var result = addMethod.Invoke(g.world.unit, new object[] { info });
            MelonLoggerPlaceholder(setCount, skipCount, d.Count);
            return result as WorldUnitBase;
        }

        private static void MelonLoggerPlaceholder(int setCount, int skipCount, int jsonKeys)
        {
            try { Diag("ImportUnit诊断: JSON键=" + jsonKeys + " 写入=" + setCount + " 跳过=" + skipCount); } catch { }
        }

        /// <summary>把 JSON 字典递归合并进已存在的 il2cpp 对象：基础类型直接写；子对象保留原实例继续合并；列表/数组整体重建。
        /// 比整体替换子对象安全（原生指针永远有效）。skip 集合内的顶层键跳过。</summary>
        private static void MergeInto(Dictionary<string, object> node, object target, HashSet<string> skip, ref int setCount, ref int skipCount)
        {
            if (node == null || target == null) return;
            Type t = target.GetType();
            foreach (var kv in node)
            {
                if (kv.Key == "__t") continue;
                if (skip != null && skip.Contains(kv.Key)) { skipCount++; continue; }
                try
                {
                    var p = t.GetProperty(kv.Key);
                    if (p == null || !p.CanWrite) { skipCount++; continue; }
                    var sub = kv.Value as Dictionary<string, object>;
                    if (sub != null && sub.ContainsKey("__t"))
                    {
                        // 子对象：能拿到现有实例就继续合并，拿不到就整体构造
                        object existing = null;
                        try { existing = p.GetValue(target, null); } catch { }
                        if (existing != null)
                        {
                            MergeInto(sub, existing, null, ref setCount, ref skipCount);
                            continue;
                        }
                    }
                    object val = ConvertTo(kv.Value, p.PropertyType);
                    if (val != null) { p.SetValue(target, val, null); setCount++; }
                    else skipCount++;
                }
                catch { skipCount++; }
            }
        }

        /// <summary>原生分配 il2cpp 对象并用 ctor(IntPtr) 包装（兜底：类型无无参构造时）。</summary>
        private static object NewIl2CppRaw(Type t)
        {
            try
            {
                var store = typeof(UnhollowerBaseLib.Il2CppClassPointerStore<>).MakeGenericType(t);
                var ptrProp = store.GetProperty("NativeClassPtr");
                if (ptrProp == null) return null;
                IntPtr klass;
                try { klass = (IntPtr)ptrProp.GetValue(null, null); } catch { return null; }
                if (klass == IntPtr.Zero) return null;
                IntPtr raw = UnhollowerBaseLib.IL2CPP.il2cpp_object_new(klass);
                if (raw == IntPtr.Zero) return null;
                var wrapCtor = t.GetConstructor(new Type[] { typeof(IntPtr) });
                if (wrapCtor == null) return null;
                return wrapCtor.Invoke(new object[] { raw });
            }
            catch { return null; }
        }

        private static object BuildFrom(object node, Type target)
        {
            if (node == null) return null;
            if (target == null) return null;
            // 列表根节点：BuildFrom本体只处理对象节点，列表交给 ConvertTo（含 List/数组/字典构造）
            var rootNodeList = node as List<object>;
            if (rootNodeList != null) return ConvertTo(node, target);
            var d = node as Dictionary<string, object>;
            if (d != null && d.ContainsKey("__t"))
            {
                // 对象节点：按JSON里的类型构造（更准确）
                string tn = d["__t"] as string;
                if (!string.IsNullOrEmpty(tn))
                {
                    var asm = typeof(g).Assembly;
                    Type rt = asm.GetType(tn);
                    if (rt != null) target = rt;
                }
            }
            object inst = null;
            try
            {
                foreach (var c in target.GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 0) { inst = c.Invoke(null); break; }
                }
                // 无无参构造的互操作类型：原生分配 il2cpp 对象后用 ctor(IntPtr) 包装
                if (inst == null) inst = NewIl2CppRaw(target);
                if (inst == null) return null;
            }
            catch { return null; }

            if (d != null)
            {
                foreach (var kv in d)
                {
                    if (kv.Key == "__t") continue;
                    try
                    {
                        var p = target.GetProperty(kv.Key);
                        if (p == null || !p.CanWrite) continue;
                        object val = ConvertTo(kv.Value, p.PropertyType);
                        if (val != null) p.SetValue(inst, val, null);
                    }
                    catch { }
                }
            }
            return inst;
        }

        private static object ConvertTo(object node, Type target)
        {
            if (node == null) return null;
            try
            {
                if (target == typeof(string)) return node is string ? node : Convert.ToString(node);
                if (target == typeof(bool)) return Convert.ToBoolean(node);
                if (target == typeof(int)) return Convert.ToInt32(node);
                if (target == typeof(long)) return Convert.ToInt64(node);
                if (target == typeof(float)) return Convert.ToSingle(node);
                if (target == typeof(double)) return Convert.ToDouble(node);
                if (target == typeof(byte)) return Convert.ToByte(node);
                if (target.IsEnum) return Enum.ToObject(target, Convert.ToInt32(node));

                var d = node as Dictionary<string, object>;
                var l = node as List<object>;
                if (d != null) return BuildFrom(d, target);

                if (l != null)
                {
                    // List<T> / 数组 / Dictionary<K,V>(k,v交替) / Il2CppStringArray
                    string tn = target.Name;
                    if (target.IsGenericType && tn.StartsWith("Dictionary`2"))
                    {
                        object dict = Activator.CreateInstance(target);
                        var args = target.GetGenericArguments();
                        var itemProp = target.GetProperty("Item");
                        for (int i = 0; i + 1 < l.Count; i += 2)
                        {
                            object k = ConvertTo(l[i], args[0]);
                            object v = ConvertTo(l[i + 1], args[1]);
                            if (k != null && v != null)
                                itemProp.SetValue(dict, v, new object[] { k });
                        }
                        return dict;
                    }
                    if (target.IsGenericType && tn.StartsWith("List`1"))
                    {
                        object list = Activator.CreateInstance(target);
                        var elemType = target.GetGenericArguments()[0];
                        var addM = target.GetMethod("Add");
                        foreach (var item in l)
                        {
                            object v = ConvertTo(item, elemType);
                            if (v != null) addM.Invoke(list, new object[] { v });
                        }
                        return list;
                    }
                    if (tn == "Il2CppStringArray" || target == typeof(UnhollowerBaseLib.Il2CppStringArray))
                    {
                        var slist = new List<string>();
                        foreach (var item in l) if (item is string) slist.Add((string)item);
                        return new UnhollowerBaseLib.Il2CppStringArray(slist.ToArray());
                    }
                    if (tn.StartsWith("Il2CppStructArray"))
                    {
                        // 数值数组：仅支持常见整型
                        var ctor = target.GetConstructor(new Type[] { typeof(int) });
                        if (ctor != null)
                        {
                            object arr = ctor.Invoke(new object[] { l.Count });
                            var itemProp = target.GetProperty("Item");
                            for (int i = 0; i < l.Count; i++)
                            {
                                try { itemProp.SetValue(arr, Convert.ChangeType(l[i], typeof(int)), new object[] { i }); } catch { }
                            }
                            return arr;
                        }
                        return null;
                    }
                    if (target.IsArray)
                    {
                        var elem = target.GetElementType();
                        Array a = Array.CreateInstance(elem, l.Count);
                        for (int i = 0; i < l.Count; i++) a.SetValue(Convert.ChangeType(l[i], elem), i);
                        return a;
                    }
                }
            }
            catch { }
            return null;
        }

        // ===== 极简JSON解析（返回 Dictionary<string,object> / List<object> / string / double / bool / null）=====
        private static object ParseJson(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '{')
            {
                pos++;
                var dict = new Dictionary<string, object>();
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
                while (pos < s.Length)
                {
                    SkipWs(s, ref pos);
                    string key = ParseString(s, ref pos);
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ':') pos++;
                    object val = ParseJson(s, ref pos);
                    dict[key] = val;
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == '}') { pos++; break; }
                    break;
                }
                return dict;
            }
            if (c == '[')
            {
                pos++;
                var list = new List<object>();
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                while (pos < s.Length)
                {
                    object val = ParseJson(s, ref pos);
                    list.Add(val);
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == ']') { pos++; break; }
                    break;
                }
                return list;
            }
            if (c == '"') return ParseString(s, ref pos);
            if (c == 't' && pos + 4 <= s.Length && s.Substring(pos, 4) == "true") { pos += 4; return true; }
            if (c == 'f' && pos + 5 <= s.Length && s.Substring(pos, 5) == "false") { pos += 5; return false; }
            if (c == 'n' && pos + 4 <= s.Length && s.Substring(pos, 4) == "null") { pos += 4; return null; }
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E')) pos++;
            if (pos > start)
            {
                double d;
                if (double.TryParse(s.Substring(start, pos - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            }
            return null;
        }

        private static string ParseString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"') { pos++; return ""; }
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') break;
                if (c == '\\' && pos < s.Length)
                {
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(pos, 4), System.Globalization.NumberStyles.HexNumber, null, out code)) sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int pos)
        {
            // 注意：逗号绝不能当空白吞——否则每个对象只解析出第一个键就 break（历史大坑）
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\n' || s[pos] == '\r' || s[pos] == '\t')) pos++;
        }
    }
}
