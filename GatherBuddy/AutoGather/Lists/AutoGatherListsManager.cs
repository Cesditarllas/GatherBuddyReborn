using ElliLib.Filesystem;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.AutoGather.Lists;

public sealed class AutoGatherFolderConfig
{
    public string Path { get; set; } = string.Empty;
    public bool BalanceEnabled { get; set; }
}

public class ManualOrderSortMode : ISortMode<AutoGatherList>
{
    public ReadOnlySpan<byte> Name
        => "手动排序"u8;

    public ReadOnlySpan<byte> Description
        => "按手动指定的顺序排序，文件夹优先"u8;

    public IEnumerable<FileSystem<AutoGatherList>.IPath> GetChildren(FileSystem<AutoGatherList>.Folder folder)
    {
        var folders = folder.GetSubFolders()
            .OrderBy(path =>
            {
                var index = GatherBuddy.Config.AutoGatherFolders.FindIndex(config => config.Path == path.FullName());
                return index >= 0 ? index : int.MaxValue;
            })
            .ThenBy(path => path.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<FileSystem<AutoGatherList>.IPath>();
        var leaves = folder.GetLeaves()
            .OrderBy(l => l.Value.Order)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<FileSystem<AutoGatherList>.IPath>();
        return folders.Concat(leaves);
    }
}

public partial class AutoGatherListsManager : IDisposable
{
    public event Action? ActiveItemsChanged;

    private const string FileName         = "auto_gather_lists.json";
    private const string FileNameFallback = "gather_window.json";

    private readonly FileSystem<AutoGatherList>             _fileSystem;
    private readonly List<(IGatherable Item, uint Quantity)> _activeItems   = [];
    private readonly List<(IGatherable Item, uint Quantity)> _fallbackItems = [];
    private readonly HashSet<IGatherable>                    _localInventoryActiveItems = [];
    private readonly Dictionary<IGatherable, int>             _itemOrder = [];
    private readonly HashSet<IGatherable>                     _balancedItems = [];
    public static ManualOrderSortMode SortMode { get; } = new();

    public FileSystem<AutoGatherList> FileSystem
        => _fileSystem;

    public IEnumerable<AutoGatherList> Lists
        => _fileSystem.Select(kvp => kvp.Key);

    public ReadOnlyCollection<(IGatherable Item, uint Quantity)> ActiveItems
        => _activeItems.AsReadOnly();

    public ReadOnlyCollection<(IGatherable Item, uint Quantity)> FallbackItems
        => _fallbackItems.AsReadOnly();

    internal bool UsesRetainerInventory(IGatherable item)
        => !_localInventoryActiveItems.Contains(item);

    internal int GetItemOrder(IGatherable item)
        => _itemOrder.GetValueOrDefault(item, int.MaxValue);

    internal bool IsBalanced(IGatherable item)
        => _balancedItems.Contains(item);

    internal bool HasBalancedItems => _balancedItems.Count > 0;

    public AutoGatherListsManager()
    {
        _fileSystem = new FileSystem<AutoGatherList>();
        RestoreSavedFolders();
        _fileSystem.Changed += OnFileSystemChanged;
    }

    private AutoGatherListsManager(AutoGatherList.Config[] configs)
    {
        _fileSystem = new FileSystem<AutoGatherList>();
        var change = false;

        RestoreSavedFolders();

        foreach (var cfg in configs)
        {
            change |= AutoGatherList.FromConfig(cfg, out var list);

            var folderPath = string.IsNullOrEmpty(list.FolderPath) ? string.Empty : list.FolderPath;

            if (folderPath == list.Name)
            {
                folderPath = string.Empty;
                change = true;
            }

            var folderNames = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var folder = _fileSystem.Root;
            foreach (var folderName in folderNames)
            {
                (folder, _) = _fileSystem.FindOrCreateFolder(folder, folderName);
            }

            try
            {
                _fileSystem.CreateLeaf(folder, list.Name, list);
            }
            catch
            {
                _fileSystem.CreateDuplicateLeaf(folder, list.Name, list);
                change = true;
            }
        }

        if (change)
            Save();

        var addedFolder = false;
        foreach (var folder in _fileSystem.Root.GetAllDescendants(SortMode).OfType<FileSystem<AutoGatherList>.Folder>())
            if (!GatherBuddy.Config.AutoGatherFolders.Any(config => config.Path == folder.FullName()))
            {
                GatherBuddy.Config.AutoGatherFolders.Add(new AutoGatherFolderConfig { Path = folder.FullName() });
                addedFolder = true;
            }
        if (addedFolder) GatherBuddy.Config.Save();

        _fileSystem.Changed += OnFileSystemChanged;
        SetActiveItems();
    }

    private void RestoreSavedFolders()
    {
        foreach (var savedFolder in GatherBuddy.Config.AutoGatherFolders)
        {
            if (string.IsNullOrWhiteSpace(savedFolder.Path)) continue;
            try { _fileSystem.FindOrCreateAllFolders(savedFolder.Path); }
            catch (Exception e) { GatherBuddy.Log.Warning($"Could not restore auto-gather folder '{savedFolder.Path}': {e.Message}"); }
        }
    }

    private void OnFileSystemChanged(FileSystemChangeType type, FileSystem<AutoGatherList>.IPath changedObject, FileSystem<AutoGatherList>.IPath? previousParent, FileSystem<AutoGatherList>.IPath? newParent)
    {
        if (type == FileSystemChangeType.FolderAdded && changedObject is FileSystem<AutoGatherList>.Folder addedFolder)
        {
            GatherBuddy.Config.AutoGatherFolders.Add(new AutoGatherFolderConfig { Path = addedFolder.FullName() });
            GatherBuddy.Config.Save();
        }
        if (type == FileSystemChangeType.ObjectMoved && changedObject is FileSystem<AutoGatherList>.Folder movedFolder
            && previousParent is FileSystem<AutoGatherList>.Folder oldParent)
        {
            RememberFolder(movedFolder.Parent);
            var oldPath = oldParent.IsRoot ? movedFolder.Name : oldParent.FullName() + "/" + movedFolder.Name;
            MoveFolderMetadata(oldPath, movedFolder.FullName());
        }
        if (type == FileSystemChangeType.ObjectRemoved && changedObject is FileSystem<AutoGatherList>.Folder removedFolder)
        {
            var path = removedFolder.FullName();
            GatherBuddy.Config.AutoGatherFolders.RemoveAll(config => config.Path == path || config.Path.StartsWith(path + "/", StringComparison.Ordinal));
            GatherBuddy.Config.Save();
        }
        // Not renumbering the source folder on ObjectRemoved or ObjectMoved makes the numbering sparse, but it's fine for ordering.
        if (type is FileSystemChangeType.ObjectMoved or FileSystemChangeType.LeafAdded && changedObject is FileSystem<AutoGatherList>.Leaf newLeaf)
        {
            RememberFolder(newLeaf.Parent);
            newLeaf.Value.Order = newLeaf.Parent.GetLeaves().Where(leaf => leaf != newLeaf).Select(leaf => leaf.Value.Order).DefaultIfEmpty().Max() + 1;
            Save();
            if (newLeaf.Value.Enabled) SetActiveItems();
        }
    }

    private static void RememberFolder(FileSystem<AutoGatherList>.Folder folder)
    {
        var path = new Stack<FileSystem<AutoGatherList>.Folder>();
        for (var current = folder; !current.IsRoot; current = current.Parent) path.Push(current);
        var changed = false;
        while (path.TryPop(out var current))
        {
            if (GatherBuddy.Config.AutoGatherFolders.Any(config => config.Path == current.FullName())) continue;
            GatherBuddy.Config.AutoGatherFolders.Add(new AutoGatherFolderConfig { Path = current.FullName() });
            changed = true;
        }
        if (changed) GatherBuddy.Config.Save();
    }

    public void Dispose()
    { }

    public void SetActiveItems(bool removeCompletedItems = false)
    {
        if (removeCompletedItems && RemoveCompletedItemsFromEnabledLists())
            Save();
        _activeItems.Clear();
        _fallbackItems.Clear();
        _localInventoryActiveItems.Clear();
        _itemOrder.Clear();
        _balancedItems.Clear();

        var order = 0;
        foreach (var leaf in _fileSystem.Root.GetAllDescendants(SortMode).OfType<FileSystem<AutoGatherList>.Leaf>())
        {
            var list = leaf.Value;
            if (!list.Enabled) continue;
            var balanced = list.BalanceEnabled || IsFolderBalanced(leaf.Parent);
            foreach (var item in list.Items.Where(item => list.EnabledItems.GetValueOrDefault(item)))
            {
                _itemOrder.TryAdd(item, order);
                if (balanced) _balancedItems.Add(item);
                order++;
            }
        }

        var items = _fileSystem.Root.GetAllDescendants(SortMode)
            .OfType<FileSystem<AutoGatherList>.Leaf>()
            .Select(leaf => leaf.Value)
            .Where(l => l.Enabled)
            .SelectMany(l => l.Items.Select(i => (Item: i, Quantity: l.Quantities[i], l.Fallback, ItemEnabled: l.EnabledItems[i], l.UsesRetainerInventory)))
            .Where(i => i.ItemEnabled)
            .GroupBy(i => (i.Item, i.Fallback))
            .Select(x => (x.Key.Item, Quantity: (uint)Math.Min(x.Sum(g => g.Quantity), uint.MaxValue), x.Key.Fallback, UsesRetainerInventory: x.All(g => g.UsesRetainerInventory)));

        foreach (var (item, quantity, fallback, usesRetainerInventory) in items)
        {
            if (fallback)
            {
                _fallbackItems.Add((item, quantity));
            }
            else
            {
                if (!usesRetainerInventory)
                    _localInventoryActiveItems.Add(item);
                _activeItems.Add((item, quantity));
            }
        }

        ActiveItemsChanged?.Invoke();
    }

    private static bool IsFolderBalanced(FileSystem<AutoGatherList>.Folder folder)
    {
        for (var current = folder; !current.IsRoot; current = current.Parent)
            if (GatherBuddy.Config.AutoGatherFolders.Any(config => config.Path == current.FullName() && config.BalanceEnabled))
                return true;
        return false;
    }

    public void Save()
    {
        var file = Functions.ObtainSaveFile(FileName);
        if (file == null)
        {
            GatherBuddy.Log.Error("无法获取自动采集列表的保存文件");
            return;
        }

        try
        {
            var allLists = _fileSystem.Select(kvp => kvp.Key).ToList();
            foreach (var list in allLists)
            {
                if (_fileSystem.TryGetValue(list, out var leaf))
                    list.FolderPath = leaf.Parent.IsRoot ? string.Empty : leaf.Parent.FullName();
            }

            var text = JsonConvert.SerializeObject(allLists.Select(p => new AutoGatherList.Config(p)), Formatting.Indented);
            File.WriteAllText(file.FullName, text);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"序列化自动采集清单数据时出错:\n{e}");
        }
    }

    public static AutoGatherListsManager Load()
    {
        var file = Functions.ObtainSaveFile(FileName);
        if (file is not { Exists: true })
        {
            file = Functions.ObtainSaveFile(FileNameFallback);
        }

        if (file is { Exists: true })
        {
            try
            {
                var text = File.ReadAllText(file.FullName);
                var configs = JsonConvert.DeserializeObject<AutoGatherList.Config[]>(text);
                if (configs != null)
                    return new AutoGatherListsManager(configs);
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"反序列化自动采集清单时出错:\n{e}");
                Communicator.PrintError($"[GatherBuddy Reborn] �Զ��ɼ��б�����ʧ��, ��ִ�����á�");
            }
        }

        return new AutoGatherListsManager();
    }
}
