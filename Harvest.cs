using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

internal static class Harvest
{
    public const string Channel = "harvest";
    private const int GiveBase = 900000000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly FieldInfo StockField = AccessTools.Field(typeof(WorkableResourceSource), "currentStock");
    private static readonly FieldInfo MaxField = AccessTools.Field(typeof(WorkableResourceSource), "maximumStock");
    private static readonly FieldInfo NeedField = AccessTools.Field(typeof(WorkableResourceSource), "currentDamageRequied");
    private static readonly FieldInfo EmptyColliderField = AccessTools.Field(typeof(WorkableResourceSource), "disableColliderOnEmpty");
    private static readonly FieldInfo ColliderField = AccessTools.Field(typeof(WorkableResourceSource), "resourceCollider");
    private static ulong yieldTo;
    private static int giveId;
    public static int Patched;

    public static void Install()
    {
        Harmony harmony = new Harmony(Plugin.PluginGuid + ".harvest");
        HarmonyMethod prefix = new HarmonyMethod(typeof(Harvest), "DamagePrefix");
        HarmonyMethod postfix = new HarmonyMethod(typeof(Harvest), "DamagePostfix");
        foreach (Type type in new[] { typeof(WorkableResourceSource), typeof(SpawnItemOnBreak) })
        {
            MethodInfo m = type.GetMethod("Damage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(int), typeof(Vector3), typeof(Vector3), typeof(DamageReceiver.DamageType) }, null);
            if (m == null) continue;
            try
            {
                harmony.Patch(m, prefix: prefix, postfix: postfix);
                Patched++;
            }
            catch (Exception e)
            {
                CoopLog.Once("Harvest.Install." + type.Name, e);
            }
        }
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ItemOperations), "AddItemsAndDropRemaining"), prefix: new HarmonyMethod(typeof(Harvest), "YieldPrefix"));
            harmony.Patch(AccessTools.Method(typeof(PlayerStats), "IncreaseSkill"), prefix: new HarmonyMethod(typeof(Harvest), "SkillPrefix"));
            harmony.Patch(AccessTools.Method(typeof(PlayerStats), "GetSkill"), prefix: new HarmonyMethod(typeof(Harvest), "GetSkillPrefix"));
        }
        catch (Exception e)
        {
            CoopLog.Once("Harvest.Install.items", e);
        }
        CoopLog.Log.Info("coop: harvest sync patched " + Patched + " resource and loot receivers");
    }

    private static bool AuthorityHere
    {
        get
        {
            if (!Session.IsGuest || !GuestSession.InHostWorld || FreeRoam.IAmAuthority) return false;
            string here = GameRefs.AreaId;
            if (here == null) return false;
            ulong authority = FreeRoam.AuthorityOf(here);
            return authority != 0UL && authority != Session.Me && string.Equals(Session.AreaOf(authority), here, StringComparison.Ordinal);
        }
    }

    public static bool DamagePrefix(MonoBehaviour __instance, int damage, Vector3 direction, Vector3 point, DamageReceiver.DamageType type)
    {
        try
        {
            if (!AuthorityHere || damage <= 0) return true;
            string key = CombatSync.KeyOf(__instance);
            if (string.IsNullOrEmpty(key)) return true;
            int skill = 0;
            WorkableResourceSource source = __instance as WorkableResourceSource;
            object kind = source == null || SkillTypeField == null ? null : SkillTypeField.GetValue(source);
            if (kind is PlayerStats.PlayerSkillType && (PlayerStats.PlayerSkillType)kind != PlayerStats.PlayerSkillType.None && PlayerStats.instance != null && source.CheckDamageType(type) && Int(StockField, source) >= 1)
            {
                skill = PlayerStats.instance.GetSkill((PlayerStats.PlayerSkillType)kind);
                PlayerStats.instance.IncreaseSkill((PlayerStats.PlayerSkillType)kind, 0.005f);
            }
            ulong authority = FreeRoam.Authority;
            if (authority == 0UL) return true;
            Session.Send(authority, Channel, "HREQ|" + GameRefs.AreaId + "|" + key + "|" + damage.ToString(Inv) + "|" + ((int)type).ToString(Inv) + "|" + V(direction) + "|" + V(point) + "|" + skill.ToString(Inv), true);
            return false;
        }
        catch (Exception e)
        {
            CoopLog.Once("Harvest.DamagePrefix", e);
            return true;
        }
    }

    public static void DamagePostfix(MonoBehaviour __instance)
    {
        try
        {
            if ((!Session.IsHost && !FreeRoam.IAmAuthority) || __instance == null) return;
            string here = GameRefs.AreaId;
            if (here == null) return;
            List<ulong> peers = Session.PeersInArea(here);
            if (peers.Count == 0) return;
            string key = CombatSync.KeyOf(__instance);
            if (string.IsNullOrEmpty(key)) return;
            string text;
            WorkableResourceSource source = __instance as WorkableResourceSource;
            SpawnItemOnBreak loot = __instance as SpawnItemOnBreak;
            if (source != null) text = "HS|" + here + "|" + key + "|" + Int(StockField, source).ToString(Inv) + "|" + Flt(NeedField, source).ToString("F1", Inv);
            else if (loot != null) text = "HB|" + here + "|" + key + "|" + loot.health.ToString(Inv) + "|0";
            else return;
            for (int i = 0; i < peers.Count; i++) Session.Send(peers[i], Channel, text, true);
        }
        catch (Exception e)
        {
            CoopLog.Once("Harvest.DamagePostfix", e);
        }
    }

    public static bool YieldPrefix(Item item, object[] meta, int amount)
    {
        if (yieldTo == 0UL || item == null || amount <= 0) return true;
        try
        {
            string text;
            string refused;
            if (!ItemMeta.TryEncode(meta, out text, out refused)) text = "";
            giveId = giveId >= 99999999 ? 1 : giveId + 1;
            Session.Send(yieldTo, ItemWire.Channel, ItemWire.Give(GiveBase + giveId, item.ID, amount, text), true);
            CoopLog.Log.Info("coop: harvest yield " + item.ID + " x" + amount + " sent to guest " + yieldTo);
        }
        catch (Exception e)
        {
            CoopLog.Once("Harvest.YieldPrefix", e);
        }
        return false;
    }

    public static bool SkillPrefix()
    {
        return yieldTo == 0UL;
    }

    private static readonly FieldInfo SkillTypeField = AccessTools.Field(typeof(WorkableResourceSource), "skillType");
    private static int guestSkill = -1;

    public static bool GetSkillPrefix(ref int __result)
    {
        if (yieldTo == 0UL || guestSkill < 0) return true;
        __result = guestSkill;
        return false;
    }

    private static string V(Vector3 v)
    {
        return v.x.ToString("F2", Inv) + "," + v.y.ToString("F2", Inv) + "," + v.z.ToString("F2", Inv);
    }

    private static int Int(FieldInfo f, object o)
    {
        object v = f == null ? null : f.GetValue(o);
        return v is int ? (int)v : 0;
    }

    private static float Flt(FieldInfo f, object o)
    {
        object v = f == null ? null : f.GetValue(o);
        return v is float ? (float)v : 0f;
    }

    public static void Handle(ulong from, string text)
    {
        try
        {
            if (!Session.Peers.Contains(from) || text == null || text.Length > 400 || !CombatSync.Admit(from)) return;
            string[] f = text.Split('|');
            if (f.Length < 5 || f[1] != GameRefs.AreaId) return;
            if (f[0] == "HREQ" && f.Length == 8 && FreeRoam.IAmAuthority && Session.AreaOf(from) == f[1]) Request(from, f);
            else if ((f[0] == "HS" || f[0] == "HB") && f.Length == 5 && from == FreeRoam.Authority) Apply(f);
        }
        catch (Exception e)
        {
            CoopLog.Once("Harvest.Handle", e);
        }
    }

    private static void Request(ulong from, string[] f)
    {
        int damage, type;
        Vector3 dir, point;
        if (!int.TryParse(f[3], NumberStyles.Integer, Inv, out damage) || damage <= 0 || damage > 100000) return;
        if (!int.TryParse(f[4], NumberStyles.Integer, Inv, out type) || !Enum.IsDefined(typeof(DamageReceiver.DamageType), type)) return;
        if (!CombatSync.Vec(f[5], out dir) || !CombatSync.Vec(f[6], out point)) return;
        int skill;
        if (!int.TryParse(f[7], NumberStyles.Integer, Inv, out skill) || skill < 0 || skill > 1000) skill = 0;
        MonoBehaviour target = CombatSync.Find(f[2]);
        if (!(target is WorkableResourceSource) && !(target is SpawnItemOnBreak)) return;
        yieldTo = from;
        guestSkill = skill;
        try { ((IDamageReceiver)target).Damage(damage, dir, point, (DamageReceiver.DamageType)type); }
        finally
        {
            yieldTo = 0UL;
            guestSkill = -1;
        }
    }

    private static void Apply(string[] f)
    {
        MonoBehaviour target = CombatSync.Find(f[2]);
        int value;
        if (target == null || !int.TryParse(f[3], NumberStyles.Integer, Inv, out value)) return;
        WorkableResourceSource source = target as WorkableResourceSource;
        if (source != null)
        {
            float need;
            float.TryParse(f[4], NumberStyles.Float, Inv, out need);
            int max = Math.Max(1, Int(MaxField, source));
            value = Mathf.Clamp(value, 0, max);
            int before = Int(StockField, source);
            if (StockField != null) StockField.SetValue(source, value);
            if (NeedField != null) NeedField.SetValue(source, need);
            if (source.onStock != null) source.onStock.triggerOutputs((float)value / max);
            if (source.onCurrentStock != null) source.onCurrentStock.triggerOutputs(value);
            object empty = EmptyColliderField == null ? null : EmptyColliderField.GetValue(source);
            Collider col = ColliderField == null ? null : ColliderField.GetValue(source) as Collider;
            if (empty is bool && (bool)empty && col != null) col.enabled = value > 0;
            if (value < max) source.ChangeDeltaSecondsStatus(true);
            if (before >= 1 && value < 1 && source.onEmpty != null) source.onEmpty.triggerOutputs();
            return;
        }
        SpawnItemOnBreak loot = target as SpawnItemOnBreak;
        if (loot == null) return;
        loot.health = value;
        if (value > 0) return;
        if (loot.onBreak != null) loot.onBreak.triggerOutputs();
        if (!loot.destroy) return;
        Collectible c = loot.GetComponent<Collectible>();
        if (c != null) c.DestroyWithEffect();
        else UnityEngine.Object.Destroy(loot.gameObject);
    }
}
