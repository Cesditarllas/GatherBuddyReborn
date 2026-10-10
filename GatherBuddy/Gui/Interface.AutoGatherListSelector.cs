using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ElliLib;
using ElliLib.Filesystem;
using ElliLib.Filesystem.Selector;
using ElliLib.Log;
using ElliLib.Raii;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private sealed class AutoGatherListFileSystemSelector : FileSystemSelector<AutoGatherList, int>
    {
        private const string BaitBuyListResultPopupId = "Bait Buy List Result###AutoGatherBaitBuyListResult";
        private sealed record BaitBuyListGenerationResult(
            bool VendorDataReady,
            IReadOnlyList<string> AllBaitNames,
            IReadOnlyList<VendorBuyListManager.VendorTargetRequest> Targets,
            IReadOnlyList<string> SkippedBaitNames);
        private sealed record BaitBuyListResultPopupState(string Summary, IReadOnlyList<string> SkippedBaitNames);

        private BaitBuyListResultPopupState? _baitBuyListResultPopup;
        public override ISortMode<AutoGatherList> SortMode
            => AutoGatherListsManager.SortMode;

        public void RefreshView()
        {
            SetFilterDirty();
        }

        public float SelectorWidth
        {
            get => GatherBuddy.Config.AutoGatherListSelectorWidth * ImGuiHelpers.GlobalScale;
            set
            {
                GatherBuddy.Config.AutoGatherListSelectorWidth = value / ImGuiHelpers.GlobalScale;
                GatherBuddy.Config.Save();
            }
        }

        public AutoGatherListFileSystemSelector()
            : base(_plugin.AutoGatherListsManager.FileSystem, Dalamud.Keys, new Logger(), null, "##AutoGatherListsFileSystem", true)
        {
            SetFilterDirty();
            AddButton(AddListButton, 0);
            AddButton(ImportFromClipboardButton, 10);
            SubscribeRightClickLeaf(MoveUpContext, 50);
            SubscribeRightClickLeaf(MoveDownContext, 60);
            SubscribeRightClickLeaf(DeleteListContext, 100);
            SubscribeRightClickLeaf(DuplicateListContext, 200);
            SubscribeRightClickLeaf(ToggleListContext, 300);
            SubscribeRightClickLeaf(BalanceListContext, 310);
            SubscribeRightClickLeaf(ExportListContext, 400);
            SubscribeRightClickLeaf(GenerateVendorBuyListContext, 450);
            SubscribeRightClickFolder(CreateFolderContext, 500);
            SubscribeRightClickFolder(MoveFolderUpContext, 40);
            SubscribeRightClickFolder(MoveFolderDownContext, 50);
            SubscribeRightClickFolder(ToggleFolderContext, 300);
            SubscribeRightClickFolder(BalanceFolderContext, 310);
            SubscribeRightClickFolder(DeleteFolderContext, 600);
            UnsubscribeRightClickLeaf(RenameLeaf);
            UnsubscribeRightClickFolder(RenameFolder);
            SubscribeRightClickLeaf(RenameListContext, 950);
            SubscribeRightClickFolder(RenameFolderContext, 950);

            PathDropped += OnPathDropped;
        }

        public AutoGatherListsDragDropData? DragDropItem { set; get; }
        private string _renameListName = string.Empty;
        private string _renameFolderName = string.Empty;

        public void DrawBaitBuyListResultPopup()
        {
            if (_baitBuyListResultPopup == null)
                return;
            using var theme = VulcanUiStyle.PushTheme();
            ImGui.PushStyleColor(ImGuiCol.WindowBg, VulcanUiStyle.PanelBackground);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
            var windowCenter = ImGui.GetWindowPos() + ImGui.GetWindowSize() * 0.5f;
            ImGui.SetNextWindowPos(windowCenter, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            if (!ImGui.Begin(BaitBuyListResultPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.End();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
                return;
            }

            ImGui.TextWrapped(_baitBuyListResultPopup.Summary);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Needs manual acquisition");
            DrawBaitNameList(_baitBuyListResultPopup.SkippedBaitNames);
            ImGui.Spacing();

            if (ImGui.Button("Close", new Vector2(100f * ImGuiHelpers.GlobalScale, 0f)))
                _baitBuyListResultPopup = null;

            ImGui.End();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        private void OnPathDropped(List<KeyValuePair<string, FileSystem<AutoGatherList>.IPath>> movedPaths, FileSystem<AutoGatherList>.IPath targetPath)
        {
            if (movedPaths.Count == 0)
                return;
            if (targetPath is not FileSystem<AutoGatherList>.Leaf targetLeaf)
                return;

            var movedLeaves = movedPaths
                .Where(path => path.Value is FileSystem<AutoGatherList>.Leaf)
                .Select(path => (path.Key, Leaf: (FileSystem<AutoGatherList>.Leaf)path.Value))
                .Where(path => path.Leaf != targetLeaf)
                .ToList();
            if (movedLeaves.Count == 0)
                return;

            var orderedMoves = movedLeaves.Select(path =>
            {
                var movedFromUpperPart = IsMovedFromUpperPart(path.Key, targetLeaf);
                return (path.Leaf, MovedFromUpperPart: movedFromUpperPart,
                    PlaceAfterTarget: movedFromUpperPart || path.Leaf.Value.Order < targetLeaf.Value.Order);
            }).ToList();

            // Reversing rows that move after the target keeps the selected block's visible order.
            var placeAfterTarget = orderedMoves[0].PlaceAfterTarget;
            var moves = placeAfterTarget ? orderedMoves.AsEnumerable().Reverse() : orderedMoves;
            foreach (var move in moves)
                _plugin.AutoGatherListsManager.MoveList(move.Leaf, targetLeaf, move.MovedFromUpperPart);

            Select(movedLeaves[^1].Leaf, true);

            static bool IsAncestor(FileSystem<AutoGatherList>.IPath ancestor, FileSystem<AutoGatherList>.IPath descendant)
            {
                do
                {
                    if (descendant.Parent == ancestor)
                        return true;
                    descendant = descendant.Parent;
                } while (descendant != null);
                return false;
            }

            bool IsMovedFromUpperPart(string oldPath, FileSystem<AutoGatherList>.Leaf target)
            {
                if (FileSystem.Find(oldPath, out var sourceFolder))
                    return false;

                if (IsAncestor(target.Parent, sourceFolder))
                    return true;

                if (IsAncestor(sourceFolder, target))
                    return false;

                foreach (var node in FileSystem.Root.GetAllDescendants(SortMode))
                {
                    if (node == sourceFolder)
                        return true;
                    if (node == target.Parent)
                        return false;
                }

                return false;
            }
        }

        protected override void HandleDragDrop(FileSystem<AutoGatherList>.IPath path)
        {
            if (DragDropItem != null && ImGuiUtil.IsDropping(AutoGatherListsDragDropData.Label) && path is FileSystem<AutoGatherList>.Leaf leaf)
            {
                var sourceList = DragDropItem.List;
                var targetList = leaf.Value;
                var index = DragDropItem.ItemIdx;
                _plugin.AutoGatherListsManager.MoveItem(sourceList, targetList, index);
            }
        }

        protected override bool FoldersDefaultOpen
            => false;

        protected override uint ExpandedFolderColor
            => 0xFFFFFFFF;

        protected override uint CollapsedFolderColor
            => 0xFFFFFFFF;

        protected override void DrawLeafName(FileSystem<AutoGatherList>.Leaf leaf, in int state, bool selected)
        {
            var list = leaf.Value;
            var flag = selected ? ImGuiTreeNodeFlags.Selected | LeafFlags : LeafFlags;

            var enabled = list.Enabled;
            if (ImGui.Checkbox($"##list_enabled_{leaf.Identifier}", ref enabled))
                _plugin.AutoGatherListsManager.SetListsEnabled([list], enabled);
            ImGui.SameLine();
            
            using var color = ImRaii.PushColor(ImGuiCol.Text, ColorId.DisabledText.Value(), !list.Enabled);
            var displayName = CheckUnnamed(list.Name);
            
            using var _ = ImRaii.TreeNode(displayName, flag);
            
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _plugin.AutoGatherListsManager.ToggleList(list);
            }
        }

        protected override int GetState(FileSystem<AutoGatherList>.IPath path)
            => 0;

        private void AddListButton(Vector2 size)
        {
            const string newListName = "newListName";
            if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Plus.ToIconString(), size, "Create a new auto-gather list.", false, true))
                ImGui.OpenPopup(newListName);

            string name = string.Empty;
            if (ImGuiUtil.OpenNameField(newListName, ref name) && name.Length > 0)
            {
                var list = new AutoGatherList() { Name = name };
                _plugin.AutoGatherListsManager.AddList(list);
            }
        }

        private void ImportFromClipboardButton(Vector2 size)
        {
            const string importName = "importListName";
            if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Clipboard.ToIconString(), size, "Import an auto-gather list from clipboard.", false, true))
                ImGui.OpenPopup(importName);

            string name = string.Empty;
            if (ImGuiUtil.OpenNameField(importName, ref name) && name.Length > 0)
            {
                var clipboardText = ImGuiUtil.GetClipboardText();
                if (AutoGatherList.Config.FromBase64(clipboardText, out var cfg))
                {
                    AutoGatherList.FromConfig(cfg, out var list);
                    list.Name = name;
                    _plugin.AutoGatherListsManager.AddList(list);
                }
            }
        }

        private void MoveUpContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            if (ImGui.MenuItem("Move Up"))
                _plugin.AutoGatherListsManager.MoveListUp(leaf);
        }

        private void MoveDownContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            if (ImGui.MenuItem("Move Down"))
                _plugin.AutoGatherListsManager.MoveListDown(leaf);
        }

        private void DeleteListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            if (ImGui.MenuItem("Delete List"))
                _plugin.AutoGatherListsManager.DeleteList(leaf.Value);
        }

        private void DuplicateListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            if (ImGui.MenuItem("Duplicate List"))
            {
                var clone = leaf.Value.Clone();
                clone.Name = $"{leaf.Value.Name} (Copy)";
                _plugin.AutoGatherListsManager.AddList(clone, leaf.Parent);
            }
        }

        private void ToggleListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            var selected = SelectedPaths.OfType<FileSystem<AutoGatherList>.Leaf>().ToList();
            if (selected.Count > 1 && selected.Contains(leaf))
            {
                if (ImGui.MenuItem($"Enable Selected ({selected.Count})"))
                    _plugin.AutoGatherListsManager.SetListsEnabled(selected.Select(path => path.Value), true);
                if (ImGui.MenuItem($"Disable Selected ({selected.Count})"))
                    _plugin.AutoGatherListsManager.SetListsEnabled(selected.Select(path => path.Value), false);
                return;
            }
            var list = leaf.Value;
            if (ImGui.MenuItem(list.Enabled ? "Disable" : "Enable"))
                _plugin.AutoGatherListsManager.ToggleList(list);
        }

        private void BalanceListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            var enabled = leaf.Value.BalanceEnabled;
            if (ImGui.MenuItem("Balance gathering within this list", string.Empty, enabled))
                _plugin.AutoGatherListsManager.SetListBalanceEnabled(leaf.Value, !enabled);
        }

        private void RenameListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            if (ImGui.IsWindowAppearing()) _renameListName = leaf.Value.Name;
            ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputTextWithHint("##rename_auto_list", "Rename list, press Enter", ref _renameListName, 128,
                    ImGuiInputTextFlags.EnterReturnsTrue) && !string.IsNullOrWhiteSpace(_renameListName))
            {
                _plugin.AutoGatherListsManager.ChangeName(leaf.Value, _renameListName.Trim());
                ImGui.CloseCurrentPopup();
            }
        }

        private void ExportListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            if (ImGui.MenuItem("Export to Clipboard"))
            {
                try
                {
                    var config = new AutoGatherList.Config(leaf.Value);
                    var base64 = config.ToBase64();
                    ImGui.SetClipboardText(base64);
                    Communicator.PrintClipboardMessage("Auto-gather list", leaf.Value.Name);
                }
                catch (Exception e)
                {
                    Communicator.PrintClipboardMessage("Auto-gather list", leaf.Value.Name, e);
                }
            }
        }

        private void GenerateVendorBuyListContext(FileSystem<AutoGatherList>.Leaf leaf)
        {
            var vendorBuyListManager = GatherBuddy.VendorBuyListManager;
            var vendorBuyListWindow  = GatherBuddy.VendorBuyListWindow;
            var result               = BuildVendorBuyListGenerationResult(leaf.Value, vendorBuyListManager);
            var canOpenMenu          = vendorBuyListManager != null && vendorBuyListWindow != null && result.VendorDataReady && result.AllBaitNames.Count > 0;

            if (!ImGui.BeginMenu("Generate Bait Buy List", canOpenMenu))
            {
                if (!canOpenMenu && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    var tooltip = vendorBuyListManager == null || vendorBuyListWindow == null
                        ? "Vendor buy lists are not available."
                        : result.AllBaitNames.Count == 0
                            ? "No direct fishing bait was found in this auto-gather list."
                            : VendorShopResolver.IsInitializing
                                ? "Vendor data is still loading."
                                : "Vendor data is not ready yet.";
                    ImGui.SetTooltip(tooltip);
                }
                return;
            }

            if (ImGui.MenuItem("Create New List", string.Empty, false, result.Targets.Count > 0))
                OpenCreateVendorBuyListPopup(leaf.Value, result, vendorBuyListWindow!);

            if (ImGui.BeginMenu("Add to Existing List", result.Targets.Count > 0 && vendorBuyListManager!.Lists.Count > 0))
            {
                foreach (var list in vendorBuyListManager.Lists.OrderByDescending(list => list.CreatedAt))
                {
                    if (ImGui.MenuItem(list.Name))
                        AddTargetsToVendorBuyList(leaf.Value, list.Id, list.Name, result, vendorBuyListManager);
                }

                ImGui.EndMenu();
            }

            if (result.SkippedBaitNames.Count > 0)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Show Non-Vendor Baits"))
                    OpenSkippedBaitPopup(leaf.Value, result);
            }

            ImGui.EndMenu();
        }

        private static BaitBuyListGenerationResult BuildVendorBuyListGenerationResult(AutoGatherList list,
            VendorBuyListManager? vendorBuyListManager)
        {
            var baits = list.Items.OfType<Fish>()
                .Select(fish => fish.InitialBait)
                .Where(bait => bait is { Id: not 0 })
                .GroupBy(bait => bait.Id)
                .Select(group => group.First())
                .OrderBy(bait => bait.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var baitNames = baits.Select(bait => bait.Name).ToList();

            if (vendorBuyListManager == null)
                return new BaitBuyListGenerationResult(false, baitNames, [], []);

            VendorShopResolver.InitializeAsync();
            if (!VendorShopResolver.IsInitialized)
                return new BaitBuyListGenerationResult(false, baitNames, [], []);

            var targets = new List<VendorBuyListManager.VendorTargetRequest>();
            var skippedBaits = new List<string>();
            foreach (var bait in baits)
            {
                if (vendorBuyListManager.CanAddSupportedItem(bait.Id))
                    targets.Add(new VendorBuyListManager.VendorTargetRequest(bait.Id, 1));
                else
                    skippedBaits.Add(bait.Name);
            }

            return new BaitBuyListGenerationResult(true, baitNames, targets, skippedBaits);
        }

        private void OpenCreateVendorBuyListPopup(AutoGatherList sourceList, BaitBuyListGenerationResult result,
            VendorBuyListWindow vendorBuyListWindow)
        {
            var listName = string.IsNullOrWhiteSpace(sourceList.Name)
                ? "Auto-Gather Baits"
                : $"{sourceList.Name} Baits";
            GatherBuddy.Log.Debug(
                $"[AutoGatherListSelector] Creating a new vendor buy list '{listName}' from auto-gather list '{sourceList.Name}' with {result.Targets.Count:N0} bait target(s) and {result.SkippedBaitNames.Count:N0} skipped bait(s).");
            if (!vendorBuyListWindow.OpenCreateListPopup(listName, result.Targets))
            {
                GatherBuddy.Log.Debug(
                    $"[AutoGatherListSelector] Unable to create vendor buy list '{listName}' from auto-gather list '{sourceList.Name}'.");
                return;
            }

            OpenSkippedBaitPopup(sourceList, result, $"Created vendor buy list '{listName}'");
        }

        private void AddTargetsToVendorBuyList(AutoGatherList sourceList, Guid listId, string listName,
            BaitBuyListGenerationResult result, VendorBuyListManager vendorBuyListManager)
        {
            GatherBuddy.Log.Debug(
                $"[AutoGatherListSelector] Adding {result.Targets.Count:N0} bait target(s) from auto-gather list '{sourceList.Name}' to vendor buy list '{listName}' with {result.SkippedBaitNames.Count:N0} skipped bait(s).");
            if (vendorBuyListManager.TrySetTargets(listId, result.Targets, selectList: true, openWindow: true, announce: true) == 0)
            {
                GatherBuddy.Log.Debug(
                    $"[AutoGatherListSelector] Unable to add bait targets from auto-gather list '{sourceList.Name}' to vendor buy list '{listName}'.");
                return;
            }

            OpenSkippedBaitPopup(sourceList, result, $"Updated vendor buy list '{listName}'");
        }

        private void OpenSkippedBaitPopup(AutoGatherList sourceList, BaitBuyListGenerationResult result, string? actionPrefix = null)
        {
            if (result.SkippedBaitNames.Count == 0)
                return;

            var sourceListName = string.IsNullOrWhiteSpace(sourceList.Name)
                ? "this auto-gather list"
                : $"'{sourceList.Name}'";
            var baitLabel = result.SkippedBaitNames.Count == 1 ? "bait was" : "baits were";
            var requirementText = result.SkippedBaitNames.Count == 1
                ? "It needs to be crafted or obtained outside the vendor system."
                : "They need to be crafted or obtained outside the vendor system.";
            var summary = actionPrefix == null
                ? $"The following {baitLabel} not added from {sourceListName}. {requirementText}"
                : $"{actionPrefix} from {sourceListName}. The following {baitLabel} not added. {requirementText}";

            _baitBuyListResultPopup = new BaitBuyListResultPopupState(summary, result.SkippedBaitNames);
        }

        private static void DrawBaitNameList(IReadOnlyList<string> baitNames)
        {
            var height = Math.Clamp(
                baitNames.Count * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().FramePadding.Y * 4f,
                80f * ImGuiHelpers.GlobalScale,
                220f * ImGuiHelpers.GlobalScale);
            using var panel = VulcanUiStyle.PushPanel();
            using var child = ImRaii.Child("##baitBuyListSkippedBaits", new Vector2(440f * ImGuiHelpers.GlobalScale, height), true);
            if (!child)
                return;

            foreach (var baitName in baitNames)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(baitName);
            }
        }

        private void CreateFolderContext(FileSystem<AutoGatherList>.Folder folder)
        {
            const string newFolderName = "newFolderName";
            if (ImGui.MenuItem("Create Subfolder"))
                ImGui.OpenPopup(newFolderName);

            string name = string.Empty;
            if (ImGuiUtil.OpenNameField(newFolderName, ref name) && name.Length > 0)
            {
                _plugin.AutoGatherListsManager.CreateFolder(name, folder);
            }
        }

        private void MoveFolderUpContext(FileSystem<AutoGatherList>.Folder folder)
        {
            if (!folder.IsRoot && ImGui.MenuItem("Move Folder Up"))
                _plugin.AutoGatherListsManager.MoveFolderOnce(folder, false);
        }

        private void MoveFolderDownContext(FileSystem<AutoGatherList>.Folder folder)
        {
            if (!folder.IsRoot && ImGui.MenuItem("Move Folder Down"))
                _plugin.AutoGatherListsManager.MoveFolderOnce(folder, true);
        }

        private void ToggleFolderContext(FileSystem<AutoGatherList>.Folder folder)
        {
            if (folder.IsRoot) return;
            if (ImGui.MenuItem("Enable All Lists in Folder"))
                _plugin.AutoGatherListsManager.SetFolderEnabled(folder, true);
            if (ImGui.MenuItem("Disable All Lists in Folder"))
                _plugin.AutoGatherListsManager.SetFolderEnabled(folder, false);
        }

        private void BalanceFolderContext(FileSystem<AutoGatherList>.Folder folder)
        {
            if (folder.IsRoot) return;
            var enabled = _plugin.AutoGatherListsManager.IsFolderBalanceEnabled(folder);
            if (ImGui.MenuItem("Balance gathering within this folder", string.Empty, enabled))
                _plugin.AutoGatherListsManager.SetFolderBalanceEnabled(folder, !enabled);
        }

        private void RenameFolderContext(FileSystem<AutoGatherList>.Folder folder)
        {
            if (folder.IsRoot) return;
            if (ImGui.IsWindowAppearing()) _renameFolderName = folder.Name;
            ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputTextWithHint("##rename_auto_folder", "Rename folder, press Enter", ref _renameFolderName, 128,
                    ImGuiInputTextFlags.EnterReturnsTrue) && !string.IsNullOrWhiteSpace(_renameFolderName))
            {
                _plugin.AutoGatherListsManager.RenameFolder(folder, _renameFolderName);
                ImGui.CloseCurrentPopup();
            }
        }

        private void DeleteFolderContext(FileSystem<AutoGatherList>.Folder folder)
        {
            if (folder.IsRoot)
                return;

            if (ImGui.MenuItem("Delete Folder"))
            {
                _plugin.AutoGatherListsManager.DeleteFolder(folder);
            }
        }
    }
}
