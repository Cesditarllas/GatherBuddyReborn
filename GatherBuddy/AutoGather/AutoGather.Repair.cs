using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
using GatherBuddy.Helpers;

namespace GatherBuddy.AutoGather;

public unsafe partial class AutoGather
{
    private bool _npcRepairTaskQueued;

    private Item? EquipmentNeedingRepair()
    {
        const int defaultThreshold = 5;
        var threshold = GatherBuddy.Config.AutoGatherConfig.DoRepair ? GatherBuddy.Config.AutoGatherConfig.RepairThreshold : defaultThreshold;

        var equippedItems = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        for (var i = 0; i < equippedItems->Size; i++)
        {
            var equippedItem = equippedItems->GetInventorySlot(i);
            if (equippedItem != null && equippedItem->ItemId > 0)
            {
                if (equippedItem->Condition / 300 <= threshold)
                {
                    return Dalamud.GameData.Excel.GetSheet<Item>().GetRow(equippedItem->ItemId);
                }
            }
        }

        return null;
    }

    private bool HasRepairJob(Item itemToRepair)
    {
        if (itemToRepair.ClassJobRepair.RowId > 0)
        {
            var repairJobLevel =
                PlayerState.Instance()->ClassJobLevels[
                    Dalamud.GameData.GetExcelSheet<ClassJob>()?.GetRow(itemToRepair.ClassJobRepair.RowId).ExpArrayIndex ?? 0];
            if (Math.Max(1, itemToRepair.LevelEquip - 10) <= repairJobLevel)
                return true;
        }

        return false;
    }

    private bool HasDarkMatter(Item itemToRepair)
    {
        var darkMatters = Dalamud.GameData.Excel.GetSheet<ItemRepairResource>();
        foreach (var darkMatter in darkMatters)
        {
            if (darkMatter.Item.RowId < itemToRepair.ItemRepair.Value.Item.RowId)
                continue;

            if (GetInventoryItemCount(darkMatter.Item.RowId) > 0)
                return true;
        }

        return false;
    }

    private bool RepairIfNeeded()
    {
        if (Dalamud.Conditions[ConditionFlag.Mounted] || Player.Job is not 16 /* MIN */ and not 17 /* BTN */ and not 18 /* FSH */)
            return false;

        var itemToRepair = EquipmentNeedingRepair();

        if (itemToRepair == null)
            return false;

        if (!GatherBuddy.Config.AutoGatherConfig.DoRepair)
        {
            Communicator.PrintError("装备耐久度过低, 请先启用自动修理功能。");
            AbortAutoGather("需要修理装备。");
            return true;
        }

        if (TryQueueNpcRepair(false))
            return true;

        if (!HasRepairJob((Item)itemToRepair))
        {
            AbortAutoGather("需要修理装备, 但未找到可修理的职业。");
            return true;
        }
        if (!HasDarkMatter((Item)itemToRepair))
        {
            AbortAutoGather("需要修理装备, 但未找到暗物质。");
            return true;
        }

        AutoStatus = "正在修理装备...";
        StopNavigation();

        var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
        if (RepairAddon == null)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);

        TaskManager.Enqueue(() => RepairAddon != null, 1000, true, "等待修理界面就绪");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon && addon != null) { GatherBuddy.Log.Debug("[Repair] 点击全部修理按钮"); new AddonMaster.Repair(addon).RepairAll(); } }, 1000, "修理全部装备");
        TaskManager.Enqueue(() => SelectYesnoAddon != null, 1000, true, "等待确认对话框就绪");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (SelectYesnoAddon is var addon && addon != null) Callback.Fire(&addon->AtkUnitBase, true, 0); }, 1000, "确认修理");
        TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 5000, "等待修理完成");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon and not null) Callback.Fire(&addon->AtkUnitBase, true, -1); }, 1000, true, "关闭修理界面");
        TaskManager.DelayNext(1000);
        TaskManager.Enqueue(() => {
            var repairAutoAddon = GetAddon<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RepairAuto");
            if (repairAutoAddon == null || !repairAutoAddon->IsVisible)
                return true;
            GatherBuddy.Log.Debug("[Repair] 修理界面仍可见，正在关闭");
            repairAutoAddon->Close(true);
            return true;
        }, 3000, "等待修理界面关闭或强制关闭");
        TaskManager.DelayNext(delay);

        return true;
    }

    private DateTime _lastRepairTime = DateTime.MinValue;
    
    private bool RepairIfNeededForFishing()
    {
        if (Dalamud.Conditions[ConditionFlag.Mounted] || Player.Job is not 18 /* FSH */)
            return false;

        var itemToRepair = EquipmentNeedingRepair();

        if (itemToRepair == null)
        {
            _lastRepairTime = DateTime.MinValue;
            return false;
        }
        
        if (GatherBuddy.Config.AutoGatherConfig.DeferRepairDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
            return false;
        
        if ((DateTime.Now - _lastRepairTime).TotalSeconds < 5)
            return false;

        if (!GatherBuddy.Config.AutoGatherConfig.DoRepair)
        {
            Communicator.PrintError("装备耐久度过低, 请先启用自动修理功能。");
            AbortAutoGather("需要修理装备。");
            return true;
        }

        if (TryQueueNpcRepair(true))
            return true;

        if (!HasRepairJob((Item)itemToRepair))
        {
            AbortAutoGather("需要修理装备, 但未找到可修理的职业。");
            return true;
        }
        if (!HasDarkMatter((Item)itemToRepair))
        {
            AbortAutoGather("需要修理装备, 但未找到暗物质。");
            return true;
        }

        AutoStatus = "正在修理装备...";
        _lastRepairTime = DateTime.Now;
        var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
        
        TaskManager.Enqueue(StopNavigation);
        
        if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
        {
            TaskManager.Enqueue(() =>
            {
                AutoHook.SetPluginState?.Invoke(false);
                AutoHook.SetAutoStartFishing?.Invoke(false);
            });
        }
        
        if (IsGathering || IsFishing)
        {
            QueueQuitFishingTasks();
            TaskManager.Enqueue(() => !IsFishing, 5000, "等待钓鱼停止");
        }
        
        if (RepairAddon == null)
        {
            EnqueueActionWithDelay(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6));
        }

        TaskManager.Enqueue(() => RepairAddon != null, 1000, true, "等待修理界面就绪");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon && addon != null) new AddonMaster.Repair(addon).RepairAll(); }, 1000, "修理全部装备");
        TaskManager.Enqueue(() => SelectYesnoAddon != null, 1000, true, "等待确认对话框就绪");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (SelectYesnoAddon is var addon && addon != null) new AddonMaster.SelectYesno(addon).Yes(); }, 1000, "确认修理");
        TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 5000, "等待修理完成");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon and not null) Callback.Fire(&addon->AtkUnitBase, true, -1); }, 1000, true, "关闭修理界面");
        TaskManager.DelayNext(1000);
        TaskManager.Enqueue(() => {
            var repairAutoAddon = GetAddon<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RepairAuto");
            if (repairAutoAddon == null || !repairAutoAddon->IsVisible)
                return true;
            GatherBuddy.Log.Debug("[Repair] 修理界面仍可见，正在关闭");
            repairAutoAddon->Close(true);
            return true;
        }, 3000, "等待修理界面关闭或强制关闭");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() =>
        {
            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                AutoHook.SetPluginState?.Invoke(true);
                AutoHook.SetAutoStartFishing?.Invoke(true);
            }
        });

        return true;
    }

    private bool TryQueueNpcRepair(bool fishing)
    {
        var repairConfig = GatherBuddy.Config.VulcanRepairConfig;
        if (!repairConfig.PrioritizeNPCRepair)
            return false;

        var npc = repairConfig.PreferredRepairNPC ?? FindRepairNPCInCurrentZone();
        if (npc == null)
            return false;

        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var needsNavigation = npc.TerritoryType != Dalamud.ClientState.TerritoryType
            || playerPosition == Vector3.Zero
            || Vector3.Distance(playerPosition, npc.Position) > 7f;
        if (needsNavigation && !VNavmesh.Enabled)
            return false;

        var repairPrice = global::GatherBuddy.Crafting.RepairManager.GetNPCRepairPrice();
        var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);
        if (gilCount < repairPrice)
        {
            GatherBuddy.Log.Warning($"[AutoGather] Not enough gil for NPC repair ({gilCount}/{repairPrice})");
            return false;
        }

        if (_npcRepairTaskQueued)
            return true;

        _npcRepairTaskQueued = true;
        _lastRepairTime = DateTime.Now;
        AutoStatus = "Repairing at NPC...";
        TaskManager.Enqueue(StopNavigation);

        var restoreAutoHook = fishing && GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled;
        if (fishing)
        {
            if (restoreAutoHook)
            {
                TaskManager.Enqueue(() =>
                {
                    AutoHook.SetPluginState?.Invoke(false);
                    AutoHook.SetAutoStartFishing?.Invoke(false);
                });
            }

            if (IsGathering || IsFishing)
            {
                QueueQuitFishingTasks();
                TaskManager.Enqueue(() => !IsFishing, 5000, "Wait until fishing stopped for NPC repair.");
            }
        }

        global::GatherBuddy.Crafting.CraftingTasks.ResetRepairState();
        if (needsNavigation)
        {
            EnqueueNpcRepairStep(() => global::GatherBuddy.Crafting.CraftingTasks.TaskNavigateToRepairNPC(npc),
                "Navigate to repair NPC", "Could not reach the repair NPC.", 120000);
        }
        EnqueueNpcRepairStep(global::GatherBuddy.Crafting.CraftingTasks.TaskInteractWithRepairNPC,
            "Interact with repair NPC", "Could not interact with the repair NPC.");
        EnqueueNpcRepairStep(global::GatherBuddy.Crafting.CraftingTasks.TaskSelectRepairFromMenu,
            "Select NPC repair", "Could not open the NPC repair menu.");
        EnqueueNpcRepairStep(() => global::GatherBuddy.Crafting.CraftingTasks.TaskExecuteRepair(),
            "Repair equipment at NPC", "NPC repair did not complete.");
        EnqueueNpcRepairStep(global::GatherBuddy.Crafting.CraftingTasks.TaskCloseRepairWindow,
            "Close repair window", "Could not close the NPC repair window.");
        TaskManager.Enqueue(() =>
        {
            _npcRepairTaskQueued = false;
            _lastRepairTime = DateTime.MinValue;
            if (restoreAutoHook)
            {
                AutoHook.SetPluginState?.Invoke(true);
                AutoHook.SetAutoStartFishing?.Invoke(true);
            }
        }, "Finish NPC repair");

        return true;
    }

    private global::GatherBuddy.Crafting.RepairNPCData? FindRepairNPCInCurrentZone()
    {
        if (global::GatherBuddy.Crafting.RepairManager.RepairNPCNearby(out var nearbyNpc) && nearbyNpc != null)
        {
            return global::GatherBuddy.Crafting.RepairNPCHelper.RepairNPCs.FirstOrDefault(npc =>
                       npc.DataId == nearbyNpc.BaseId && npc.TerritoryType == Dalamud.ClientState.TerritoryType)
                   ?? new global::GatherBuddy.Crafting.RepairNPCData
                   {
                       DataId = nearbyNpc.BaseId,
                       Name = nearbyNpc.Name.TextValue,
                       TerritoryType = Dalamud.ClientState.TerritoryType,
                       Position = nearbyNpc.Position
                   };
        }

        var territory = Dalamud.ClientState.TerritoryType;
        var playerPosition = Dalamud.Objects.LocalPlayer?.Position;
        if (playerPosition == null)
            return null;

        return global::GatherBuddy.Crafting.RepairNPCHelper.RepairNPCs
            .Where(npc => npc.TerritoryType == territory)
            .OrderBy(npc => Vector3.Distance(playerPosition.Value, npc.Position))
            .FirstOrDefault();
    }

    private void EnqueueNpcRepairStep(Func<global::GatherBuddy.Crafting.CraftingTasks.TaskResult> step,
        string name, string failure, int timeoutMs = 15000)
    {
        var started = DateTime.UtcNow;
        TaskManager.Enqueue(() =>
        {
            var result = step();
            if (result == global::GatherBuddy.Crafting.CraftingTasks.TaskResult.Done)
                return true;
            if (result == global::GatherBuddy.Crafting.CraftingTasks.TaskResult.Retry
                && (DateTime.UtcNow - started).TotalMilliseconds <= timeoutMs)
                return false;

            global::GatherBuddy.Crafting.CraftingTasks.StopNavigation();
            global::GatherBuddy.Crafting.CraftingTasks.ResetRepairState();
            TaskManager.Abort();
            _npcRepairTaskQueued = false;
            Communicator.PrintError($"[Auto-Gather] {failure}");
            AbortAutoGather(failure);
            return true;
        }, timeoutMs + 1000, true, name);
    }
}
