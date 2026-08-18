using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(MOD_llcJEQ.ModMain), "记录子系统", "1.0.0", "NavysLion")]

namespace MOD_llcJEQ
{
    public class ModMain : MelonMod
    {
        private static HarmonyLib.Harmony harmony;

        public override void OnApplicationLateStart()
        {
            // 数据包检测：ModExportData\Mod_llcJEQ 存在才启用（删除文件夹=禁用，零影响）
            try
            {
                string dataDir = System.IO.Path.Combine(MelonUtils.GameDirectory, "ModExportData", "Mod_llcJEQ");
                if (!System.IO.Directory.Exists(dataDir))
                {
                    MelonLogger.Msg("记录子系统：未检测到数据包，已停用（安装到 ModExportData 后重启生效）");
                    return;
                }
            }
            catch { }

            if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
            harmony = new HarmonyLib.Harmony("MOD_llcJEQ");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            MelonLogger.Msg("记录子系统已加载");
        }

        private bool _chasteSwept = false;
        private int _tick = 0;
        private long _lastSweepTick = 0;

        public override void OnUpdate()
        {
            // 进存载后扫一次忠贞性格（世界未就绪时静默等待，换世界后重扫）
            try
            {
                if (!_chasteSwept)
                {
                    var pu = g.world.playerUnit;
                    var list = g.world.unit.GetUnits(true);
                    if (pu != null && list != null && list.Count > 0)
                    {
                        _chasteSwept = true;
                        _lastSweepTick = DateTime.UtcNow.Ticks;
                        Record.ChasteSweep();
                    }
                }
                else if (g.world == null || g.world.unit == null || g.world.unit.allUnit.Count == 0)
                {
                    _chasteSwept = false; // 退到主菜单/换世界，允许重扫
                }
            }
            catch { _chasteSwept = false; }
            // 女儿续寿：每10秒一轮；每10分钟全量重扫一次（重建名单，兜住出生登记漏网者）
            _tick++;
            if (_tick < 600) return;
            _tick = 0;
            try
            {
                if (_chasteSwept)
                {
                    Record.ProtectedTopUp();
                    if ((DateTime.UtcNow.Ticks - _lastSweepTick) > TimeSpan.TicksPerMinute * 10)
                    {
                        _lastSweepTick = DateTime.UtcNow.Ticks;
                        Record.ChasteSweep();
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>基础扩展</summary>
    public static class Ext
    {
        public const string NS = "记录子系统";
        public static int GetInt(this WorldUnitBase unit, string key) { try { return unit.data.unitData.objData.GetInt(NS, key); } catch { return 0; } }
        public static void SetInt(this WorldUnitBase unit, string key, int value) { try { unit.data.unitData.objData.SetString(NS, key, value); } catch { } }
        public static string GetStr(this WorldUnitBase unit, string key) { try { return unit.data.unitData.objData.GetString(NS, key) ?? ""; } catch { return ""; } }
        public static void SetStr(this WorldUnitBase unit, string key, string value) { try { unit.data.unitData.objData.SetString(NS, key, value); } catch { } }
        public static string GetStrNS(this WorldUnitBase unit, string ns, string key) { try { return unit.data.unitData.objData.GetString(ns, key) ?? ""; } catch { return ""; } }
        public static string GetName(this WorldUnitBase unit) { try { return unit.data.unitData.propertyData.GetName(); } catch { return ""; } }
        public static string GetID(this WorldUnitBase unit) { try { return unit.data.unitData.unitID; } catch { return ""; } }
        public static string Married(this WorldUnitBase unit) { try { return unit.data.unitData.relationData.married ?? ""; } catch { return ""; } }

        /// <summary>是否道侣关系（双向）</summary>
        public static bool IsDaoLv(this WorldUnitBase a, WorldUnitBase b)
        {
            try
            {
                return a.data.GetRelationType(b) == (UnitBothRelationType)6
                    || b.data.GetRelationType(a) == (UnitBothRelationType)6;
            }
            catch { return false; }
        }

        /// <summary>协程：延迟数帧重新激活（游戏刷新会隐藏多余节点）</summary>
        public static System.Collections.IEnumerator KeepVisible(Transform t)
        {
            for (int i = 0; i < 3; i++)
            {
                yield return null;
                if (t != null) t.gameObject.SetActive(true);
            }
        }

        public static UISkyTipEffect AddSkyTip(this GameObject go, string tip, bool isLeftAligen = false)
        {
            try
            {
                UISkyTipEffect e = go.GetComponent<UISkyTipEffect>();
                if (e == null) e = go.AddComponent<UISkyTipEffect>();
                e.InitData(tip, default(Vector3));
                e.isLeftAligen = isLeftAligen;
                return e;
            }
            catch { return null; }
        }
        public static UISkyTipEffect AddSkyTip(this Transform t, string tip, bool isLeftAligen = false)
        {
            return t.gameObject.AddSkyTip(tip, isLeftAligen);
        }
    }

    /// <summary>行房记录核心</summary>
    public static class Record
    {
        public const string K_LEGAL = "jl_legal";
        public const string K_ILLEGAL = "jl_illegal";
        public const string K_LEGAL_NAMES = "jl_legal_names";
        public const string K_ILLEGAL_NAMES = "jl_illegal_names";
        public const string K_DEFILER = "jl_defiler";
        public const string K_ALL_DEFLOWERED = "jl_all_deflowered"; // 历史累计破处名单（含已故，只增不减）
        public const int CHASTE_TRAIT = 19; // 外在性格词条"忠贞"(role_character_name19)
        public const string MASTER_NAME = "杨梦圆"; // 挂靠师傅（闭关期间的救援人），按名字查找，改人改这里

        /// <summary>正当判定：已婚配偶 或 道侣 或 任一方是玩家</summary>
        public static bool IsLegal(WorldUnitBase a, WorldUnitBase b)
        {
            try
            {
                string aid = a.GetID(), bid = b.GetID();
                if (!string.IsNullOrEmpty(a.Married()) && a.Married() == bid) return true;
                if (!string.IsNullOrEmpty(b.Married()) && b.Married() == aid) return true;
                if (a.IsDaoLv(b)) return true;
                var player = g.world.playerUnit;
                if (player != null)
                {
                    string pid = player.data.unitData.unitID;
                    if (pid == aid || pid == bid) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 存量迁移：把失德名单里"当前玩家名"的段（名字:次数）挪到正当名单，计数同步转移。幂等。
        /// </summary>
        public static void MigratePlayerToLegal(WorldUnitBase u)
        {
            try
            {
                var player = g.world.playerUnit;
                if (player == null) return;
                string pName = player.GetName();
                string iln = u.GetStr(K_ILLEGAL_NAMES);
                if (iln.Length == 0 || !iln.Contains(pName)) return;
                int moveCount = 0;
                var keep = new System.Text.StringBuilder();
                foreach (string seg in iln.Split('|'))
                {
                    if (seg.Length == 0) continue;
                    string name = seg; int c = seg.LastIndexOf(':');
                    int cnt = 1;
                    if (c > 0)
                    {
                        name = seg.Substring(0, c);
                        int tr; if (int.TryParse(seg.Substring(c + 1), out tr)) cnt = tr;
                    }
                    if (name == pName) { moveCount += cnt; continue; }
                    if (keep.Length > 0) keep.Append('|');
                    keep.Append(seg);
                }
                if (moveCount == 0) return;
                u.SetStr(K_ILLEGAL_NAMES, keep.ToString());
                // 并入正当名单（已有则取较大次数）
                string ln = u.GetStr(K_LEGAL_NAMES);
                int existCnt = 0;
                var lb = new System.Text.StringBuilder();
                foreach (string seg in ln.Split('|'))
                {
                    if (seg.Length == 0) continue;
                    string name = seg; int c = seg.LastIndexOf(':');
                    int cnt = 1;
                    if (c > 0)
                    {
                        name = seg.Substring(0, c);
                        int tr; if (int.TryParse(seg.Substring(c + 1), out tr)) cnt = tr;
                    }
                    if (name == pName) { existCnt = cnt > existCnt ? cnt : existCnt; continue; }
                    if (lb.Length > 0) lb.Append('|');
                    lb.Append(seg);
                }
                int finalCnt = moveCount > existCnt ? moveCount : existCnt;
                if (lb.Length > 0) lb.Append('|');
                lb.Append(pName).Append(':').Append(finalCnt);
                u.SetStr(K_LEGAL_NAMES, lb.ToString());
                // 计数同步转移
                u.SetInt(K_ILLEGAL, u.GetInt(K_ILLEGAL) - moveCount);
                u.SetInt(K_LEGAL, u.GetInt(K_LEGAL) + moveCount);
            }
            catch { }
        }

        private static string _lastPair = "";
        private static long _lastPairTime = 0;

        /// <summary>行房记录（双方）。双修反馈会为双方各触发一次，1秒内同一对去重</summary>
        public static void OnTrains(WorldUnitBase a, WorldUnitBase b)
        {
            try
            {
                string idA = a.GetID(), idB = b.GetID();
                string pair = string.CompareOrdinal(idA, idB) < 0 ? idA + "|" + idB : idB + "|" + idA;
                long now = DateTime.UtcNow.Ticks;
                if (pair == _lastPair && (now - _lastPairTime) < TimeSpan.TicksPerSecond)
                {
                    return;
                }
                _lastPair = pair;
                _lastPairTime = now;
            }
            catch { }
            _defCache = null; // 行房可能产生新破处记录，立即失效破处统计缓存（下次查询重扫，数字即时准确）
            try { RecordSide(a, b); } catch (Exception) { }
            try { RecordSide(b, a); } catch (Exception) { }
        }

        private static void RecordSide(WorldUnitBase u, WorldUnitBase partner)
        {
            bool legal = IsLegal(u, partner);
            string pname = partner.GetName();

            // 破处者：优先守宫砂子系统字段，未装则自记
            string defiler = u.GetStr(K_DEFILER);
            string cDefiler = u.GetStrNS("守宫砂子系统", "破处者");
            if (string.IsNullOrEmpty(defiler) && string.IsNullOrEmpty(cDefiler))
            {
                u.SetStr(K_DEFILER, pname);
            }

            if (legal)
            {
                u.SetInt(K_LEGAL, u.GetInt(K_LEGAL) + 1);
                AddName(u, K_LEGAL_NAMES, pname);
            }
            else
            {
                u.SetInt(K_ILLEGAL, u.GetInt(K_ILLEGAL) + 1);
                AddName(u, K_ILLEGAL_NAMES, pname);
            }
        }

        /// <summary>
        /// 记录对象名单并计数，存储格式"名1:数1|名2:数2"；旧格式（纯名字）自动升级并从1起计。
        /// 名字里含':'时按最后一个冒号解析，尽量避免冲突。
        /// </summary>
        private static void AddName(WorldUnitBase u, string key, string name)
        {
            string cur = u.GetStr(key);
            string[] parts = cur.Split('|');
            var nb = new System.Text.StringBuilder();
            bool found = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string seg = parts[i];
                if (seg.Length == 0) continue;
                if (seg == name)
                {
                    seg = name + ":2"; found = true;
                }
                else if (seg.StartsWith(name + ":"))
                {
                    int c; found = true;
                    if (int.TryParse(seg.Substring(name.Length + 1), out c)) seg = name + ":" + (c + 1);
                    else seg = name + ":2";
                }
                if (nb.Length > 0) nb.Append('|');
                nb.Append(seg);
            }
            if (!found)
            {
                if (nb.Length > 0) nb.Append('|');
                nb.Append(name).Append(":1");
            }
            u.SetStr(key, nb.ToString());
        }

        /// <summary>解析"名:数"段，旧格式纯名字按1次</summary>
        private static void ParseSeg(string seg, System.Collections.Generic.Dictionary<string, int> dict)
        {
            if (seg.Length == 0) return;
            string name = seg; int cnt = 1;
            int c = seg.LastIndexOf(':');
            if (c > 0)
            {
                int tr;
                if (int.TryParse(seg.Substring(c + 1), out tr)) { name = seg.Substring(0, c); cnt = tr; }
            }
            if (name.Length == 0) return;
            if (!dict.ContainsKey(name) || dict[name] < cnt) dict[name] = cnt;
        }

        /// <summary>游戏日志统计：双修完成日志(12900/13100)按对象计数（日志有保留期限，仅覆盖近期）</summary>
        private static void MergeLogCounts(WorldUnitBase u, System.Collections.Generic.Dictionary<string, int> dict)
        {
            var allAdd = g.world.unitLog.allAddLogData;
            Il2CppSystem.Collections.Generic.List<WorldUnitLogMgr.LogData> logs = null;
            string mid = u.GetID();
            if (allAdd == null || !allAdd.ContainsKey(mid)) return;
            try { logs = allAdd[mid]; } catch { }
            if (logs == null) return;
            foreach (var ld in logs)
            {
                if (ld == null || ld.logData == null || ld.logData.logs == null) continue;
                foreach (var d in ld.logData.logs)
                {
                    if (d == null || d.id == null || d.values == null || d.values.Length == 0) continue;
                    bool hit = false;
                    foreach (int i in d.id) { if (i == 12900 || i == 13100) { hit = true; break; } }
                    if (!hit) continue;
                    string v0 = CleanLogName(d.values[0]);
                    if (string.IsNullOrEmpty(v0)) continue;
                    if (!dict.ContainsKey(v0)) dict[v0] = 0;
                    dict[v0] = dict[v0] + 1;
                }
            }
        }

        /// <summary>
        /// 双修次数排行榜（正当+失德合计）。mod计数与日志统计同源同步增长，取max避免重复计数：
        /// total(n) = max(mod正当(n)+mod失德(n), 日志(n))，旧名单按1并入mod侧。
        /// </summary>
        public static System.Collections.Generic.List<string[]> TopPartners(WorldUnitBase u, int topN)
        {
            var legal = new System.Collections.Generic.Dictionary<string, int>();
            var illegal = new System.Collections.Generic.Dictionary<string, int>();
            foreach (string seg in u.GetStr(K_LEGAL_NAMES).Split('|')) ParseSeg(seg, legal);
            foreach (string seg in u.GetStr(K_ILLEGAL_NAMES).Split('|')) ParseSeg(seg, illegal);
            var mod = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var kv in legal) mod[kv.Key] = kv.Value;
            foreach (var kv in illegal) { if (mod.ContainsKey(kv.Key)) mod[kv.Key] += kv.Value; else mod[kv.Key] = kv.Value; }
            var logs = new System.Collections.Generic.Dictionary<string, int>();
            try { MergeLogCounts(u, logs); } catch { }
            var total = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var kv in mod)
            {
                int lc = logs.ContainsKey(kv.Key) ? logs[kv.Key] : 0;
                total[kv.Key] = kv.Value > lc ? kv.Value : lc;
            }
            foreach (var kv in logs)
            {
                if (!total.ContainsKey(kv.Key)) total[kv.Key] = kv.Value;
            }
            var list = new System.Collections.Generic.List<string[]>();
            foreach (var kv in total) list.Add(new string[] { kv.Key, "" + kv.Value });
            for (int i = 0; i < list.Count - 1 && i < topN; i++)
            {
                int maxJ = i; int maxV = int.Parse(list[i][1]);
                for (int j = i + 1; j < list.Count; j++)
                {
                    int v = int.Parse(list[j][1]);
                    if (v > maxV) { maxV = v; maxJ = j; }
                }
                var tmp = list[i]; list[i] = list[maxJ]; list[maxJ] = tmp;
            }
            var result = new System.Collections.Generic.List<string[]>();
            for (int i = 0; i < list.Count && i < topN; i++) result.Add(list[i]);
            return result;
        }

        /// <summary>按ID查单位：allUnit 与 allUnits 双查，避开互操作TryGetValue的潜在兼容问题</summary>
        private static WorldUnitBase FindUnit(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { if (g.world.unit.allUnit != null && g.world.unit.allUnit.ContainsKey(id)) return g.world.unit.allUnit[id]; } catch { }
            try { if (g.world.unit.allUnits != null && g.world.unit.allUnits.ContainsKey(id)) return g.world.unit.allUnits[id]; } catch { }
            return null;
        }

        // 诊断日志节流：完全相同的消息2秒内不重复打
        private static string _lastMsg = "";
        private static long _lastTime = 0;
        private static void Diag(string msg)
        {
            long now = DateTime.UtcNow.Ticks;
            if (msg == _lastMsg && (now - _lastTime) < TimeSpan.TicksPerSecond * 2) return;
            _lastMsg = msg; _lastTime = now;
            try { MelonLogger.Msg(msg); } catch { }
        }

        /// <summary>
        /// 实时推断破处者（旧存档无记录时的显示兜底，不写入任何数据）：
        /// 路径1：经历中最早的双修完成日志（游戏日志有保留期限 ClearOverMonthLog，仅能覆盖近期；
        ///        双修完成日志 RoleLogLocal id：12900=与{0}双修 / 13100={0}与我双修；13000/13200为被拒绝，排除）
        /// 路径2：年龄最大孩子的父亲（孩子 relationData.parent 数组里不等于母亲本人的那个ID，无需判断性别）
        /// </summary>
        /// <summary>推断结果缓存（面板刷新高频触发，3秒内同单位不重复遍历日志）</summary>
        private static string _cacheId = "";
        private static string _cacheVal = null;
        private static long _cacheTick = 0;

        public static string InferDefiler(WorldUnitBase u, bool verbose = false)
        {
            try
            {
                string id = u.GetID();
                long now = DateTime.UtcNow.Ticks;
                if (id == _cacheId && (now - _cacheTick) < TimeSpan.TicksPerSecond * 3) return _cacheVal;
                string v = InferCore(u, verbose);
                _cacheId = id; _cacheVal = v; _cacheTick = now;
                return v;
            }
            catch { return null; }
        }

        private static string InferCore(WorldUnitBase u, bool verbose)
        {
            string selfId = u.GetID();
            // 路径1：最早双修日志
            try
            {
                string best = null; int bestMonth = int.MaxValue; int itemCount = 0;
                var allAdd = g.world.unitLog.allAddLogData;
                Il2CppSystem.Collections.Generic.List<WorldUnitLogMgr.LogData> logs = null;
                if (allAdd != null && allAdd.ContainsKey(selfId)) { try { logs = allAdd[selfId]; } catch { } }
                if (logs != null)
                {
                    foreach (var ld in logs)
                    {
                        if (ld == null || ld.logData == null) continue;
                        var item = ld.logData;
                        itemCount++;
                        if (item.month >= bestMonth || item.logs == null) continue;
                        foreach (var d in item.logs)
                        {
                            if (d == null || d.id == null || d.values == null || d.values.Length == 0) continue;
                            bool hit = false;
                            foreach (int i in d.id) { if (i == 12900 || i == 13100) { hit = true; break; } }
                            if (!hit) continue;
                            string v0 = CleanLogName(d.values[0]);
                            if (!string.IsNullOrEmpty(v0) && item.month < bestMonth) { bestMonth = item.month; best = v0; }
                        }
                    }
                }
                if (verbose) Diag("[推断] " + u.GetName() + " 路径1: 日志包=" + itemCount + " 命中=" + (best ?? "无"));
                if (!string.IsNullOrEmpty(best)) return best;
            }
            catch (Exception e1) { if (verbose) Diag("[推断] 路径1异常: " + e1.Message); }
            // 路径2：长子女之父
            try
            {
                var rel = u.data.unitData.relationData;
                int c1 = 0, c2 = 0;
                if (rel.children != null) c1 = rel.children.Count;
                if (rel.childrenPrivate != null) c2 = rel.childrenPrivate.Count;
                string eldestId = null; int maxAge = int.MinValue;
                CollectEldest(rel.children, ref eldestId, ref maxAge, verbose);
                CollectEldest(rel.childrenPrivate, ref eldestId, ref maxAge, verbose);
                if (verbose) Diag("[推断] " + u.GetName() + " 路径2: 子女=" + c1 + " 私生=" + c2 + " 长子女=" + (eldestId ?? "无") + " age=" + maxAge);
                if (!string.IsNullOrEmpty(eldestId))
                {
                    WorldUnitBase child = FindUnit(eldestId);
                    if (child != null)
                    {
                        var ps = child.data.unitData.relationData.parent;
                        int pn = (ps != null) ? ps.Length : 0;
                        string all = "";
                        if (ps != null) foreach (string x in ps) all += x + ",";
                        if (verbose) Diag("[推断] 孩子" + eldestId + " parent数=" + pn + " [" + all + "] 自我=" + selfId);
                        if (ps != null)
                        {
                            foreach (string pid in ps)
                            {
                                if (string.IsNullOrEmpty(pid) || pid == selfId) continue;
                                WorldUnitBase father = FindUnit(pid);
                                if (father != null)
                                    return father.GetName();
                                if (verbose) Diag("[推断] 父亲" + pid + "查无此单位");
                            }
                        }
                    }
                    else if (verbose) Diag("[推断] 长子女" + eldestId + "查无此单位");
                }
            }
            catch (Exception e2) { if (verbose) Diag("[推断] 路径2异常: " + e2.Message); }
            return null;
        }

        /// <summary>在候选孩子ID里找年龄最大的（年龄取不到按0算，取不到单位的跳过）</summary>
        private static void CollectEldest(Il2CppSystem.Collections.Generic.List<string> ids, ref string eldestId, ref int maxAge, bool verbose)
        {
            if (ids == null) return;
            foreach (string cid in ids)
            {
                if (string.IsNullOrEmpty(cid)) continue;
                WorldUnitBase c = FindUnit(cid);
                if (c == null) { if (verbose) Diag("[推断] 孩子" + cid + "查无此单位"); continue; }
                int age = 0;
                try { age = c.data.unitData.propertyData.age; } catch { }
                if (age > maxAge) { maxAge = age; eldestId = cid; }
            }
        }

        /// <summary>破处者取值链：守宫砂字段 > 自记字段 > 实时推断</summary>
        public static string GetDefiler(WorldUnitBase u)
        {
            string d = u.GetStrNS("守宫砂子系统", "破处者");
            if (string.IsNullOrEmpty(d)) d = u.GetStr(K_DEFILER);
            if (string.IsNullOrEmpty(d)) d = InferDefiler(u);
            return d;
        }

        /// <summary>
        /// 破处统计缓存：女性破处者名字 → 名单。行房事件立即失效（OnTrains 置空），
        /// 兜底TTL 10分钟；重名角色可能略有误差。
        /// </summary>
        private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> _defCache = null;
        private static long _defCacheTick = 0;

        private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> DefloweredCache()
        {
            long now = DateTime.UtcNow.Ticks;
            if (_defCache != null && (now - _defCacheTick) < TimeSpan.TicksPerSecond * 600) return _defCache;
            var dict = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
            try
            {
                var list = g.world.unit.GetUnits(true);
                if (list != null)
                {
                    foreach (WorldUnitBase w in list)
                    {
                        if (w == null) continue;
                        try
                        {
                            if ((int)w.data.unitData.propertyData.sex != 2) continue;
                            string d = w.GetStrNS("守宫砂子系统", "破处者");
                            if (string.IsNullOrEmpty(d)) d = w.GetStr(K_DEFILER);
                            if (string.IsNullOrEmpty(d)) d = InferDefiler(w); // 含历史推断（最早双修日志/长子女之父）
                            if (string.IsNullOrEmpty(d)) continue;
                            if (!dict.ContainsKey(d)) dict[d] = new System.Collections.Generic.List<string>();
                            dict[d].Add(w.GetName());
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _defCache = dict; _defCacheTick = now;
            return dict;
        }

        /// <summary>该角色（男性）破处的女性名单</summary>
        public static System.Collections.Generic.List<string> GetDefloweredList(WorldUnitBase u)
        {
            var dict = DefloweredCache();
            string name = u.GetName();
            System.Collections.Generic.List<string> list;
            if (name != null && dict.TryGetValue(name, out list)) return list;
            return new System.Collections.Generic.List<string>();
        }

        /// <summary>玩家unitID</summary>
        public static string PlayerId()
        {
            try { var pu = g.world.playerUnit; return pu != null ? pu.data.unitData.unitID : null; } catch { return null; }
        }

        private static string _masterId = null; // 师傅unitID（全量扫描时按名字刷新，查不到为null）

        public static string MasterIdRef { get { return _masterId; } }

        /// <summary>按名字刷新师傅ID（找不到置null，拜师静默跳过）</summary>
        public static string RefreshMasterId()
        {
            try
            {
                var list = g.world.unit.GetUnits(true);
                if (list != null)
                {
                    foreach (WorldUnitBase u in list)
                    {
                        if (u == null) continue;
                        string n;
                        try { n = u.GetName(); } catch { continue; }
                        if (MASTER_NAME == n) { _masterId = u.GetID(); return _masterId; }
                    }
                }
            }
            catch { }
            _masterId = null;
            return null;
        }

        /// <summary>
        /// 双边拜师：她拜师傅（master），师傅收她（student），并双向灌满亲密度。
        /// 亲密度每次调用都重灌（10分钟扫描周期=感情保鲜周期），防AI因疏远而恩断义绝；
        /// 已断绝的关系会被 Contains 检查自动重新绑定。
        /// </summary>
        public static void AssignMaster(WorldUnitBase u)
        {
            try
            {
                if (string.IsNullOrEmpty(_masterId)) return;
                string uid = u.GetID();
                if (uid == _masterId) return;
                var master = FindUnitById(_masterId);
                if (master == null) { _masterId = null; return; }
                var rel = u.data.unitData.relationData;
                if (rel.master != null && !rel.master.Contains(_masterId)) rel.master.Add(_masterId);
                var mrel = master.data.unitData.relationData;
                if (mrel.student != null && !mrel.student.Contains(uid)) mrel.student.Add(uid);
                try { if (rel.intimToUnit != null) rel.intimToUnit[_masterId] = 300f; } catch { }
                try { if (mrel.intimToUnit != null) mrel.intimToUnit[uid] = 300f; } catch { }
            }
            catch { }
        }

        /// <summary>永生保护名单缓存（道侣/妻子/女儿，全量扫描时收集，续寿用）</summary>
        private static readonly System.Collections.Generic.List<string> _protected = new System.Collections.Generic.List<string>();

        /// <summary>寿元续费：余量不足600月(50年)时补到600月</summary>
        public static void TopUpLife(object pdObj, int age, int life)
        {
            try
            {
                var pd = (DataUnit.PropertyData)pdObj;
                if (pd != null && life - age < 600) pd.life = age + 600;
            }
            catch { }
        }

        /// <summary>
        /// 清洗游戏日志里的名字标记：格式 @q_名字 (关系)|unitID@。
        /// 优先用unitID反查当前名字（天然归一改名史），查不到再剥格式取内部名。
        /// </summary>
        public static string CleanLogName(string v)
        {
            try
            {
                if (string.IsNullOrEmpty(v) || !v.StartsWith("@") || !v.EndsWith("@")) return v;
                string inner = v.Substring(1, v.Length - 2); // q_名字 (关系)|unitID
                int bar = inner.LastIndexOf('|');
                if (bar > 0 && bar < inner.Length - 1)
                {
                    string uid = inner.Substring(bar + 1);
                    WorldUnitBase w = FindUnitById(uid);
                    if (w != null) return w.GetName();
                }
                if (bar > 0) inner = inner.Substring(0, bar);      // q_名字 (关系)
                int sp = inner.IndexOf(" (");
                if (sp > 0) inner = inner.Substring(0, sp);         // q_名字
                if (inner.Length > 2 && inner[1] == '_') inner = inner.Substring(2); // 名字
                return inner;
            }
            catch { return v; }
        }

        /// <summary>解析"名:次|名:次"名单：存在非指定名字的对象则true</summary>
        private static bool HasOtherName(string data, string keepName)
        {
            if (string.IsNullOrEmpty(data)) return false;
            foreach (string seg in data.Split('|'))
            {
                if (seg.Length == 0) continue;
                string name = seg; int c = seg.LastIndexOf(':');
                if (c > 0) name = seg.Substring(0, c);
                if (name.Length > 0 && name != keepName) return true;
            }
            return false;
        }

        /// <summary>日志证据：存在非玩家的双修完成记录(12900/13100)则true</summary>
        private static bool LogHasOther(WorldUnitBase u, string keepName)
        {
            try
            {
                var allAdd = g.world.unitLog.allAddLogData;
                string mid = u.GetID();
                if (allAdd == null || !allAdd.ContainsKey(mid)) return false;
                Il2CppSystem.Collections.Generic.List<WorldUnitLogMgr.LogData> logs = null;
                try { logs = allAdd[mid]; } catch { }
                if (logs == null) return false;
                foreach (var ld in logs)
                {
                    if (ld == null || ld.logData == null || ld.logData.logs == null) continue;
                    foreach (var d in ld.logData.logs)
                    {
                        if (d == null || d.id == null || d.values == null || d.values.Length == 0) continue;
                        bool hit = false;
                        foreach (int i in d.id) { if (i == 12900 || i == 13100) { hit = true; break; } }
                        if (!hit) continue;
                        string v0 = d.values[0];
                        if (!string.IsNullOrEmpty(v0) && v0 != keepName) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>贞洁资格：双修统计（mod计数+游戏日志，取max合并）的全部对象里只有玩家或为空。破处者/孩子不作证据。</summary>
        private static bool ChasteForPlayer(WorldUnitBase u, string pid, string pName)
        {
            var all = TopPartners(u, 999);
            foreach (string[] kv in all)
            {
                if (kv[0] != pName) return false;
            }
            return true;
        }

        /// <summary>
        /// 永生保护：道侣/妻子/玩家女儿（含私生），且贞洁（只与玩家双修过或从未双修）。
        /// 女儿额外挂忠贞性格。通过者续寿并入名单。返回1=受保护。
        /// </summary>
        public static int ProtectUnit(WorldUnitBase u)
        {
            try
            {
                if ((int)u.data.unitData.propertyData.sex != 2) return 0;
                var player = g.world.playerUnit;
                if (player == null) return 0;
                string pid = player.data.unitData.unitID;
                string pName = player.GetName();
                var rel = u.data.unitData.relationData;
                bool isDaughter = false, isKin = false;
                if (!string.IsNullOrEmpty(rel.married) && rel.married == pid) isKin = true;
                if (!isKin && u.IsDaoLv(player)) isKin = true;
                if (!isKin && rel.parent != null)
                {
                    foreach (string p in rel.parent) { if (p == pid) { isDaughter = true; isKin = true; break; } }
                }
                if (!isKin) return 0;
                if (!ChasteForPlayer(u, pid, pName)) return 0;
                var pd = u.data.unitData.propertyData;
                if (isDaughter && (int)pd.outTrait1 != CHASTE_TRAIT) pd.outTrait2 = CHASTE_TRAIT;
                TopUpLife(pd, pd.age, pd.life);
                AssignMaster(u);
                lock (_protected)
                {
                    string id = u.GetID();
                    if (!_protected.Contains(id)) _protected.Add(id);
                }
                return 1;
            }
            catch { }
            return 0;
        }

        /// <summary>是否永生名单成员（Die拦截用）</summary>
        public static bool IsProtectedUnit(WorldUnitBase u)
        {
            try
            {
                if (u == null) return false;
                string id = u.GetID();
                lock (_protected) { return _protected.Contains(id); }
            }
            catch { return false; }
        }

        /// <summary>登记受保护ID（出生钩子调用；全量扫描也会重建）</summary>
        public static void AddProtected(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_protected) { if (!_protected.Contains(id)) _protected.Add(id); }
        }

        /// <summary>周期续寿：按保护名单缓存逐个补寿元（资格重验由10分钟全量扫描负责）</summary>
        public static void ProtectedTopUp()
        {
            try
            {
                lock (_protected)
                {
                    for (int i = _protected.Count - 1; i >= 0; i--)
                    {
                        WorldUnitBase u = FindUnitById(_protected[i]);
                        if (u == null) { _protected.RemoveAt(i); continue; }
                        var pd = u.data.unitData.propertyData;
                        TopUpLife(pd, pd.age, pd.life);
                    }
                }
            }
            catch { }
        }

        public static WorldUnitBase FindUnitById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { if (g.world.unit.allUnit.ContainsKey(id)) return g.world.unit.allUnit[id]; } catch { }
            try { if (g.world.unit.allUnits.ContainsKey(id)) return g.world.unit.allUnits[id]; } catch { }
            return null;
        }

        /// <summary>进存载后全量扫描一次：把存量女儿的外在性格改为忠贞（新生女儿由出生钩子处理）</summary>
        public static void ChasteSweep()
        {
            try
            {
                int n = 0;
                lock (_protected) { _protected.Clear(); }
                RefreshMasterId();
                var list = g.world.unit.GetUnits(true);
                if (list != null)
                {
                    foreach (WorldUnitBase u in list)
                    {
                        if (u == null) continue;
                        MigratePlayerToLegal(u);
                        n += ProtectUnit(u);
                    }
                }
                MelonLogger.Msg("[永生] 世界扫描完成，受保护人数=" + n);
            }
            catch (Exception e) { MelonLogger.Msg("[忠贞] 扫描异常: " + e.Message); }
        }

        /// <summary>
        /// 历史累计破处名单（含已故）：实时扫描结果合并进男方自存的永久名单，只增不减。
        /// 死亡单位会被世界移除导致实时扫描漏掉，累计名单保证"战绩是史书不是人口普查"。
        /// </summary>
        public static System.Collections.Generic.List<string> GetDefloweredTotalList(WorldUnitBase male)
        {
            var all = new System.Collections.Generic.List<string>();
            string stored = male.GetStr(K_ALL_DEFLOWERED);
            if (stored.Length > 0)
            {
                foreach (string n in stored.Split('|'))
                {
                    if (n.Length > 0 && !all.Contains(n)) all.Add(n);
                }
            }
            var dict = DefloweredCache();
            string name = male.GetName();
            System.Collections.Generic.List<string> current;
            if (name != null && dict.TryGetValue(name, out current) && current != null)
            {
                foreach (string n in current)
                {
                    if (n.Length > 0 && !all.Contains(n)) all.Add(n);
                }
            }
            string merged = all.Count > 0 ? string.Join("|", all.ToArray()) : "";
            if (merged != stored) male.SetStr(K_ALL_DEFLOWERED, merged);
            return all;
        }

        /// <summary>悬停详情</summary>
        public static string GetDetail(WorldUnitBase u)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("性记录：正当 ").Append(u.GetInt(K_LEGAL)).Append(" 次 / 失德 ").Append(u.GetInt(K_ILLEGAL)).Append(" 次");
            string d = GetDefiler(u);
            sb.Append("\n破处者：").Append(string.IsNullOrEmpty(d) ? "无" : d);
            string ln = u.GetStr(K_LEGAL_NAMES);
            if (ln.Length > 0) sb.Append("\n正当对象：").Append(ln);
            string iln = u.GetStr(K_ILLEGAL_NAMES);
            if (iln.Length > 0) sb.Append("\n失德对象：").Append(iln);
            var top = TopPartners(u, 3);
            if (top.Count > 0)
            {
                sb.Append("\n双修前三：");
                for (int i = 0; i < top.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(top[i][0]).Append(':').Append(top[i][1]);
                }
            }
            try
            {
                if ((int)u.data.unitData.propertyData.sex != 2)
                {
                    var dl = GetDefloweredTotalList(u);
                    sb.Append("\n破处：").Append(dl.Count).Append(" 人");
                    if (dl.Count > 0) sb.Append("（").Append(string.Join("、", dl.ToArray())).Append("）");
                }
            }
            catch { }
            return sb.ToString();
        }
    }

    /// <summary>出生挂忠贞：新生的玩家女儿（含私生）直接改外在性格为忠贞（存量女儿由进世界全量扫描处理）</summary>
    [HarmonyPatch(typeof(ConfRoleAttributeCoefficient), "RandomInitNPCUnit")]
    public class BirthChastePatch
    {
        [HarmonyPostfix]
        private static DataUnit.UnitInfoData Postfix(DataUnit.UnitInfoData __result)
        {
            try
            {
                if (__result == null || __result.propertyData == null) return __result;
                if (__result.propertyData.sex != (UnitSexType)2) return __result;
                string pid = Record.PlayerId();
                if (string.IsNullOrEmpty(pid)) return __result;
                var ps = __result.relationData.parent;
                if (ps == null) return __result;
                foreach (string p in ps)
                {
                    if (p == pid)
                    {
                        try { if ((int)__result.propertyData.outTrait1 != Record.CHASTE_TRAIT) __result.propertyData.outTrait2 = Record.CHASTE_TRAIT; } catch { }
                        try
                        {
                            var pd = __result.propertyData;
                            if (pd.life - pd.age < 600) pd.life = pd.age + 600;
                        }
                        catch { }
                        Record.AddProtected(__result.unitID);
                        try
                        {
                            if (Record.RefreshMasterId() != null)
                            {
                                var rl = __result.relationData;
                                string mid = Record.MasterIdRef;
                                if (rl.master != null && !rl.master.Contains(mid)) rl.master.Add(mid);
                                var mst = Record.FindUnitById(mid);
                                if (mst != null)
                                {
                                    string nid = __result.unitID;
                                    var mrel = mst.data.unitData.relationData;
                                    if (mrel.student != null && !mrel.student.Contains(nid)) mrel.student.Add(nid);
                                }
                            }
                        }
                        catch { }
                        break;
                    }
                }
            }
            catch { }
            return __result;
        }
    }

    /// <summary>死亡拦截：永生名单成员跳过死亡流程（防被杀，兼作寿终的最终防线）</summary>
    [HarmonyPatch(typeof(WorldUnitBase), "Die")]
    public class DieGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WorldUnitBase __instance)
        {
            try
            {
                if (Record.IsProtectedUnit(__instance))
                {
                    try { MelonLogger.Msg("[永生] 拦截死亡：" + __instance.GetName()); } catch { }
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(WorldUnitBase), "DieSecret")]
    public class DieGuardSecretPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WorldUnitBase __instance)
        {
            try
            {
                if (Record.IsProtectedUnit(__instance))
                {
                    try { MelonLogger.Msg("[永生] 拦截暗杀：" + __instance.GetName()); } catch { }
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>双修请求反馈（行房核心入口）</summary>
    [HarmonyPatch(typeof(UnitActionFeedback1031), "OnCreate")]
    public class TrainsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UnitActionFeedback1031 __instance)
        {
            try
            {
                if (__instance == null || __instance.unit == null || __instance.trainsUnit == null) return;
                if (__instance.state != 1) return;
                Record.OnTrains(__instance.unit, __instance.trainsUnit);
            }
            catch (Exception) { }
        }
    }

    /// <summary>NPC 信息面板：行房统计 + 悬停详情</summary>
    [HarmonyPatch(typeof(UINPCInfoProperty), "UpdateUI")]
    public class NpcInfoPatch
    {
        [HarmonyPrefix]
        public static void Prefix(UINPCInfoProperty __instance)
        {
            try
            {
                if (__instance == null || __instance.nPCInfo == null || __instance.nPCInfo.unit == null) return;
                var unit = __instance.nPCInfo.unit;
                Transform root = __instance.goInfoRoot.transform;
                Transform item = root.Find("记录");
                if (item == null)
                {
                    item = UnityEngine.Object.Instantiate<Transform>(root.GetChild(0), root, false);
                    item.name = "记录";
                }
                UiHelper.UpdateItem(item, unit, root.childCount > 5 ? root.GetChild(5) : root.GetChild(0));
            }
            catch { }
        }
    }

    /// <summary>玩家信息面板：行房统计 + 悬停详情</summary>
    [HarmonyPatch(typeof(UIPlayerInfo), "Init")]
    public class PlayerInfoPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UIPlayerInfo __instance)
        {
            try
            {
                if (__instance == null || __instance.unit == null) return;
                var unit = __instance.unit;
                Transform root = __instance.uiProperty.goInfoRoot.transform;
                Transform item = root.Find("记录");
                if (item == null)
                {
                    item = UnityEngine.Object.Instantiate<Transform>(root.GetChild(0), root, false);
                    item.name = "记录";
                }
                UiHelper.UpdateItem(item, unit, root.childCount > 1 ? root.GetChild(1) : null);
            }
            catch { }
        }
    }

    /// <summary>行房条目 UI 助手</summary>
    internal static class UiHelper
    {
        public static void UpdateItem(Transform item, WorldUnitBase unit, Transform refItem)
        {
            try
            {
                // 结构：0=标签Text 1=Image 2=数值Text
                Transform t1 = item.GetChild(0);
                Text l1 = t1 != null ? t1.GetComponent<Text>() : null;
                if (l1 != null)
                {
                    l1.text = "记录：";
                    // 对齐参考条目（如声望栏）的标签位置与对齐方式
                    try
                    {
                        if (refItem != null)
                        {
                            Transform refT = refItem.GetChild(0);
                            Text refText = refT != null ? refT.GetComponent<Text>() : null;
                            RectTransform rr = refT != null ? refT.GetComponent<RectTransform>() : null;
                            if (refText != null) l1.alignment = refText.alignment;
                            if (rr != null && l1.rectTransform != null)
                            {
                                l1.rectTransform.anchoredPosition = rr.anchoredPosition;
                                l1.rectTransform.sizeDelta = rr.sizeDelta;
                            }
                        }
                    }
                    catch { }
                }
                // 数值文本：取最后一个子节点（NPC面板结构 0标签/1图/2数值；玩家面板 0标签/1数值）
                Transform t2 = item.childCount > 0 ? item.GetChild(item.childCount - 1) : null;
                Text l2 = t2 != null ? t2.GetComponent<Text>() : null;
                if (l2 != null) l2.text = string.Format("正{0}·失{1}", unit.GetInt(Record.K_LEGAL), unit.GetInt(Record.K_ILLEGAL));
                item.gameObject.SetActive(true);
                MelonCoroutines.Start(Ext.KeepVisible(item));
                item.AddSkyTip(Record.GetDetail(unit), true);
                // 破处者行：直接显示，无需悬停
                try
                {
                    Transform root2 = item.parent;
                    if (root2 != null)
                    {
                        Transform item2 = root2.Find("破处");
                        if (item2 == null)
                        {
                            item2 = UnityEngine.Object.Instantiate<Transform>(root2.GetChild(0), root2, false);
                            item2.name = "破处";
                        }
                        Transform tb1 = item2.GetChild(0);
                        Text lb1 = tb1 != null ? tb1.GetComponent<Text>() : null;
                        bool isFemale = false;
                        try { isFemale = (int)unit.data.unitData.propertyData.sex == 2; } catch { }
                        if (lb1 != null) lb1.text = isFemale ? "破处者：" : "破处数：";
                        Transform tb2 = item2.childCount > 0 ? item2.GetChild(item2.childCount - 1) : null;
                        Text lb2 = tb2 != null ? tb2.GetComponent<Text>() : null;
                        if (lb2 != null)
                        {
                            if (isFemale)
                            {
                                string dv = Record.GetDefiler(unit);
                                lb2.text = string.IsNullOrEmpty(dv) ? "无" : dv;
                            }
                            else
                            {
                                lb2.text = Record.GetDefloweredTotalList(unit).Count + " 人";
                            }
                        }
                        item2.gameObject.SetActive(true);
                        MelonCoroutines.Start(Ext.KeepVisible(item2));
                        // 双修前三行：名字:次数
                        Transform item3 = root2.Find("前三");
                        if (item3 == null)
                        {
                            item3 = UnityEngine.Object.Instantiate<Transform>(root2.GetChild(0), root2, false);
                            item3.name = "前三";
                        }
                        Transform tc1 = item3.GetChild(0);
                        Text lc1 = tc1 != null ? tc1.GetComponent<Text>() : null;
                        if (lc1 != null) lc1.text = "双修前三：";
                        Transform tc2 = item3.childCount > 0 ? item3.GetChild(item3.childCount - 1) : null;
                        Text lc2 = tc2 != null ? tc2.GetComponent<Text>() : null;
                        if (lc2 != null)
                        {
                            var top = Record.TopPartners(unit, 3);
                            if (top.Count == 0) lc2.text = "无";
                            else
                            {
                                var tb = new System.Text.StringBuilder();
                                for (int i = 0; i < top.Count; i++)
                                {
                                    if (i > 0) tb.Append(' ');
                                    tb.Append(top[i][0]).Append(':').Append(top[i][1]);
                                }
                                lc2.text = tb.ToString();
                            }
                        }
                        item3.gameObject.SetActive(true);
                        MelonCoroutines.Start(Ext.KeepVisible(item3));
                    }
                }
                catch { }
            }
            catch { }
        }
    }
}