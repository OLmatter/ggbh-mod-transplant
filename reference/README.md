# 参考资源

## 工坊 Mod 源码（本项目起步时研读的对象）

均为 Steam 创意工坊公开订阅的 mod，源码随订阅分发在
`SteamLibrary\steamapps\workshop\content\1468810\<ID>\ModProject\ModCode\ModMain\ModMain.cs`。
收录于此仅作学习对照。

| 文件 | 工坊 ID | 作者 | 学到什么 |
|---|---|---|---|
| `MOD_llcJEQ_记录子系统_ModMain.cs` | 3781538054 | NavysLion | 行房记录、`UINPCInfoProperty.UpdateUI` 前缀钩子加 UI 条目、`objData` 自定义存储、`unitLog.AddLogData(u, key, values, null)` 写日志、SkyTip 悬停 |
| `MOD_883GdA_守宫砂_ModMain.cs` | 3781538601 | NavysLion | `ConfRoleAttributeCoefficient.RandomInitNPCUnit` 后缀钩子（NPC 出生入口——本项目把它发展为注入工厂）、`UnitActionFeedback1031.OnCreate` 双修触发点、气运(Luck)增删 |

工坊页面：
- https://steamcommunity.com/sharedfiles/filedetails/?id=3781538054
- https://steamcommunity.com/sharedfiles/filedetails/?id=3781538601

## 通用工具链

| 资源 | 用途 |
|---|---|
| [MelonLoader](https://github.com/LavaGang/MelonLoader) | Il2Cpp 游戏 mod 加载器 |
| [Harmony](https://github.com/pardeike/Harmony) | 方法钩子（随 MelonLoader 附带 0Harmony.dll） |
| UnhollowerBaseLib / UnhollowerRuntimeLib | Il2Cpp↔托管互操作（MelonLoader 自带） |
| [ilspy / ilspycmd](https://github.com/icsharpcode/ILSpy) | 反编译（本项目环境装不上，用反射+编译器验证替代） |
| microsoft.net.compilers.toolset (NuGet) | Roslyn csc.exe（系统 csc 是 C#5 读不了互操作元数据） |

## 本项目产出的知识（README 第三章）

日志/事件（生涯大事）系统的两级存储、raw 串格式、月份盖章机制——游戏本身没有公开文档，
社区流传的"事件系统教程"多止步于 `AddLogData` 写入；持久层结构与历史日期保真方案见主 README。
