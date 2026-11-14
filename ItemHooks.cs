using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class ItemHooks
{
    private static readonly string[] Refused = { "CollectibleNote", "CollectiblePackage", "CollectibleTaskItem", "CollectibleStartPackage", "CollectibleRecipe", "CollectibleRecipeBook", "CollectibleRecipes", "FurnitureCollectable", "WastelandGlow" };
    private static readonly List<string> missing = new List<string>();
    private static bool installed;
    public static bool Ready { get; private set; }

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".item");
        missing.Clear();
        bool ok = true;
        ok &= Patch(harmony, typeof(CollectibleItem), "PerformPickup", Type.EmptyTypes, "PickupPrefix");
        ok &= Patch(harmony, typeof(CollectibleItem), "Interact", Type.EmptyTypes, "ItemInteractPrefix");
        ok &= Patch(harmony, typeof(CollectibleItem), "InvokeActions", Type.EmptyTypes, "InvokeActionsPrefix");
        ok &= Patch(harmony, typeof(CollectibleCoin), "Interact", Type.EmptyTypes, "CoinPrefix");
        ok &= Patch(harmony, typeof(Collectible), "Interact", Type.EmptyTypes, "InteractPrefix");
        ok &= Postfix(harmony, typeof(Collectible), "Destroy", Type.EmptyTypes, "DestroyPostfix");
        ok &= Postfix(harmony, typeof(ItemOperations), "SetCollectibleItemValues", new[] { typeof(ItemStack), typeof(GameObject), typeof(bool) }, "SpawnedPostfix");
        ok &= Finalizer(harmony, typeof(ItemData), "DropItem", new[] { typeof(int), typeof(bool) }, "GuestDropPrefix", "DropDoneFinalizer");
        ok &= Patch(harmony, typeof(ItemOperations), "DropItem", new[] { typeof(ItemStack), typeof(GameObject), typeof(ItemOperations.DropItemValues) }, "SuppressPrefix");
        RefuseAll(harmony);
        installed = true;
        if (!ok)
        {
            CoopLog.Once("ItemHooks.Install", "these item hooks did not install: " + string.Join(", ", missing.ToArray()));
            CoopLog.Log.Error("item sync is incomplete, the missing hooks can leave one side acting on its own");
            harmony.UnpatchSelf();
            return false;
        }
        Ready = true;
        return true;
    }

    public static bool PickupPrefix(CollectibleItem __instance)
    {
        return ItemGuest.PickupPrefix(__instance);
    }

    public static bool ItemInteractPrefix(CollectibleItem __instance)
    {
        return ItemGuest.ItemInteractPrefix(__instance);
    }

    public static bool InvokeActionsPrefix()
    {
        return ItemGuest.InvokeActionsPrefix();
    }

    public static bool CoinPrefix(CollectibleCoin __instance)
    {
        return ItemGuest.PickupPrefix(__instance);
    }

    public static bool InteractPrefix(Collectible __instance)
    {
        return ItemGuest.RefuseOther(__instance);
    }

    public static void DropDoneFinalizer(Exception __exception)
    {
        ItemHost.PlayerDropping = false;
    }

    public static bool GuestDropPrefix(ItemData __instance, int amount, bool dropAll)
    {
        ItemHost.PlayerDropping = true;
        return ItemGuest.DropPrefix(__instance, amount, dropAll);
    }

    public static bool SuppressPrefix()
    {
        return !ItemGuest.Applying;
    }

    public static void DestroyPostfix(Collectible __instance)
    {
        ItemHost.OnDestroyed(__instance);
    }

    public static void SpawnedPostfix(ItemStack item, GameObject gameObject)
    {
        if (gameObject == null) return;
        CollectibleItem piece = gameObject.GetComponent<CollectibleItem>();
        if (piece != null) ItemHost.OnSpawned(piece);
        if (piece != null) LocalDrops.OnSpawned(piece);
        if (piece != null) ItemGuest.OnItemSpawned(piece);
    }

    private static void RefuseAll(Harmony harmony)
    {
        List<Type> targets = new List<Type>();
        for (int i = 0; i < Refused.Length; i++) Target(targets, AccessTools.TypeByName(Refused[i]));
        Type[] types = AssemblyTypes(typeof(Collectible));
        if (types != null)
        {
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || !typeof(Collectible).IsAssignableFrom(type)) continue;
                if (type == typeof(Collectible) || type == typeof(CollectibleItem) || type == typeof(CollectibleCoin)) continue;
                if (!DeclaresInteract(type)) continue;
                Target(targets, type);
            }
        }
        List<string> names = new List<string>();
        for (int i = 0; i < targets.Count; i++)
        {
            if (Refuse(harmony, targets[i])) names.Add(targets[i].Name);
        }
        CoopLog.Log.Info("item hooks refuse " + names.Count + " collectible kinds on the guest: " + string.Join(", ", names.ToArray()));
    }

    private static void Target(List<Type> targets, Type type)
    {
        if (type == null || targets.Contains(type)) return;
        targets.Add(type);
    }

    private static bool DeclaresInteract(Type type)
    {
        try
        {
            return type.GetMethod("Interact", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHooks.interact." + type.Name, e);
            return false;
        }
    }

    private static Type[] AssemblyTypes(Type anchor)
    {
        try
        {
            return anchor.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            CoopLog.Once("ItemHooks.assembly", e);
            return e.Types;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHooks.assembly", e);
            return null;
        }
    }

    private static bool Refuse(Harmony harmony, Type type)
    {
        MethodBase original = AccessTools.Method(type, "Interact", Type.EmptyTypes);
        if (original == null) return false;
        if (original.DeclaringType != type) return false;
        try
        {
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(ItemHooks), "InteractPrefix"));
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHooks.refuse." + type.Name, e);
            return false;
        }
    }

    private static bool Patch(Harmony harmony, Type type, string name, Type[] args, string prefix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) return Miss(type, name);
        try
        {
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(ItemHooks), prefix));
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHooks." + name, e);
            return Miss(type, name);
        }
    }

    private static bool Postfix(Harmony harmony, Type type, string name, Type[] args, string postfix)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) return Miss(type, name);
        try
        {
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(ItemHooks), postfix));
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHooks." + name, e);
            return Miss(type, name);
        }
    }

    private static bool Finalizer(Harmony harmony, Type type, string name, Type[] args, string prefix, string finalizer)
    {
        MethodBase original = AccessTools.Method(type, name, args);
        if (original == null) return Miss(type, name);
        try
        {
            harmony.Patch(original,
                prefix: new HarmonyMethod(typeof(ItemHooks), prefix),
                finalizer: new HarmonyMethod(typeof(ItemHooks), finalizer));
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ItemHooks." + name, e);
            return Miss(type, name);
        }
    }

    private static bool Miss(Type type, string name)
    {
        missing.Add(type.Name + "." + name);
        return false;
    }
}
