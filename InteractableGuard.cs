using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ObenseuerCore;

internal static class InteractableGuard
{
    private const float NoticeGap = 3f;
    private const string NoticeText = "Not shared in co-op yet: ";
    private const string LocalKind = "local";
    private const string SharedKind = "shared";
    private const string HandledKind = "handled";
    private const string Unclassified = "not classified in the guard table, refused by default";

    private struct Entry
    {
        public string kind;
        public string reason;
        public bool unknown;
    }

    private static readonly Dictionary<string, Entry> Table = Build();

    private static bool installed;
    private static int patched;
    private static int local;
    private static int handled;
    private static int missing;
    private static readonly HashSet<string> refused = new HashSet<string>();
    private static readonly List<string> unclassified = new List<string>();

    public static bool Install()
    {
        if (installed) return true;
        installed = true;
        patched = 0;
        local = 0;
        handled = 0;
        missing = 0;
        unclassified.Clear();
        refused.Clear();
        try
        {
            Type[] types = AssemblyTypes();
            if (types == null)
            {
                CoopLog.Once("InteractableGuard.types", "coop: the game assembly types could not be read, the interact guard is off");
                return false;
            }
            Harmony harmony = new Harmony(Plugin.PluginGuid + ".interactguard");
            List<string> failed = new List<string>();
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || type.IsAbstract || type == typeof(Interactable)) continue;
                if (!IsInteractable(type) || !DeclaresInteract(type)) continue;
                Entry entry = Classify(type);
                if (entry.kind == LocalKind)
                {
                    local++;
                    continue;
                }
                if (entry.kind == HandledKind)
                {
                    handled++;
                    continue;
                }
                if (entry.unknown) unclassified.Add(type.Name);
                if (Patch(harmony, type)) patched++;
                else failed.Add(type.Name);
            }
            missing = CountMissing(types);
            SelfCheck(failed);
            return failed.Count == 0;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableGuard.Install", e);
            return false;
        }
    }

    public static bool GuardPrefix(object __instance)
    {
        try
        {
            if (!Session.IsInLobby || !Session.IsGuest || FreeRoam.IAmAuthority || !GuestSession.InHostWorld) return true;
            if (LobbyKeys.Busy() || SaveController.Loading || MainMenu.Instance != null) return true;
            Interactable target = __instance as Interactable;
            if (target == null) return true;
            string name = target.GetType().Name;
            if (refused.Add(name)) CoopLog.Log.Info("interact guard: refused " + name + " on a guest");
            CoopNotice.Show("guard." + name, NoticeText + Pretty(name), NoticeGap);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableGuard.GuardPrefix", e);
            return true;
        }
    }

    private static bool Patch(Harmony harmony, Type type)
    {
        try
        {
            MethodBase original = AccessTools.Method(type, "Interact", Type.EmptyTypes);
            if (original == null || original.DeclaringType != type) return false;
            HarmonyMethod prefix = new HarmonyMethod(typeof(InteractableGuard), "GuardPrefix");
            prefix.priority = (int)Priority.First;
            harmony.Patch(original, prefix: prefix);
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableGuard.patch." + type.Name, e);
            return false;
        }
    }

    private static void SelfCheck(List<string> failed)
    {
        CoopLog.Log.Info("coop: interact guard patched " + patched + " shared, " + local + " local, " + handled + " excluded, " + missing + " missing from this build");
        if (unclassified.Count != 0) CoopLog.Log.Info("coop: interact guard refused " + unclassified.Count + " unclassified types: " + string.Join(", ", unclassified.ToArray()));
        if (failed.Count == 0) return;
        CoopLog.Once("InteractableGuard.failed", "coop: the interact guard could not patch: " + string.Join(", ", failed.ToArray()));
        CoopLog.Log.Error("the interact guard is incomplete, those interactables can still be used by a guest");
    }

    private static Entry Classify(Type type)
    {
        Type cursor = type;
        while (cursor != null && cursor != typeof(Interactable))
        {
            Entry found;
            if (Table.TryGetValue(cursor.Name, out found) && found.kind == HandledKind && Owned(found, cursor.Name)) return found;
            cursor = cursor.BaseType;
        }
        Entry entry;
        if (Table.TryGetValue(type.Name, out entry))
        {
            if (entry.kind == HandledKind && !Owned(entry, type.Name)) entry.kind = SharedKind;
            return entry;
        }
        Entry unknown = new Entry();
        unknown.kind = SharedKind;
        unknown.reason = Unclassified;
        unknown.unknown = true;
        return unknown;
    }

    private static bool Owned(Entry entry, string name)
    {
        if (entry.reason == null || entry.reason.IndexOf("InteractableSync", StringComparison.Ordinal) != 0) return true;
        return InteractableSync.Patched(name);
    }

    private static int CountMissing(Type[] types)
    {
        HashSet<string> names = new HashSet<string>();
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] != null) names.Add(types[i].Name);
        }
        int count = 0;
        foreach (KeyValuePair<string, Entry> pair in Table)
        {
            if (!names.Contains(pair.Key)) count++;
        }
        return count;
    }

    private static bool IsInteractable(Type type)
    {
        if (typeof(Interactable).IsAssignableFrom(type)) return true;
        Type[] faces = type.GetInterfaces();
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].Name == "IInteractable") return true;
        }
        return false;
    }

    private static bool DeclaresInteract(Type type)
    {
        try
        {
            return type.GetMethod("Interact", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableGuard.interact." + type.Name, e);
            return false;
        }
    }

    private static Type[] AssemblyTypes()
    {
        try
        {
            return typeof(Interactable).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            CoopLog.Once("InteractableGuard.assembly", e);
            return e.Types;
        }
        catch (Exception e)
        {
            CoopLog.Once("InteractableGuard.assembly", e);
            return null;
        }
    }

    private static string Pretty(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        StringBuilder text = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) text.Append(' ');
            text.Append(c);
        }
        return text.ToString();
    }

    private static Dictionary<string, Entry> Build()
    {
        Dictionary<string, Entry> table = new Dictionary<string, Entry>();
        Add(table, LocalKind, "InteractableChair", "sits only the player who clicked");
        Add(table, LocalKind, "InteractableLadder", "climbs only the player who clicked");
        Add(table, LocalKind, "Toilet", "toilet use and the bucket act on this player");
        Add(table, LocalKind, "ToiletBucket", "sits only the player who clicked (Toilet)");
        Add(table, LocalKind, "InteractableWait", "waits away this machine's own clock");
        Add(table, LocalKind, "InteractableWaitWork", "waits away this machine's own clock");
        Add(table, LocalKind, "InteractableWaitResident", "waits away this machine's own clock");
        Add(table, LocalKind, "FakeDoor", "sound and animation only, the door never moves");
        Add(table, LocalKind, "Meter", "read only gauge");
        Add(table, LocalKind, "InteractableCalendar", "read only calendar");
        Add(table, LocalKind, "BaseCrafting", "empty Interact, nothing to sync");

        Add(table, SharedKind, "InteractableComputer", "no sync owner, the screen and its contents do not travel");
        Add(table, SharedKind, "InteractableDamageReceiver", "damage and lock state are world state");
        Add(table, SharedKind, "CardGameTable", "gambling table, money and card state stay local");
        Add(table, SharedKind, "CraftingUpgrade", "toggles a workstation connection that is world state");
        Add(table, SharedKind, "FuelPump", "pays with the acting machine's money");
        Add(table, SharedKind, "MinitrainFuelTank", "takes liquid from the acting machine's inventory");
        Add(table, SharedKind, "grow", "uproot and harvest change a savable plant");
        Add(table, SharedKind, "GrowItem", "harvest changes the plant and the picker inventory");
        Add(table, SharedKind, "InteractableCashRegister", "shop register, money is world state");
        Add(table, SharedKind, "InteractableFurnitureDisplay", "buys furniture into the world");
        Add(table, SharedKind, "InteractableItemRemover", "removes items out of world slots");
        Add(table, SharedKind, "InteractableMoney", "takes money for an output");
        Add(table, SharedKind, "PajatsoGameplay", "gambling machine, the coins are world state");
        Add(table, SharedKind, "ParcelLocker", "takes parcels out of the shared mail state");
        Add(table, SharedKind, "PutItemSelection", "consumes an input item and can cost money");
        Add(table, SharedKind, "InvoicePaymentMachine", "payment machine, money and items");
        Add(table, SharedKind, "RatTrap", "picks the trap contents into the picker inventory");
        Add(table, SharedKind, "TrackTrainInteractableCoupler", "couples or releases a mine train carriage");

        Add(table, HandledKind, "Door", "DoorSync owns the door state");
        Add(table, HandledKind, "DoorChangelevel", "DoorSync carries the changelevel door lock");
        Add(table, HandledKind, "DoorLockBypass", "runs Door.UseDoor, which DoorSync reports");
        Add(table, HandledKind, "Changelevel", "travel is gated by GuestGateHooks and HostTravel");
        Add(table, HandledKind, "TriggerChangelevel", "travel is gated by GuestGateHooks and HostTravel");
        Add(table, HandledKind, "InteractableFastTravel", "travel is gated by GuestGateHooks and HostTravel");
        Add(table, HandledKind, "Storage", "ContainerHooks owns container use");
        Add(table, HandledKind, "LiquidStorage", "LiquidSync and UnsharedHooks own the tank");
        Add(table, HandledKind, "VendingMachine", "VendHooks owns the purchase");
        Add(table, HandledKind, "CraftingBase", "CraftHooks and StationState own the station");
        Add(table, HandledKind, "manufacturing", "CraftHooks owns the craft (CraftingBase)");
        Add(table, HandledKind, "manufacturingProcess", "CraftHooks owns the process station (CraftingBase)");
        Add(table, HandledKind, "Growing", "CraftHooks owns the grow bed (CraftingBase)");
        Add(table, HandledKind, "Generator", "PowerSync owns the generator (CraftingBase)");
        Add(table, HandledKind, "Dishwasher", "CraftHooks and PowerSync own it (CraftingBase)");
        Add(table, HandledKind, "BaseShop", "GuestGateHooks owns the shop menu");
        Add(table, HandledKind, "FurnitureCrafting", "its Interact opens the shop menu, which GuestGateHooks gates");
        Add(table, HandledKind, "MarketShop", "GuestGateHooks owns the market shop");
        Add(table, HandledKind, "Trade", "GuestGateHooks owns the trade menu");
        Add(table, HandledKind, "TradeSpecial", "GuestGateHooks owns the trade menu (Trade)");
        Add(table, HandledKind, "FurnitureShop", "GuestGateHooks owns the shop menu (BaseShop)");
        Add(table, HandledKind, "TrainShop", "GuestGateHooks owns the shop menu (BaseShop)");
        Add(table, HandledKind, "InteractableTicketMachine", "GuestGateHooks owns the ticket machine");
        Add(table, HandledKind, "BottleRecycling", "GuestGateHooks owns the bottle recycling");
        Add(table, HandledKind, "InteractablePay", "GuestGateHooks owns the pay machine");
        Add(table, HandledKind, "Phone", "GuestGateHooks owns the phone");
        Add(table, HandledKind, "Mailbox", "GuestGateHooks owns the mailbox");
        Add(table, HandledKind, "Bank", "GuestGateHooks owns the bank");
        Add(table, HandledKind, "InteractableTalk", "TalkHooks and GuestGateHooks own the dialogue");
        Add(table, HandledKind, "InteractableItemGiver", "TalkHooks owns the item giver");
        Add(table, HandledKind, "Collectible", "ItemHooks owns the pickup");
        Add(table, HandledKind, "CollectibleCoin", "ItemHooks owns the coin pickup");
        Add(table, HandledKind, "CollectibleItem", "ItemHooks owns the pickup");
        Add(table, HandledKind, "CollectibleNote", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "CollectiblePackage", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "CollectibleRecipe", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "CollectibleRecipeBook", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "CollectibleRecipes", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "CollectibleStartPackage", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "CollectibleTaskItem", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "FurnitureCollectable", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "WastelandGlow", "ItemHooks refuses it and syncs the pickup");
        Add(table, HandledKind, "SlotMachineGameplay", "SlotHooks owns the slot machine");
        Add(table, HandledKind, "InteractableDynamiteCone", "DynamiteSync owns the cone");
        Add(table, HandledKind, "Sleep", "BedVote owns the bed use");
        Add(table, HandledKind, "Button", "InteractableSync owns it");
        Add(table, HandledKind, "BreakableLock", "InteractableSync owns the lock state and its break");
        Add(table, HandledKind, "SelectionButton", "InteractableSync owns it");
        Add(table, HandledKind, "Keypad", "InteractableSync owns it");
        Add(table, HandledKind, "Lantern", "InteractableSync owns it");
        Add(table, HandledKind, "EditableSign", "InteractableSync owns it");
        Add(table, HandledKind, "InteractableRelay", "InteractableSync owns it");
        Add(table, HandledKind, "Radio", "InteractableSync owns it (phase 3a)");
        Add(table, HandledKind, "TelevisionNoise", "InteractableSync owns it (phase 3a)");
        Add(table, HandledKind, "InteractableShower", "InteractableSync owns it (phase 3a)");
        Add(table, HandledKind, "WorkableResourceSource", "Harvest owns the resource and loot damage");
        return table;
    }

    private static void Add(Dictionary<string, Entry> table, string kind, string name, string reason)
    {
        Entry entry = new Entry();
        entry.kind = kind;
        entry.reason = reason;
        table[name] = entry;
    }
}
