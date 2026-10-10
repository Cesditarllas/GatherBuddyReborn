using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Crafting;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private uint? _recipeMaterialsRecipeId;
    private bool _openRecipeMaterialsPopup;
    private bool _recipeMaterialsUseCurrent;
    private string _recipeMaterialsNewListName = string.Empty;
    private string _recipeMaterialsStatus = string.Empty;

    public void OpenRecipeMaterialsPopup(uint recipeId)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null) return;

        _recipeMaterialsRecipeId = recipeId;
        _recipeMaterialsNewListName = $"{recipe.Value.ItemResult.Value.Name.ExtractText()} Materials";
        _recipeMaterialsUseCurrent = GatherBuddy.Instance.Interface.CurrentAutoGatherList != null;
        _recipeMaterialsStatus = string.Empty;
        _openRecipeMaterialsPopup = true;
        IsOpen = true;
    }

    private void DrawRecipeMaterialsPopup()
    {
        const string popupId = "Recipe Materials to Auto-Gather###RecipeMaterialsToGather";
        if (_openRecipeMaterialsPopup)
        {
            ImGui.OpenPopup(popupId);
            _openRecipeMaterialsPopup = false;
        }
        if (!ImGui.BeginPopupModal(popupId, ImGuiWindowFlags.AlwaysAutoResize)) return;

        var outputQuantity = GatherBuddy.Config.RecipeGatherOutputQuantity;
        if (ImGui.InputInt("Desired output quantity", ref outputQuantity))
        {
            GatherBuddy.Config.RecipeGatherOutputQuantity = Math.Clamp(outputQuantity, 1, 999999);
            GatherBuddy.Config.Save();
        }
        var fixedQuantities = GatherBuddy.Config.RecipeGatherFixedQuantities;
        if (ImGui.Checkbox("使用固定的材料数量", ref fixedQuantities))
        {
            GatherBuddy.Config.RecipeGatherFixedQuantities = fixedQuantities;
            GatherBuddy.Config.Save();
        }
        if (fixedQuantities)
        {
            var materials = GatherBuddy.Config.RecipeGatherMaterialQuantity;
            var crystals = GatherBuddy.Config.RecipeGatherCrystalQuantity;
            if (ImGui.InputInt("Each material", ref materials))
            {
                GatherBuddy.Config.RecipeGatherMaterialQuantity = Math.Clamp(materials, 1, 999999);
                GatherBuddy.Config.Save();
            }
            if (ImGui.InputInt("Each crystal / shard / cluster", ref crystals))
            {
                GatherBuddy.Config.RecipeGatherCrystalQuantity = Math.Clamp(crystals, 1, 999999);
                GatherBuddy.Config.Save();
            }
        }

        var current = GatherBuddy.Instance.Interface.CurrentAutoGatherList;
        ImGui.BeginDisabled(current == null);
        ImGui.Checkbox($"添加到当前清单: {current?.Name ?? "无"}", ref _recipeMaterialsUseCurrent);
        ImGui.EndDisabled();
        if (!_recipeMaterialsUseCurrent || current == null)
            ImGui.InputText("新清单名称", ref _recipeMaterialsNewListName, 128);

        if (ImGui.Button("添加材料") && _recipeMaterialsRecipeId is { } recipeId)
        {
            if (AddRecipeMaterials(recipeId, _recipeMaterialsUseCurrent ? current : null))
                ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
        if (_recipeMaterialsStatus.Length > 0) ImGui.TextWrapped(_recipeMaterialsStatus);
        ImGui.EndPopup();
    }

    private bool AddRecipeMaterials(uint recipeId, AutoGatherList? destination)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null) return false;
        var amountPerCraft = Math.Max(1, (int)recipe.Value.AmountResult);
        var crafts = (int)Math.Min(999999, ((long)GatherBuddy.Config.RecipeGatherOutputQuantity + amountPerCraft - 1) / amountPerCraft);
        var source = new CraftingListDefinition();
        source.AddRecipe(recipeId, crafts);
        var materials = source.CreatePlan().Materials;
        var targets = materials.Select(pair =>
        {
            IGatherable? item = GatherBuddy.GameData.Gatherables.GetValueOrDefault(pair.Key)
                                ?? (IGatherable?)GatherBuddy.GameData.Fishes.GetValueOrDefault(pair.Key);
            var quantity = GatherBuddy.Config.RecipeGatherFixedQuantities
                ? item is Gatherable { IsCrystal: true }
                    ? GatherBuddy.Config.RecipeGatherCrystalQuantity
                    : GatherBuddy.Config.RecipeGatherMaterialQuantity
                : pair.Value;
            return (Item: item, Quantity: (uint)Math.Clamp(quantity, 1, 999999));
        }).Where(target => target.Item != null).ToList();
        if (targets.Count == 0)
        {
            _recipeMaterialsStatus = "This recipe has no auto-gatherable materials.";
            return false;
        }

        var manager = GatherBuddy.Instance.AutoGatherListsManager;
        if (destination == null)
        {
            var baseName = string.IsNullOrWhiteSpace(_recipeMaterialsNewListName)
                ? "Recipe Materials" : _recipeMaterialsNewListName.Trim();
            var name = baseName;
            for (var suffix = 2; manager.Lists.Any(list => list.Name == name); suffix++)
                name = $"{baseName} {suffix}";
            destination = new AutoGatherList { Name = name };
            manager.AddList(destination);
        }

        var changed = 0;
        foreach (var (item, quantity) in targets)
        {
            if (item == null) continue;
            if (destination.Quantities.TryGetValue(item, out var existingQuantity))
            {
                if (quantity == existingQuantity) continue;
                manager.ChangeQuantity(destination, item, quantity);
            }
            else
                manager.AddItem(destination, item, quantity);
            changed++;
        }
        Communicator.Print($"Added or updated {changed} recipe materials in '{destination.Name}'.");
        return true;
    }
}
