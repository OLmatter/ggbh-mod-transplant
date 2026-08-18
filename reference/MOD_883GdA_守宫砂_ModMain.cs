using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(MOD_883GdA.ModMain), "守宫砂", "1.0.0", "NavysLion")]

namespace MOD_883GdA
{
    public class ModMain : MelonMod
    {
        private static HarmonyLib.Harmony _harmony;

        public override void OnApplicationLateStart()
        {
            // 数据包检测：ModExportData\Mod_883GdA 存在才启用（删除文件夹=禁用，零影响）
            try
            {
                string dataDir = System.IO.Path.Combine(MelonUtils.GameDirectory, "ModExportData", "Mod_883GdA");
                if (!System.IO.Directory.Exists(dataDir))
                {
                    MelonLogger.Msg("守宫砂：未检测到数据包，已停用（安装到 ModExportData 后重启生效）");
                    return;
                }
            }
            catch { }

            if (_harmony != null) { _harmony.UnpatchSelf(); _harmony = null; }
            _harmony = new HarmonyLib.Harmony("MOD_883GdA");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            MelonLogger.Msg("守宫砂已加载");
        }
    }

    /// <summary>
    /// 守宫砂：创建之初随女性角色出生，首次行房即破处消失，死亡复活不恢复。
    /// 数据存储：objData("守宫砂子系统", "破处者")
    /// </summary>
    public static class Chastity
    {
        public const string NS = "守宫砂子系统";
        public const string KEY_DEFLOWER = "破处者";
        public const string KEY_LOG_DONE = "破处日志已写";
        public const string KEY_LOG = "chastity_deflower";
        public const int VIRGIN_LUCK = -1826968862;

        private static string ReadStr(WorldUnitBase u, string key)
        {
            try { return u.data.unitData.objData.GetString(NS, key) ?? ""; } catch { return ""; }
        }

        private static void WriteStr(WorldUnitBase u, string key, string value)
        {
            try { u.data.unitData.objData.SetString(NS, key, value); } catch { }
        }

        private static string UnitName(WorldUnitBase u)
        {
            try { return u.data.unitData.propertyData.GetName(); } catch { return ""; }
        }

        private static bool IsFemale(WorldUnitBase u)
        {
            try { return u.data.unitData.propertyData.sex == (UnitSexType)2; } catch { return false; }
        }

        private static void RemoveLuck(WorldUnitBase u, int luckId)
        {
            try { u.CreateAction(new UnitActionLuckDel(luckId), false); } catch { }
        }

        /// <summary>出生时添加守宫砂：仅女性、未婚、无子女。先清后加，保证恰好一个</summary>
        public static void ApplyAtBirth(DataUnit.UnitInfoData info)
        {
            try
            {
                if (info == null || info.propertyData.sex != (UnitSexType)2) return;
                // 已婚或有子女则不加
                string married = info.relationData.married ?? "";
                if (!string.IsNullOrEmpty(married)) return;
                if (info.relationData.children != null && info.relationData.children.Count > 0) return;
                if (info.relationData.childrenPrivate != null && info.relationData.childrenPrivate.Count > 0) return;
                // 先清后加：无论出生流程调用几次，最终恰好一个
                try { info.propertyData.DelAddLuck(VIRGIN_LUCK); } catch { }
                var luck = new DataUnit.LuckData();
                luck.id = VIRGIN_LUCK;
                luck.duration = -1;
                info.propertyData.AddAddLuck(luck);
            }
            catch { }
        }

        /// <summary>行房后处理：女性且未破处则破处</summary>
        public static void OnSex(WorldUnitBase a, WorldUnitBase b)
        {
            try { DeflowerIfVirgin(a, b); } catch { }
            try { DeflowerIfVirgin(b, a); } catch { }
        }

        private static void DeflowerIfVirgin(WorldUnitBase u, WorldUnitBase partner)
        {
            try
            {
                if (!IsFemale(u)) return;
                if (ReadStr(u, KEY_DEFLOWER).Length > 0) return;
                RemoveAllVirginLuck(u);
                WriteStr(u, KEY_DEFLOWER, UnitName(partner));
                if (ReadStr(u, KEY_LOG_DONE).Length == 0)
                {
                    WriteStr(u, KEY_LOG_DONE, "1");
                    WriteDeflowerLog(u, partner);
                }
            }
            catch { }
        }

        /// <summary>移除全部守宫砂（按当前持有数逐个移除，防御重复残留）</summary>
        private static void RemoveAllVirginLuck(WorldUnitBase u)
        {
            try
            {
                int count = 0;
                foreach (var luck in u.allLuck)
                {
                    if (luck.luckData.id == VIRGIN_LUCK) count++;
                }
                for (int i = 0; i < count; i++)
                {
                    RemoveLuck(u, VIRGIN_LUCK);
                }
            }
            catch { }
        }

        private static void WriteDeflowerLog(WorldUnitBase u, WorldUnitBase partner)
        {
            try
            {
                string name = UnitName(partner);
                g.world.unitLog.AddLogData(u, KEY_LOG, new string[] { name }, null);
            }
            catch { }
        }
    }

    /// <summary>双修反馈（行房触发点）</summary>
    [HarmonyPatch(typeof(UnitActionFeedback1031), "OnCreate")]
    public class SexPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UnitActionFeedback1031 __instance)
        {
            try
            {
                if (__instance == null || __instance.unit == null || __instance.trainsUnit == null) return;
                if (__instance.state != 1) return;
                Chastity.OnSex(__instance.unit, __instance.trainsUnit);
            }
            catch { }
        }
    }

    /// <summary>NPC 出生：添加守宫砂</summary>
    [HarmonyPatch(typeof(ConfRoleAttributeCoefficient), "RandomInitNPCUnit")]
    public class BirthPatch
    {
        [HarmonyPostfix]
        private static DataUnit.UnitInfoData Postfix(DataUnit.UnitInfoData __result)
        {
            Chastity.ApplyAtBirth(__result);
            return __result;
        }
    }
}
