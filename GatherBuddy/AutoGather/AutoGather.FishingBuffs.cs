using System.Linq;
using GatherBuddy.Classes;
using GatherBuddy.Helpers;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private static readonly uint[] FishingBuffIds =
    [
        850,   // Angler's Fortune (Patience)
        1803,  // Surface Slap
        1804,  // Identical Cast
        2779,  // Makeshift Bait
        2780,  // Prize Catch
        568,   // Fisher's Intuition
        763,   // Chum
        762,   // Fish Eyes
        3907,  // Big Game Fishing
        3972,  // Ambitious Lure
        3973   // Modest Lure
    ];

    private bool HasActiveFishingBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        return player.StatusList.Any(status => FishingBuffIds.Contains(status.StatusId));
    }

    private bool HasFoodBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        return player.StatusList.Any(status => status.StatusId == 48);
    }

    private bool HasMedicineBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        return player.StatusList.Any(status => status.StatusId == 49);
    }

    private ConfigPreset GetFishingConsumablesPreset()
    {
        if (_currentGatherTarget?.Fish != null)
            return MatchConfigPreset(_currentGatherTarget.Value.Fish);

        var next = _activeItemList.GetNextOrDefault();
        if (next.Fish != null)
            return MatchConfigPreset(next.Fish);

        return MatchConfigPreset((Fish?)null);
    }

    private bool TryUseFishingConsumables(ConfigPreset config)
    {
        if (Player.Job != 18)
            return false;

        var foodId = FirstAvailableConsumable(config.Consumables.Food);
        var potionId = FirstAvailableConsumable(config.Consumables.Potion);
        var needFood = config.Consumables.Food.Enabled
            && foodId > 0
            && !HasFoodBuff();
        var needMed = config.Consumables.Potion.Enabled
            && potionId > 0
            && !HasMedicineBuff();
        var manualItemId = GetConsumablesWithCastTime(config);

        if (!needFood && !needMed && manualItemId == 0)
            return false;
        if (HasActiveFishingBuff())
            return false;

        if (IsFishing || IsGathering)
        {
            GatherBuddy.Log.Debug("[Consumables] Quitting fishing to use configured fishing consumables");
            QueueQuitFishingTasks();
            return true;
        }

        if (needFood)
        {
            GatherBuddy.Log.Information($"[Consumables] Using food item {foodId}");
            EnqueueActionWithDelay(() => UseItem(foodId));
            return true;
        }

        if (needMed)
        {
            GatherBuddy.Log.Information($"[Consumables] Using medicine item {potionId}");
            EnqueueActionWithDelay(() => UseItem(potionId));
            return true;
        }

        if (manualItemId > 0)
        {
            GatherBuddy.Log.Information($"[Consumables] Using manual item {manualItemId}");
            EnqueueActionWithDelay(() => UseItem(manualItemId));
            return true;
        }

        return false;
    }
}
