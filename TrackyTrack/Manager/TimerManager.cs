﻿using System.Timers;
using CriticalCommonLib.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TrackyTrack.Data;

namespace TrackyTrack.Manager;

public class TimerManager
{
    private Plugin Plugin;

    private BulkResult LastBulkResult = new();
    private readonly Timer AwaitingBulkDesynth = new(1 * 1000);

    private readonly Timer CastTimer = new(3 * 1000);
    private bool OpeningCoffer;
    private uint CofferId;

    public readonly Timer TicketUsedTimer = new(1 * 1000);

    private readonly Timer RepairTimer = new(0.5 * 1000);
    public uint Repaired;

    private Territory EurekaTerritory;
    private CofferRarity EurekaRarity;
    private EurekaResult EurekaResult = new();
    public readonly Timer AwaitingEurekaResult = new(3 * 1000);

    public TimerManager(Plugin plugin)
    {
        Plugin = plugin;

        AwaitingBulkDesynth.AutoReset = false;
        AwaitingBulkDesynth.Elapsed += StoreBulkResult;

        CastTimer.AutoReset = false;
        CastTimer.Elapsed += (_, _) => OpeningCoffer = false;

        TicketUsedTimer.AutoReset = false;

        RepairTimer.AutoReset = false;
        RepairTimer.Elapsed += (_, _) => Repaired = 0;

        AwaitingEurekaResult.AutoReset = false;
        AwaitingEurekaResult.Elapsed += StoreEurekaResult;
    }

    public void Dispose() { }

    public void StartBulk()
    {
        LastBulkResult = new();
        AwaitingBulkDesynth.Start();
    }

    public void StartCoffer(uint cofferId)
    {
        CastTimer.Stop();
        CastTimer.Start();

        CofferId = cofferId;
        OpeningCoffer = true;
    }

    public void StartTicketUsed()
    {
        TicketUsedTimer.Start();
    }

    public void StartRepair()
    {
        RepairTimer.Start();
    }

    public void StartEureka(uint rarity)
    {
        EurekaTerritory = (Territory) Plugin.ClientState.TerritoryType;
        EurekaRarity = (CofferRarity) rarity;
        EurekaResult = new EurekaResult();
        AwaitingEurekaResult.Start();

        Plugin.Log.Debug($"Waiting for items to be received ...");
    }

    public void RepairResult(int gilDifference)
    {
        if (!RepairTimer.Enabled)
            return;

        RepairTimer.Stop();

        var character = Plugin.CharacterStorage.GetOrCreate(Plugin.ClientState.LocalContentId);
        character.Repairs += Repaired;
        character.RepairCost += (uint) gilDifference;

        Plugin.ConfigurationBase.SaveCharacterConfig();
    }

    public void DesynthItemAdded(InventoryMonitor.ItemChangesItem item)
    {
        if (!AwaitingBulkDesynth.Enabled)
            return;

        // 19 and below are crystals
        if (item.ItemId > 19)
            LastBulkResult.AddItem(item.ItemId, (uint) item.Quantity, item.Flags == InventoryItem.ItemFlags.HQ);
        else
            LastBulkResult.AddCrystal(item.ItemId, (uint) item.Quantity);
    }

    public void DesynthItemRemoved(InventoryMonitor.ItemChangesItem item)
    {
        if (!AwaitingBulkDesynth.Enabled)
            return;

        LastBulkResult.AddSource(item.ItemId);
    }

    public void StoreBulkResult(object? _, ElapsedEventArgs __)
    {
        if (!LastBulkResult.IsValid)
            return;

        var character = Plugin.CharacterStorage.GetOrCreate(Plugin.ClientState.LocalContentId);

        character.Storage.History.Add(DateTime.Now, new DesynthResult(LastBulkResult));
        foreach (var result in LastBulkResult.Received.Where(r => r.Item != 0))
        {
            var id = result.Item > 1_000_000 ? result.Item - 1_000_000 : result.Item;
            if (!character.Storage.Total.TryAdd(id, result.Count))
                character.Storage.Total[id] += result.Count;
        }

        Plugin.ConfigurationBase.SaveCharacterConfig();
    }

    public void StoreCofferResult(InventoryMonitor.ItemChangesItem item)
    {
        if (!OpeningCoffer)
            return;

        var save = false;
        var character = Plugin.CharacterStorage.GetOrCreate(Plugin.ClientState.LocalContentId);

        if (Plugin.Configuration.EnableVentureCoffers)
        {
            if (CofferId == 32161 && VentureCoffer.Content.Contains(item.ItemId))
            {
                character.Coffer.Opened += 1;
                if (!character.Coffer.Obtained.TryAdd(item.ItemId, (uint)item.Quantity))
                    character.Coffer.Obtained[item.ItemId] += (uint)item.Quantity;
                save = true;
            }
        }

        if (Plugin.Configuration.EnableGachaCoffers)
        {
            if (CofferId == 36635 && GachaThreeZero.Content.Contains(item.ItemId))
            {
                character.GachaThreeZero.Opened += 1;
                if (!character.GachaThreeZero.Obtained.TryAdd(item.ItemId, (uint) item.Quantity))
                    character.GachaThreeZero.Obtained[item.ItemId] += (uint) item.Quantity;
                save = true;
            }
            else if (CofferId == 36636 && GachaFourZero.Content.Contains(item.ItemId))
            {
                character.GachaFourZero.Opened += 1;
                if (!character.GachaFourZero.Obtained.TryAdd(item.ItemId, (uint) item.Quantity))
                    character.GachaFourZero.Obtained[item.ItemId] += (uint) item.Quantity;
                save = true;
            }
            else if (CofferId == 41667 && Sanctuary.Content.Contains(item.ItemId))
            {
                character.GachaSanctuary.Opened += 1;
                if (!character.GachaSanctuary.Obtained.TryAdd(item.ItemId, (uint) item.Quantity))
                    character.GachaSanctuary.Obtained[item.ItemId] += (uint) item.Quantity;
                save = true;
            }
        }

        if (save)
        {
            OpeningCoffer = false;
            Plugin.ConfigurationBase.SaveCharacterConfig();

            Plugin.GachaEntryUpload(CofferId, item.ItemId, (uint) item.Quantity);
        }

        if (OpeningCoffer)
        {
            OpeningCoffer = false;

            Plugin.ChatGui.Print(Utils.SuccessMessage("You've found an unknown coffer drop."));
            Plugin.ChatGui.Print(Utils.SuccessMessage("Please consider sending the following information to the dev:"));
            Plugin.ChatGui.Print($"Coffer: {CofferId} Item: {item.ItemId}");
        }
    }

    public void EurekaItemAdded(InventoryMonitor.ItemChangesItem item)
    {
        if (!AwaitingEurekaResult.Enabled)
            return;

        Plugin.Log.Debug($"Received item {item.ItemId}");
        EurekaResult.AddItem(item.ItemId, (uint) item.Quantity);
    }

    public void StoreEurekaResult(object? _, ElapsedEventArgs __)
    {
        if (!EurekaResult.IsValid)
        {
            Plugin.Log.Debug($"No items received, invalid result");
            return;
        }

        Plugin.Log.Debug($"All items received, storing result");
        var character = Plugin.CharacterStorage.GetOrCreate(Plugin.ClientState.LocalContentId);
        character.Eureka.History[EurekaTerritory][EurekaRarity].Add(DateTime.Now, EurekaResult);
        character.Eureka.Opened += 1;
        Plugin.ConfigurationBase.SaveCharacterConfig();

        Plugin.BunnyEntryUpload((uint) EurekaRarity, (uint) EurekaTerritory, EurekaResult.Items);
    }
}
