# 从零复现手册（Zero-to-Repro）

目标：在一台新机器上，从装好游戏的裸机状态，复现「NPC 跨存档全量移植」。

## 0. 前置条件

- Steam 版鬼谷八荒（本文验证版本：2026-08 的当前版）
- Windows 10/11，.NET Framework 4.x
- 网络（下载工具）

## 1. 装 MelonLoader

1. 下载 [MelonLoader](https://github.com/LavaGang/MelonLoader) v0.5.4（或当时最新稳定版）
2. 把安装器指向游戏目录（`SteamLibrary\steamapps\common\鬼谷八荒\`）安装
3. 首次启动游戏一次——MelonLoader 会生成 `MelonLoader\Il2CppAssemblies\` 互操作 DLL
4. 建目录 `游戏目录\Mods\`（mod 放这里）

## 2. 拷出编译引用

```
mkdir work\dll
copy "游戏目录\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll" work\dll\
copy "游戏目录\MelonLoader\Il2CppAssemblies\Il2Cppmscorlib.dll" work\dll\
copy "游戏目录\MelonLoader\Il2CppAssemblies\Il2CppSystem.dll" work\dll\
copy "游戏目录\MelonLoader\Il2CppAssemblies\Il2CppSystem.Core.dll" work\dll\
copy "游戏目录\MelonLoader\MelonLoader.dll" work\dll\
copy "游戏目录\MelonLoader\0Harmony.dll" work\dll\
copy "游戏目录\MelonLoader\UnhollowerBaseLib.dll" work\dll\
copy "游戏目录\MelonLoader\UnhollowerRuntimeLib.dll" work\dll\
copy "游戏目录\游戏_Data\Managed\UnityEngine.*.dll" work\dll\  (CoreModule/UI/InputLegacy/TextRendering)
```

## 3. 拿一个能用的 C# 编译器

**系统自带 csc.exe 是 C#5，读互操作 DLL 会报 CS0008（元数据损坏）。** 下载 Roslyn 编译器：

```
nuget 下载 microsoft.net.compilers.toolset（解压取 tools/csc.exe）
```

## 4. 编译 DebugAgent

```
用 src/build_debug.cmd（改好路径），或直接：
csc.exe -nologo -target:library -optimize+ -out:MOD_dbgAgent.dll ^
  -reference:work\dll\Assembly-CSharp.dll ...（全部引用） ^
  src\ModMain.cs src\UnitIO.cs
```

产物 `MOD_dbgAgent.dll` 放进 `游戏目录\Mods\`。

## 5. 验证 mod 活着

启动游戏，进任意存档。看 `游戏目录\DebugAgent\out.txt` 出现 `== DebugAgent 输出 ==` 即成功。

外部驱动方式（无需游戏内控制台）：

```
echo count > "游戏目录\DebugAgent\cmd.txt"     # 写命令
type "游戏目录\DebugAgent\out.txt"             # 读结果（0.5s 轮询，读完清空 cmd.txt）
```

## 6. 源存档导出

```
echo find 钟青曼 > DebugAgent\cmd.txt          # 找到 unitID（如 vQ1mDL）
echo export vQ1mDL > DebugAgent\cmd.txt        # 导出
```

产物在 `DebugAgent\units\`：

| 文件 | 内容 |
|---|---|
| `<id>.json` | UnitInfoData 全量（应 >50KB，若只有几 KB=序列化器容器跳过 bug） |
| `<id>_log.json` | 人物故事 `List<LogItemData>` |
| `<id>_vital.json` | 生涯大事 `List<LogItemData>` |
| `<id>_readable.txt` | 人读版（**先核对这个**） |
| `<id>_meta.json` | `{"srcMonth":源世界绝对月}` |

**验证点**：python `json.load` 两个 json 能过、条数与 readable 一致。

## 7. 目标存档操作（先备份！）

```
copy "存档目录\3_XXXXXX" "备份目录\" /E     # CacheData 下对应槽位整目录
```

进目标存档后：

```
echo import vQ1mDL > DebugAgent\cmd.txt
```

预期输出：

```
ImportUnit诊断: JSON键=45 写入=311 跳过=31
年龄修正: 源月A->当前月B 补N月 (新年龄4219岁)
注入成功: id=<新ID> 名字=钟青曼 ... 孩子=5+2
```

验证：`unit <新ID>`、`ghost x`（幽灵数应为 0）、游戏内打开人物面板。

**⚠️ 如果注入失败：不要存档，直接重启读档清掉幽灵单位。**

## 8. 历史日志真实年月（关键步骤）

`import` 走的合并路径会把所有日期盖成当前月。修复：

```
echo fixmonths <新ID> vQ1mDL > DebugAgent\cmd.txt
```

预期输出：

```
fixmonths: raw[0]长度=2 [0]=<当前月> [1]=<当前月>&Q... => raw重写: 大事74条(真实月份) 故事68条(+N月)
```

验证：

```
echo logs <新ID> > DebugAgent\cmd.txt
```

生涯大事应显示真实历史年月（第 X 年 X 月，从人物出生起算）。

**修完不要再跑任何写日志的命令（addvital/writestore），然后存档。**

## 9. 自定义叙事（可选，在 fixmonths 之前做或用 raw 直写）

```
echo addvital <新ID> 999999 1536 10 叙事文本... > DebugAgent\cmd.txt
```

需要 `CustomLogPatch`（GetLogString 钩子）已注册才能渲染。

## 10. 常见故障对照

| 症状 | 根因 | 修复 |
|---|---|---|
| 进任何存档瞬间卡死 | hook 了 Il2Cpp 构造函数 | 只 hook 实例方法 |
| import 报 AddUnit 数组越界 | JSON 没解析进去（空单位）或未用工厂骨架 | 见 README 坑 #1/#3 |
| 注入成功但名字是随机 NPC | 数据没合并进骨架（解析器 bug） | 本地 harness 验证解析器 |
| 日志在缓冲里但 UI 不显示 | UI 只读持久层 | fixmonths 直写 raw |
| 存档后日期全变当前月 | 合并 API 盖章 | fixmonths 重修，之后不碰合并 |
| 人物故事过月消失 | ClearOverMonthLog 清远古 | 故事月份平移（fixmonths 已内置） |
