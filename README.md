# 鬼谷八荒（Tale of Immortal）Mod 开发实战：NPC 跨存档移植全记录

> 目标：把 2 号存档（1288 年）里的 NPC「钟青曼」（3970 岁，元婴，74 条生涯大事 + 68 条人物故事）完整移植到 3 号存档（1537 年），属性/技能/法宝/亲族/道心/一生大事全部保留，年龄按"从出生算起"修正为 4219 岁，并追加一条自定义穿越叙事。
>
> 本文记录完整方法论、游戏数据模型逆向结论、以及踩过的每一个坑。适用于 MelonLoader 0.5.4 + UnhollowerBaseLib 0.4.18（Il2Cpp 互操作）环境。

---

## 一、环境与工具链

| 组件 | 版本/路径 | 用途 |
|---|---|---|
| 游戏 | Steam 鬼谷八荒 (AppID 1468810) | 目标 |
| MelonLoader | 0.5.4 | mod 加载器 |
| UnhollowerBaseLib | 0.4.18 | Il2Cpp 互操作 |
| 0Harmony | 随 MelonLoader | 方法钩子 |
| csc（Roslyn） | NuGet 下载 | 编译（**系统自带 csc 是 C#5，读不了互操作元数据**） |
| 游戏互操作 DLL | `MelonLoader/Il2CppAssemblies/` 拷出 | 编译引用 + 离线反射 |

**编译命令模板：**

```cmd
csc.exe -nologo -target:library -optimize+ -out:MOD_xxx.dll ^
 -reference:<dir>\Assembly-CSharp.dll ^
 -reference:<dir>\Il2Cppmscorlib.dll -reference:<dir>\Il2CppSystem.dll ^
 -reference:<dir>\Il2CppSystem.Core.dll ^
 -reference:<dir>\MelonLoader.dll -reference:<dir>\0Harmony.dll ^
 -reference:<dir>\UnhollowerBaseLib.dll -reference:<dir>\UnhollowerRuntimeLib.dll ^
 -reference:<dir>\UnityEngine.CoreModule.dll ... ModMain.cs
```

**本地调试 harness（强烈推荐）**：把纯逻辑代码（如 JSON 解析器）提取成独立 .cs，用 csc 编译成 exe 直接对真实数据文件跑——**不需要启动游戏就能复现和验证 bug**。本项目最大的一个 bug（见坑 #1）就是这样定位的。

---

## 二、游戏运行时对象模型（反射逆向结论）

全局单例 `g`（全局命名空间的静态类）：

```
g.world : WorldMgr        → g.world.unit (WorldUnitMgr 单位字典)
                          → g.world.unitLog (WorldUnitLogMgr 日志缓冲)
                          → g.world.run (roundMonth 世界绝对月份, AddDay 推进时间)
                          → g.world.playerUnit (玩家)
g.data  : DataMgr         → g.data.dataUnitLog (DataUnitLog 日志持久层!)
g.conf  : ConfMgr         → g.conf.roleAttributeCoefficient (NPC工厂)
g.events / g.timer / g.res / g.sounds ...
```

> 注意：`DataMgr`/`ConfMgr` **没有静态单例属性**，只能通过 `g.data`/`g.conf` 访问。运行时探测写法见 `src/UnitIO.cs` 的 `FindDataMgr`。

单位数据 `WorldUnitBase.data.unitData : DataUnit+UnitInfoData`，关键字段：

| 字段 | 说明 |
|---|---|
| `propertyData` | 属性（age/life 以**月**为单位，name 是 Il2CppStringArray，outTrait1/2 外在性格） |
| `relationData` | 亲缘（parent/children/childrenPrivate/master/student/married/lover） |
| `objData` | mod 自定义键值存储（`GetString/SetString(ns, key)`） |
| `heart` | 道心 |
| `skillLeft/skillRight/ultimate/abilitys/props/equips` | 技能/法宝/道具 |

---

## 三、日志/事件系统深度解析（本文核心）

### 3.1 两级存储

```
【会话缓冲】 g.world.unitLog.allAddLogData / allAddVitalLogData
             Dictionary<unitID, List<WorldUnitLogMgr.LogData>>
             只存"本轮新增"，读档后为空 —— 只适合当月写入，不适合读取历史！

【持久层】   g.data.dataUnitLog.data.allLog
             Dictionary<unitID, DataUnitLog.LogData>
             历史日志真身。UI（人物故事/生涯大事页）只读这里。
```

**月结算流水线**（游戏原生）：

```
AddLogData(unit, item) ──→ 会话缓冲
       ↓ UpdateLogData()（缓冲→当月暂存 curMonthLog）
       ↓ WriteLogData(allUnit, 缓冲, 缓冲, month)（暂存→持久层）
       ↓ 存档时 SaveLogData() 兜底合并
```

### 3.2 LogData 的双层结构：raw 串 vs 惰性视图

`DataUnitLog.LogData`（每个单位一个）：

```
allLog       : List<Il2CppStringArray>   ← 人物故事 raw（真身，存档序列化它）
allVitalLog  : List<Il2CppStringArray>   ← 生涯大事 raw
allLogData       : List<LogItemData>     ← 惰性视图（UI 读这个）
allVitalLogData  : List<LogItemData>     ← lastUpdateXxxMonth != 当前月时从 raw 重建
```

**raw 条目格式（实测）**：2 元素字符串数组

```
[0] = 月份（十进制字符串，如 "114"）
[1] = 内容串："{month}&Q{日志id}&A{参数1}&A{参数2}&...&A&"
      例: "114&Q4800&A@q_熊光耀|X9aRc8@&A&"
      @q_名字|unitID@ 是人物引用占位符，@w_..@ 物品，@e_..@ 气运
```

`LogItemData.DataToString()` 生成内容串（含月份首字段），`StringToData(String)` 反解。

### 3.3 月份盖章机制（大坑）

**任何合并路径（WriteLogData/SaveLogData）都会用"当前月"重写条目月份**——游戏的自然场景里日志都是当月产生当月合并，盖章无害；但移植历史日志（真实月份在几百年前）时，合并 = 日期全灭（全部显示为今天）。

**正确做法：直接重写持久层 raw**（绕过一切合并 API）：

```csharp
var ld = g.data.dataUnitLog.data.allLog[unitID];
ld.allVitalLog.Clear();
foreach (var item in fileItems)   // LogItemData，month 已是真实月份
{
    string content = item.DataToString();     // 自动带真实月份
    ld.allVitalLog.Add(new Il2CppStringArray(
        new string[] { item.month.ToString(), content }));
}
ld.lastUpdateVitalLogDataMonth = -1;  // 强制视图按新 raw 重建
```

> ⚠️ 修完 raw 后**不要再调用任何合并 API**（包括 addvital 的合并路径）——合并会重序列化该单位全部数据，把刚修好的月份再盖一遍，甚至可能导致存档卡死。追加新条目也用 raw 直写。

### 3.4 人物故事 vs 生涯大事

| | 人物故事(allLog) | 生涯大事(allVitalLog) |
|---|---|---|
| 语义 | 近期日常（切磋/买卖/探望） | 终身里程碑（拜师/结偶/突破/死亡） |
| 清理 | ClearOverMonthLog 滚动清除远古条目 | 永久保留 |
| 移植策略 | **月份平移到当前时间附近**（源最新条目锚定到当前月），否则过月即被清 | 保留真实历史月份 |

### 3.5 自由叙事（自定义文本日志）

日志 id=999999 不在 RoleLog 表里。hook `DataUnitLog.LogData.Data.GetLogString()`（**实例方法，前缀钩子**）：

```csharp
public static bool Prefix(DataUnitLog.LogData.Data __instance, ref string __result)
{
    if (__instance.id != null && __instance.id.Contains(999999)
        && __instance.values != null && __instance.values.Length > 0)
    {
        __result = __instance.values[0];   // values[0] 即全文
        return false;                       // 跳过原方法
    }
    return true;
}
```

---

## 四、移植方法论

### 4.1 导出（源存档）

1. 反射序列化 `UnitInfoData` 全字段为 JSON（`src/UnitIO.cs` 的 `WriteValue`）：
   - 指针去重防循环引用（`TryGetPointer` + seen 集合）
   - **容器（List/Dictionary/数组）先枚举、再做类型跳过检查**——`Il2CppSystem.Collections.Generic.List` 的全名含 "Il2CppSystem"，先查跳过表会把整个容器序列化成 null（法宝/道具全丢的根因）
2. 日志导出：持久层 `allLog[id].allLogData/allVitalLogData` → 包成 `List<LogItemData>` JSON + `GetLogString()` 渲染的可读版（人工核对）
3. `_meta.json` 记录源世界 `roundMonth`（年龄修正用）

### 4.2 导入（目标存档）

**核心思想：让游戏工厂造骨架，我们只填肉。**

```
1. 骨架：g.conf.roleAttributeCoefficient.RandomInitNPCUnit(sex, 0, 0)
   → 本世界合法的 UnitInfoData（indexNum/schoolID/pointGrid 全部有效）
2. 合并：MergeInto(json, 骨架) —— 递归合并：
   - 基础类型直接写
   - 子对象保留骨架原生实例继续往里写字段（指针永远有效）
   - 列表/数组整体重建
   - 跳过世界结构性字段（见下）
3. 世界结构性字段（照搬源世界会 AddUnit 原生数组越界）：
   indexNum, schoolID, pointX/Y, pointGridData, isChangePoint,
   dieEscapeBeforePointX/Y, createDay, residueDay
4. 出生点复制玩家坐标（找得到人）
5. WorldUnitMgr.AddUnit(info) —— 游戏自动分配新 unitID 防碰撞
6. 年龄修正：新年龄 = 源年龄 + (目标世界 roundMonth - 源世界 roundMonth)
7. 日志回填：fixmonths 直写 raw（见 3.3）
```

### 4.3 安全纪律

- **动存档前必备份**：`CacheData/3_XXXXXX/` 整目录拷走（加密 .cache 文件 + CacheData 元信息）
- 注入失败可能留下**幽灵单位**（AddUnit 中途抛异常，计数+1 但按 ID 查不到）——重启读档即清除，失败后**不要存档**
- 每次只改一个变量，改完先只读验证（`logs`/`unit` 命令），确认再存档

---

## 五、踩坑实录（按杀伤力排序）

### #1 SkipWs 吞逗号 —— 一个字符伪装成三种症状
自研 JSON 解析器的跳空白函数把 `,` 当空白吞了 → **每个对象只解析出第一个键**。症状链：导入的单位是空壳 → `AddUnit` 原生数组越界（伪装成"世界索引不兼容"）→ 数据覆写全部无效 → 日志只进 1 条。
**教训：先用本地 harness 对真实文件跑解析器（`keys=预期数量`），再怪游戏。**

### #2 hook Il2Cpp 构造函数 = 全部存档进档卡死
Harmony hook `MapWorldMgr` 构造函数 → MonoMod DMD 编译失败 → `EventsMgr::Init` NRE → 所有存档加载瞬间卡死。
**教训：只 hook 实例方法，绝不 hook 构造函数。** 需要实例时用更安全的捕获点。

### #3 互操作类型的构造陷阱
- `new WorldUnitLogMgr.LogData()` 编译报 CS1729（元数据里有 public ctor() 也不行）
- `Activator.CreateInstance(t)` 失败时**抛异常而不是返回 null**——`?? 兜底` 永远走不到，必须 try/catch 包
- 无构造器类型的兜底：`il2cpp_object_new(类指针)` + `ctor(IntPtr)` 包装（`NewIl2CppRaw`）
- **最优解：设计数据流时绕开这些类型**（让游戏原生 API 负责构造）

### #4 ReflectionOnly 签名是垃圾数据
`ReflectionOnlyLoadFrom` 查看方法签名显示 `Int32[]`，运行时 `csc` 编译验证实际是 `Int32`（标量）。数组/标量之差直接让 Invoke 绑定失败。
**教训：签名以编译器为准**——写个 sig.exe 运行时加载打印 `ParameterType.FullName`。

### #5 批量合并盖章（见 3.3）

### #6 缓冲 ≠ 持久层（见 3.1）——UI 只读持久层，写缓冲不触发合并不显示

### #7 Il2Cpp 字典 TryGetValue 互操作缺陷（TValue_REF 缺失）——用 ContainsKey + 索引器

### #8 PowerShell 5.1 heredoc 中文 GBK 乱码炸语法——脚本只写 ASCII；或 UTF-8 BOM

### #9 后台 tasklist 瞬时抖动误判进程状态——关键决策要连续多次确认

### #10 幽灵单位（见 4.3）

---

## 六、DebugAgent 工具（src/ 目录含全部源码）

文件驱动：命令写入 `游戏目录\DebugAgent\cmd.txt`（外部写，mod 每 0.5s 轮询），结果追加 `out.txt`。外部程序（脚本/AI）可全自动驱动游戏。

| 命令 | 说明 |
|---|---|
| `find 名字` / `unit ID` / `rel ID` | 查人/详情/关系 |
| `count` | 单位数 |
| `logs ID` | 持久层日志诊断（条数+渲染） |
| `log ID [n]` | 缓冲日志 |
| `export ID` | 导出单位+日志+meta 到 `DebugAgent\units\` |
| `import ID [新名字] [new]` | 工厂骨架注入+年龄修正+日志回填 |
| `writestore 单位ID 文件ID` | 文件→缓冲→逐条合并（会盖章，历史日期场景用 fixmonths） |
| `fixmonths 单位ID 文件ID` | **直写 raw 修真实月份**（自验证结构，不合并不盖章） |
| `addvital ID 999999 年 月 文本` | 自由叙事生涯大事 |
| `month N [auto]` / `stop` | 时间快进（F6/F7/F8/F10） |
| `ghost x` | 幽灵单位诊断 |
| `autoexport.txt` | 放单位ID，进档自动导出 |

---

## 七、结果

钟青曼（4219 岁，从出生算起）已完整落地 3 号存档：属性/技能/法宝道具/亲族(5+2 子女)/道心全量合并，68 条人物故事（平移至近期），74 条生涯大事（**真实历史年月**：第 9 年被击杀 → 第 10 年拜师 → 第 18 年结道侣 → 第 669 年成婚 → 第 1280 年元婴），外加第 1537 年 10 月的穿越叙事。

---

## License

MIT（文档与方法论）；游戏本体与原文数值版权归 guigugame 所有。
