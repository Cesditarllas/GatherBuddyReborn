using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class CraftingListQueueBuilder
{
    public static List<CraftingListItem> CreateExpandedQueue(CraftingListDefinition list, bool useRetainerCraftableAvailability = false)
        => CreateExpandedQueue(list, list.CreatePlan(useRetainerCraftableAvailability));
    public static List<CraftingListItem> CreateGroupedQueue(CraftingListDefinition list, CraftingListPlan plan)
        => GetRecipesInDependencyOrder(list, plan)
            .Select(item => new CraftingListItem(item.RecipeId, item.Quantity)
            {
                IsOriginalRecipe = item.IsOriginalRecipe,
            })
            .ToList();

    public static List<CraftingListItem> CreateExpandedQueue(CraftingListDefinition list, CraftingListPlan plan)
    {
        var sortedRecipes = CreateGroupedQueue(list, plan);
        var expandedQueue = new List<CraftingListItem>();

        foreach (var recipeItem in sortedRecipes)
        {
            var isOriginal = recipeItem.IsOriginalRecipe;
            var originalItem = isOriginal ? list.Recipes.FirstOrDefault(r => r.RecipeId == recipeItem.RecipeId) : null;
            var recipeOptions = list.GetRecipeOptions(recipeItem.RecipeId, isOriginal);
            var recipeData = RecipeManager.GetRecipe(recipeItem.RecipeId);
            if (!recipeData.HasValue)
                continue;

            var forceQuickSynth = list.ShouldForceQuickSynth(recipeData.Value, isOriginal);
            var qualityOverrideMode = list.GetQualityOverrideMode(recipeData.Value, isOriginal);
            var forcePreferNQNoQuickSynth = !recipeData.Value.CanQuickSynth && list.ShouldForcePreferNQ(isOriginal);

            for (var i = 0; i < recipeItem.Quantity; i++)
            {
                var queueItem = new CraftingListItem(recipeItem.RecipeId, 1)
                {
                    IsOriginalRecipe = isOriginal,
                };

                queueItem.Options.NQOnly = recipeOptions.NQOnly || forceQuickSynth;
                queueItem.Options.Skipping = recipeOptions.Skipping;

                if (isOriginal && originalItem != null)
                    queueItem.ConsumableOverrides = originalItem.ConsumableOverrides.Clone();

                var craftSettings = isOriginal
                    ? originalItem?.CraftSettings
                    : list.PrecraftCraftSettings.GetValueOrDefault(recipeItem.RecipeId);
                var (effectiveMacroId, effectiveSolverOverride) = ResolveEffectiveMacroSelection(craftSettings, !isOriginal, list);
                queueItem.CraftSettings = BuildEffectiveQueueCraftSettings(
                    recipeData.Value,
                    craftSettings,
                    effectiveMacroId,
                    effectiveSolverOverride,
                    list.UseAllHQ,
                    forcePreferNQNoQuickSynth);
                queueItem.QualityPolicy = CraftingQualityPolicyResolver.Resolve(recipeData.Value, queueItem.CraftSettings, qualityOverrideMode);
                queueItem.IngredientPreferences = queueItem.QualityPolicy.BuildGuaranteedHQPreferences();

                expandedQueue.Add(queueItem);
            }
        }

        return expandedQueue;
    }

    internal static RecipeCraftSettings? BuildEffectiveQueueCraftSettings(
        Recipe recipe,
        RecipeCraftSettings? sourceSettings,
        string? effectiveMacroId,
        SolverOverrideMode effectiveSolverOverride,
        bool useAllHQByDefault,
        bool forcePreferNQ)
    {
        var settings = CraftingQualityPolicyResolver.BuildEffectiveSettings(recipe, sourceSettings, useAllHQByDefault, forcePreferNQ);
        if (settings == null && (effectiveMacroId != null || effectiveSolverOverride != SolverOverrideMode.Default))
            settings = new RecipeCraftSettings();

        if (settings == null)
            return null;

        settings.SelectedMacroId = effectiveMacroId;
        settings.SolverOverride = effectiveSolverOverride;

        return settings;
    }

    internal static (string? MacroId, SolverOverrideMode SolverOverride) ResolveEffectiveMacroSelection(RecipeCraftSettings? settings, bool isPrecraft, CraftingListDefinition list)
    {
        var isSpecific = settings != null
            && (settings.MacroMode == MacroOverrideMode.Specific
                || (settings.MacroMode == MacroOverrideMode.Inherit
                    && (!string.IsNullOrEmpty(settings.SelectedMacroId) || settings.SolverOverride != SolverOverrideMode.Default)));
        if (isSpecific)
            return (settings?.SelectedMacroId, settings?.SolverOverride ?? SolverOverrideMode.Default);

        return isPrecraft
            ? (list.DefaultPrecraftMacroId, list.DefaultPrecraftSolverOverride)
            : (list.DefaultFinalMacroId, list.DefaultFinalSolverOverride);
    }

    private static List<CraftingListItem> GetRecipesInDependencyOrder(CraftingListDefinition list, CraftingListPlan plan)
    {
        var result = new List<CraftingListItem>();
        var precrafts = plan.Recipes.Where(recipe => !recipe.IsOriginalRecipe).ToList();
        var finals = plan.OriginalRecipes;
        var finalPositions = list.Recipes.Select((recipe, index) => (recipe.RecipeId, index))
            .GroupBy(entry => entry.RecipeId)
            .ToDictionary(group => group.Key, group => group.First().index);

        AppendInDependencyOrder(Order(precrafts, list.CraftOrderMode), precrafts, result);
        AppendInDependencyOrder(Order(finals, list.CraftOrderMode, finalPositions), finals, result);
        return result;
    }

    private static IEnumerable<CraftingListItem> Order(List<CraftingListItem> items, CraftOrderMode mode,
        Dictionary<uint, int>? listPositions = null)
    {
        var positioned = items.Select((item, index) => (item, index));
        if (mode == CraftOrderMode.ListOrder)
            return listPositions == null
                ? items
                : positioned.OrderBy(entry => listPositions.GetValueOrDefault(entry.item.RecipeId, int.MaxValue))
                    .Select(entry => entry.item);

        return positioned.OrderBy(entry => GetRecipeOrderKey(entry.item.RecipeId))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item);
    }

    private static (int Level, long Difficulty) GetRecipeOrderKey(uint recipeId)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null)
            return (int.MaxValue, long.MaxValue);
        var level = recipe.Value.RecipeLevelTable.Value;
        return (level.ClassJobLevel, (long)level.Difficulty * recipe.Value.DifficultyFactor / 100);
    }

    private static void AppendInDependencyOrder(IEnumerable<CraftingListItem> ordered,
        List<CraftingListItem> allRecipes, List<CraftingListItem> result)
    {
        var byResultItem = allRecipes
            .Select(item => (Item: item, Recipe: RecipeManager.GetRecipe(item.RecipeId)))
            .Where(entry => entry.Recipe != null)
            .GroupBy(entry => entry.Recipe!.Value.ItemResult.RowId)
            .ToDictionary(group => group.Key, group => group.First().Item);
        var processed = new HashSet<uint>();

        void Visit(CraftingListItem item)
        {
            if (!processed.Add(item.RecipeId))
                return;

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe != null)
                foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe.Value))
                    if (byResultItem.TryGetValue(itemId, out var dependency))
                        Visit(dependency);

            result.Add(item);
        }

        foreach (var item in ordered)
            Visit(item);
    }
}
