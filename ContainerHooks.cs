using System;
using System.Reflection;
using HarmonyLib;
using ObenseuerCore;
using UnityEngine;

internal static class ContainerHooks
{
    private static bool installed;
    public static bool Ready { get; private set; }
    private static int worldUse;
    private static int exitDepth;

    public static bool Install()
    {
        if (installed) return true;
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".cont");
        try
        {
            Patch(harmony, "EnterExternally", new[] { typeof(bool) }, "EnterPrefix", null, null);
            Patch(harmony, "ExitExternally", Type.EmptyTypes, "ExitPrefix", null, "ExitFinally");
            Patch(harmony, "UseStorage", Type.EmptyTypes, "UsePrefix", null, "UseFinally");
            Patch(harmony, "LockUse", Type.EmptyTypes, "LockPrefix", null, "LockFinally");
            installed = true;
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHooks.Install", e);
            harmony.UnpatchSelf();
            return false;
        }
        NetBlob.Received += OnBlob;
        Lobby.MemberLeft += ContainerHost.OnMemberLeft;
        Containers.Opened += ContainerHost.OnOpened;
        Containers.Closed += ContainerHost.OnClosed;
        Ready = true;
        return true;
    }

    public static bool EnterPrefix(Storage __instance, bool forceOpen)
    {
        if (PrisonLocal.IsCellStorage(__instance))
        {
            if (PrisonLocal.OwnsCell(__instance)) return true;
            Notice("That confiscated locker belongs to the other player.");
            return false;
        }
        if (FreeRoam.IAmAuthority && ContainerHost.GuestHolds(__instance))
        {
            CoopLog.Once("ContainerHooks.hostHold", "coop: the host open of a world container is refused because a guest holds it");
            return false;
        }
        if (!GuestSession.InHostWorld) return true;
        if (worldUse > 0 || StationStorage(__instance)) return ContainerGuest.OnEnter(__instance, forceOpen);
        return true;
    }

    private static bool StationStorage(Storage storage)
    {
        if (storage == null) return false;
        if (storage.GetComponent<CraftingBase>() != null) return true;
        return storage.GetComponentInParent<CraftingBase>() != null;
    }

    public static void UsePrefix()
    {
        worldUse++;
    }

    public static void LockPrefix()
    {
        worldUse++;
    }

    public static void UseFinally()
    {
        if (worldUse > 0) worldUse--;
    }

    public static void LockFinally()
    {
        if (worldUse > 0) worldUse--;
    }

    public static bool ExitPrefix(Storage __instance)
    {
        exitDepth++;
        return ContainerGuest.OnExitStart(__instance);
    }

    public static void ExitFinally(Exception __exception)
    {
        if (exitDepth > 0) exitDepth--;
        if (exitDepth != 0) return;
        ContainerGuest.OnExitEnd();
    }

    public static void OnBlob(ulong peer, string modId, string name, byte[] data)
    {
        try
        {
            if (modId != ContainerWire.ModId || !ContainerWire.IsBlobName(name)) return;
            string json = ContainerWire.Text(data);
            if (json == null) return;
            if (FreeRoam.IAmAuthority) ContainerHost.OnState(peer, name, json);
            else ContainerGuest.OnState(peer, name, json);
        }
        catch (Exception e)
        {
            CoopLog.Once("ContainerHooks.OnBlob", e);
        }
    }

    private static void Notice(string text)
    {
        CoopLog.Log.Info("coop: " + text);
        CoopNotice.Show("prison|cell", text, 5f);
    }

    public static string SelfCheck()
    {
        try
        {
            MethodInfo enter = AccessTools.Method(typeof(Storage), "EnterExternally", new[] { typeof(bool) });
            if (enter == null) return "Storage.EnterExternally(bool) was not found";
            if (enter.GetParameters()[0].Name != "forceOpen") return "Storage.EnterExternally names its first argument " + enter.GetParameters()[0].Name;
            if (AccessTools.Method(typeof(Storage), "ExitExternally", Type.EmptyTypes) == null) return "Storage.ExitExternally() was not found";
            if (AccessTools.Method(typeof(Storage), "UseStorage", Type.EmptyTypes) == null) return "Storage.UseStorage() was not found";
            if (AccessTools.Method(typeof(Storage), "LockUse", Type.EmptyTypes) == null) return "Storage.LockUse() was not found";
            if (AccessTools.Method(typeof(SaveData), "Capture", new[] { typeof(UnityEngine.Object) }) == null) return "SaveData.Capture was not found";
            if (AccessTools.Method(typeof(SaveData), "Apply", new[] { typeof(UnityEngine.Object), typeof(string) }) == null) return "SaveData.Apply was not found";
            if (AccessTools.Method(typeof(SaveController), "SetObjectGUID", new[] { typeof(UnityEngine.Object), typeof(string) }) == null) return "SaveController.SetObjectGUID was not found";
            if (AccessTools.Method(typeof(Containers), "FindByGuid", new[] { typeof(string) }) == null) return "Containers.FindByGuid was not found";
            if (AccessTools.Method(typeof(NetBlob), "Send", new[] { typeof(ulong), typeof(string), typeof(string), typeof(byte[]) }) == null) return "NetBlob.Send was not found";
        }
        catch (Exception e)
        {
            return "exception: " + e.Message;
        }
        return "container hooks ok";
    }

    private static void Patch(Harmony harmony, string name, Type[] args, string prefix, string postfix, string finalizer)
    {
        MethodBase original = AccessTools.Method(typeof(Storage), name, args);
        if (original == null) throw new MissingMethodException(typeof(Storage).Name, name);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(ContainerHooks), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(ContainerHooks), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ContainerHooks), finalizer));
    }
}
