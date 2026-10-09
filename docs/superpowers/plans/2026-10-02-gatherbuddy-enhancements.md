# GatherBuddy Reborn 自定义功能实施计划

> 仅为实施路线图；本阶段不改源码。建议按阶段交付，每阶段单独构建与游戏内确认。

**目标：** 在保留上游可合并能力的前提下，为 GatherBuddy Reborn 加强清单管理、自动修理、Vulcan 制作排序、市场搜索入口和装备升级流程。

**架构：** 优先复用现有的 AutoGather 文件系统、Vulcan 制作队列、修理 NPC 导航、Marketboard 服务和游戏 Gearset 能力。跨插件功能只在对方提供稳定 IPC 时调用；未确认的外部接口先做兼容性验证，不把私有 UI 内部状态当稳定 API。

**技术栈：** C#、Dalamud API 15、ImGui、现有 ElliLib 文件系统与插件 IPC。

---

## 阶段 1：自动采集清单文件夹与 Shift 范围选择

**现状：** 清单已有 `FolderPath` 持久化，`AutoGatherListsManager` 已有文件夹创建/删除与移动；选择器创建时关闭多选，拖放回调遇到多项直接抛 `NotImplementedException`。

**文件：**
- 修改 `GatherBuddy/Gui/Interface.AutoGatherListSelector.cs`
- 复用 `GatherBuddy/AutoGather/Lists/AutoGatherListsManager.cs`
- 复用 `GatherBuddy/AutoGather/Lists/AutoGatherListsManager.ManipPreset.cs`
- 参考 `GatherBuddy/Gui/VulcanWindow.CraftingListsTab.cs`

**步骤：**
1. 对齐 Vulcan 清单树的文件夹呈现与操作体验；保留现有保存格式，避免迁移用户已有清单。
2. 启用 ElliLib 选择器的多选，让 Shift 选择锚点至目标之间的清单范围。
3. 移除多项拖放的未实现分支；按稳定顺序逐项移动，并维持原有选择、排序和文件夹边界行为。
4. 确认新增、移动、重命名/删除、重启加载及多选拖放后，清单内容和激活状态正确。

**难度：低。** 数据与文件夹操作多数已存在，改动集中在选择器参数和多项拖放。

## 阶段 2：自动采集的 NPC 修理

**现状：** AutoGather 的 `AutoGather.Repair.cs` 有耐久检测及黑暗物质修理；Vulcan 的 `RepairManager`、`RepairNPCHelper`、`RepairNPCNavigator` 和 `CraftingQueueProcessor` 已有 NPC 选择、导航、交互及修理流程。

**文件：**
- 修改 `GatherBuddy/AutoGather/AutoGather.Repair.cs`
- 修改 `GatherBuddy/AutoGather/AutoGather.Config.cs`
- 修改自动采集配置界面所在的 `GatherBuddy/AutoGather/AutoGather.Ui.cs`
- 复用 `GatherBuddy/Crafting/RepairManager.cs`
- 复用 `GatherBuddy/Crafting/RepairNPCHelper.cs`
- 复用 `GatherBuddy/Crafting/RepairNPCNavigator.cs`
- 参考 `GatherBuddy/Crafting/CraftingQueueProcessor.cs`

**步骤：**
1. 把 NPC 修理作为黑暗物质不可用时的可配置路径；默认行为保持不变。
2. 复用已有 NPC 数据、玩家附近优先和导航实现，不复制第二套 NPC 数据库。
3. 将修理作为 AutoGather 可暂停/恢复的任务步骤；在不能导航、无法交互或修理失败时安全停止并给出原因。
4. 确认自修、NPC 修理、没有 NPC/导航插件、钓鱼延迟修理等路径不会重复执行或丢失当前清单进度。

**难度：中低。** 核心 NPC 能力已在 Vulcan 存在，主要风险是与采集状态机衔接。

## 阶段 3：Vulcan 制作顺序模式与失败跳过

**现状：** `CraftingListPlanner` 负责清单与半成品计划；`CraftingListQueueBuilder` 将半成品按依赖顺序扩展队列；`CraftingQueueProcessor` 已对部分缺材料和求解失败路径跳过。

**文件：**
- 修改 `GatherBuddy/Crafting/CraftingListDefinition.cs`
- 修改 `GatherBuddy/Crafting/CraftingListPlanner.cs`
- 修改 `GatherBuddy/Crafting/CraftingListQueueBuilder.cs`
- 修改 `GatherBuddy/Crafting/CraftingQueueProcessor.cs`
- 修改 `GatherBuddy/Gui/VulcanWindow.CraftingListsTab.cs` 与相应列表编辑设置界面
- 使用现有清单配置持久化机制保存排序选项

**步骤：**
1. 增加两种可选队列排序：清单加入顺序；按配方等级/难度升序。
2. 两种模式都先满足半成品依赖关系；同优先级时使用稳定顺序，防止每次重新计算队列顺序漂移。
3. 将“做不了”限定为可识别的非致命项目失败（例如缺少可用配方/当前能力无法求解/连续确认缺材失败）；标记该项目并继续下一项，不吞掉整队列异常。
4. 确认依赖关系、重复物品、材料消耗、手动跳过和队列恢复均与排序一致。

**难度：中高。** 主要工作是规划器与执行队列保持一致；“做不了”的判定边界需在实现前固定。

## 阶段 4：右键物品后搜索市场

**现状：** 插件已有 `GatherBuddy/Plugin/ContextMenu.cs` 游戏右键菜单、`GatherBuddy/Marketboard/MarketboardService.cs` 查询队列和 Vulcan Marketboard 搜索界面。Marketbuddy README 说明其市场上架辅助功能及配置命令，没有公开“按物品 ID 发起市场搜索”的 IPC 文档。

**文件：**
- 修改 `GatherBuddy/Plugin/ContextMenu.cs`
- 复用 `GatherBuddy/Marketboard/MarketboardService.cs`
- 修改 `GatherBuddy/Gui/VulcanWindow.cs` 或 `GatherBuddy/Gui/VulcanWindow.MarketboardTab.cs` 以接收物品并打开搜索页
- 只有验证存在稳定公开接口后，才新增 Marketbuddy IPC 适配代码

**步骤：**
1. 在物品右键菜单加入搜索选项，解析并传递正确的物品 ID。
2. 默认通过 GatherBuddy 自带 Marketboard 搜索并展示结果，避免依赖另一插件的内部实现。
3. 若用户要求必须打开 Marketbuddy 本体，再验证其当前源码/发布版本是否提供可用 IPC；没有则需接受上游新增 IPC 或采用脆弱的 UI 操作方案。
4. 确认背包、装备、鱼饵等不同菜单来源的物品 ID 归一化正确，菜单禁用状态与搜索异常可理解。

**难度：中（使用自带市场页）；高（强制驱动 Marketbuddy）。** 依赖其是否公开稳定的搜索接口。参考 [Marketbuddy 上游说明](https://github.com/PunishXIV/Marketbuddy)。

## 阶段 5：Stylist 装备升级后重算 Vulcan 与 AutoGather

**现状：** GatherBuddy 已有 Gearset 切换与制作属性读取能力；Stylist 上游说明支持从背包装备并更新现有套装，但没有在 README 中列出供其他插件调用的装备升级 IPC。

**文件：**
- 新增 `GatherBuddy/Crafting/StylistIntegration.cs`（仅当确认 Stylist 有稳定 IPC 时）
- 修改 `GatherBuddy/Crafting/CraftingQueueProcessor.cs`
- 修改 `GatherBuddy/Crafting/CraftingContextResolver.cs`
- 修改 `GatherBuddy/AutoGather/AutoGather.cs`
- 修改 `GatherBuddy/Crafting/GearsetStatsReader.cs`
- 修改 `GatherBuddy/Config/Configuration.cs` 和相应设置界面，提供默认关闭的升级检查选项

**步骤：**
1. 先对照当前 Stylist 版本源码确认是否能查询升级、装备、保存 Gearset；没有稳定 IPC 时停止在接口评估，不模拟点击其私有界面。
2. 以用户明确开启为前提，在制作/采集任务的安全暂停点检查目标职业是否有可用升级；排除当前任务不能穿戴的装备并要求操作可恢复。
3. 执行升级后等待装备与 Gearset 数据刷新，再重新读取属性并重算制作方案/采集能力，然后继续原队列。
4. 处理没有升级、Stylist 未安装、背包/兵装库访问限制、职业切换失败、保存套装失败和任务取消；失败时不丢队列、不自动反复装备。
5. 单独验证升级与原 Gearset、幻化、套装保存的交互，再逐步开放给制作与采集。

**难度：高。** 属于五项中风险最大的一项：要保证升级检测正确、Gearset 更新成功、重算前后状态一致，还受 Stylist 可用 IPC 限制。参考 [Stylist 上游说明](https://github.com/NightmareXIV/Stylist)。

## 推荐交付顺序

1. 阶段 1：快速完成，几乎全是现有能力的启用与补齐。
2. 阶段 2：复用 NPC 修理实现。
3. 阶段 3：先确认“做不了”的条件，再实现排序和跳过。
4. 阶段 4：先交付 GatherBuddy 自带市场搜索；Marketbuddy 定向联动作为可选后续。
5. 阶段 5：先核对 Stylist IPC，再决定是否进入实现；没有稳定接口就不承诺自动闭环。

**总体评估：** 前两项低到中低难度；Vulcan 排序中高；右键市场搜索中等，强制控制 Marketbuddy 则高；Stylist 自动升级闭环高。模型等级不是首要瓶颈，最后两项主要取决于第三方接口、游戏状态机和现场验证。建议分阶段实现，不需要因为清单/修理/排序而先更换模型。
