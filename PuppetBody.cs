using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

internal static class PuppetBody
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string RiggingNamespace = "UnityEngine.Animations.Rigging";
    private const int MaxRefDepth = 8;
    private const float NeutralHeight = 0.8f;
    private const string PlaceholderName = "Coop puppet";
    private static readonly string[] HeldFields = { "righthandObject", "lefthandObject" };
    private static readonly string[] PoseParams = { "Forward", "Turn", "Crouch", "OnGround", "Jump" };

    public static void Build(Transform shell, List<Material> materials, out Animator anim, out Appearance ap, out Character placeholder)
    {
        placeholder = null;
        Appearance appearance = GameRefs.LocalAppearance;
        Animator animator = GameRefs.LocalAnimator;
        GameObject source = GameRefs.LocalRig;
        PlayerLocator locator = PlayerLocator.instance;
        if (locator == null || appearance == null || animator == null || source == null)
        {
            throw new InvalidOperationException("local player body is not ready");
        }
        Transform frame = locator.transform;
        Vector3 localPos = frame.InverseTransformPoint(source.transform.position);
        Quaternion localRot = Quaternion.Inverse(frame.rotation) * source.transform.rotation;
        GameObject body = (GameObject)UnityEngine.Object.Instantiate(source, shell);
        try
        {
            Transform src = source.transform;
            Transform dst = body.transform;
            Transform animTr = Counterpart(animator.transform, src, dst);
            Transform apTr = Counterpart(appearance.transform, src, dst);
            RemoveHeldItems(src, dst);
            Strip(body);
            EnableRenderers(dst);
            anim = animTr.GetComponent<Animator>();
            ap = apTr.GetComponent<Appearance>();
            if (anim == null || ap == null) throw new InvalidOperationException("copied rig has no Animator or Appearance");
            Drive(anim, animator);
            CheckInside(dst, frame, ap);
            placeholder = Placeholder();
            ap.createdCharacter = placeholder;
            CopyMaterials(body, materials);
            foreach (Transform t in body.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.tag = "Untagged";
            }
            bool rootMotion = anim.applyRootMotion;
            anim.applyRootMotion = false;
            anim.fireEvents = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            dst.localPosition = localPos;
            dst.localRotation = localRot;
            body.SetActive(true);
            Diagnose(anim, dst, rootMotion, animator);
        }
        catch
        {
            if (placeholder != null) UnityEngine.Object.DestroyImmediate(placeholder);
            UnityEngine.Object.DestroyImmediate(body);
            throw;
        }
    }

    private static void Drive(Animator anim, Animator local)
    {
        Animator game = OpenSewerCharacterController.instance != null ? OpenSewerCharacterController.instance.animator : null;
        RuntimeAnimatorController controller = local != null ? local.runtimeAnimatorController : null;
        if (controller == null && game != null && game != local) controller = game.runtimeAnimatorController;
        Avatar avatar = local != null ? local.avatar : null;
        if (avatar == null && game != null && game != local) avatar = game.avatar;
        if (controller == null) CoopLog.Once("PuppetBody.controller", "the local player animator has no runtimeAnimatorController, the puppet keeps its bind pose");
        else anim.runtimeAnimatorController = controller;
        if (avatar != null) anim.avatar = avatar;
    }

    private static Character Placeholder()
    {
        Character character = ScriptableObject.CreateInstance<Character>();
        character.name = PlaceholderName;
        character.GenderString = "Male";
        character.Advanced = false;
        character.Height = NeutralHeight;
        character.Weight = 0f;
        character.Gender = 0f;
        character.HeadSize = 1f;
        character.FaceSize = 1f;
        character.NeckLength = 0f;
        character.ShoulderWidth = 0f;
        character.BreastSize = 0f;
        character.HipWidth = 0f;
        character.HandSize = 1f;
        character.FootSize = 1f;
        character.LipThickness = 0f;
        character.JawPosition = 0f;
        character.ChinPosition = 0f;
        character.EyeSize = 1f;
        character.EyeDistance = 0f;
        character.EyePosition = 0f;
        character.EyeDepth = 0f;
        character.EarSize = 1f;
        character.EarRotation = 0f;
        character.NoseRootRotation = 0f;
        character.NoseTipRotation = 0f;
        character.NoseWidth = 0f;
        return character;
    }

    private static void Diagnose(Animator anim, Transform body, bool rootMotionBefore, Animator localAnimator)
    {
        AnimatorControllerParameter[] parameters = anim.parameters;
        List<string> names = new List<string>();
        foreach (AnimatorControllerParameter parameter in parameters)
        {
            names.Add(parameter.name + ":" + parameter.type);
        }
        List<string> missing = new List<string>();
        foreach (string name in PoseParams)
        {
            if (!Array.Exists(parameters, p => p.name == name)) missing.Add(name);
        }
        int on = 0;
        int active = 0;
        Renderer[] renderers = body.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            if (!renderer.enabled) continue;
            on++;
            if (renderer.gameObject.activeInHierarchy) active++;
        }
        Avatar avatar = anim.avatar;
        RuntimeAnimatorController controller = anim.runtimeAnimatorController;
        bool gameDrives = OpenSewerCharacterController.instance != null && OpenSewerCharacterController.instance.animator == localAnimator;
        CoopLog.Log.Info(string.Format(
            "Puppet body: animator {0}, controller {1}, avatar {2}, isHuman {3}, applyRootMotion was {4} now {5}, cullingMode {6}, same animator as the game controller {7}, parameters [{8}], missing [{9}], renderers enabled {10}/{11}, active in hierarchy {12}.",
            anim.name, controller != null ? controller.name : "none", avatar != null ? avatar.name : "none", avatar != null && avatar.isHuman, rootMotionBefore, anim.applyRootMotion, anim.cullingMode, gameDrives,
            string.Join(", ", names.ToArray()), missing.Count == 0 ? "none" : string.Join(", ", missing.ToArray()), on, renderers.Length, active));
    }

    private static Transform Counterpart(Transform from, Transform fromRoot, Transform toRoot)
    {
        if (from == fromRoot) return toRoot;
        return Counterpart(from.parent, fromRoot, toRoot).GetChild(from.GetSiblingIndex());
    }

    private static void EnableRenderers(Transform dst)
    {
        foreach (Renderer renderer in dst.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
    }

    private static void RemoveHeldItems(Transform src, Transform dst)
    {
        PlayerHandItems items = PlayerHandItems.instance;
        if (items == null) return;
        List<GameObject> copies = new List<GameObject>();
        foreach (string name in HeldFields)
        {
            FieldInfo field = typeof(PlayerHandItems).GetField(name, InstanceMembers);
            GameObject held = field == null ? null : field.GetValue(items) as GameObject;
            if (held == null || !held.transform.IsChildOf(src)) continue;
            copies.Add(Counterpart(held.transform, src, dst).gameObject);
        }
        foreach (GameObject copy in copies)
        {
            UnityEngine.Object.DestroyImmediate(copy);
        }
    }

    private static void Strip(GameObject root)
    {
        Component[] components = root.GetComponentsInChildren<Component>(true);
        for (int i = components.Length - 1; i >= 0; i--)
        {
            Component component = components[i];
            if (component == null || !(component is MonoBehaviour) || Kept(component)) continue;
            UnityEngine.Object.DestroyImmediate(component);
        }
        for (int i = components.Length - 1; i >= 0; i--)
        {
            Component component = components[i];
            if (component == null || component is MonoBehaviour || Kept(component)) continue;
            UnityEngine.Object.DestroyImmediate(component);
        }
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component != null && !Kept(component))
            {
                throw new InvalidOperationException("copied " + component.GetType().Name + " survived the strip");
            }
        }
    }

    private static bool Kept(Component component)
    {
        return component is Transform || component is SkinnedMeshRenderer || component is MeshRenderer || component is MeshFilter || component is Animator || component is Appearance || IsRigging(component);
    }

    private static bool IsRigging(Component component)
    {
        return component.GetType().Namespace == RiggingNamespace;
    }

    private static void CopyMaterials(GameObject root, List<Material> copies)
    {
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] shared = renderer.sharedMaterials;
            for (int i = 0; i < shared.Length; i++)
            {
                if (shared[i] == null) continue;
                shared[i] = new Material(shared[i]);
                copies.Add(shared[i]);
            }
            renderer.sharedMaterials = shared;
        }
    }

    private static void CheckInside(Transform root, Transform playerRoot, Appearance ap)
    {
        foreach (SkinnedMeshRenderer mesh in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            RequireInside(mesh.rootBone, root, "rootBone");
            foreach (Transform bone in mesh.bones)
            {
                RequireInside(bone, root, "bone");
            }
        }
        foreach (FieldInfo field in typeof(Appearance).GetFields(InstanceMembers))
        {
            object value = field.GetValue(ap);
            Transform target = value as Transform;
            GameObject go = value as GameObject;
            if (go != null) target = go.transform;
            RequireInside(target, root, field.Name);
            if (InPlayer(value, playerRoot)) throw new InvalidOperationException("copied " + field.Name + " points into the source player");
        }
        CheckRigging(root);
    }

    private static void CheckRigging(Transform root)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null || !IsRigging(component) || FieldsInside(component, root, 0)) continue;
            CoopLog.Once("PuppetBody.rigging", "copied " + component.GetType().Name + " points outside the copy, rigging stripped");
            StripRigging(root);
            return;
        }
    }

    private static void StripRigging(Transform root)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component != null && IsRigging(component)) UnityEngine.Object.DestroyImmediate(component);
        }
    }

    private static bool RefsInside(object value, Transform root, int depth)
    {
        if (value == null || depth > MaxRefDepth || value is string || value is Delegate) return true;
        Component component = value as Component;
        if (component != null) return component.transform.IsChildOf(root);
        GameObject go = value as GameObject;
        if (go != null) return go.transform.IsChildOf(root);
        if (value is UnityEngine.Object) return true;
        IEnumerable items = value as IEnumerable;
        if (items != null)
        {
            foreach (object item in items)
            {
                if (!RefsInside(item, root, depth + 1)) return false;
            }
            return true;
        }
        return FieldsInside(value, root, depth);
    }

    private static bool InPlayer(object value, Transform playerRoot)
    {
        Component component = value as Component;
        if (component != null) return component.transform.IsChildOf(playerRoot);
        GameObject go = value as GameObject;
        if (go != null) return go.transform.IsChildOf(playerRoot);
        IEnumerable items = value as IEnumerable;
        if (items == null) return false;
        foreach (object item in items)
        {
            if (InPlayer(item, playerRoot)) return true;
        }
        return false;
    }

    private static bool FieldsInside(object value, Transform root, int depth)
    {
        for (Type type = value.GetType(); type != null && type != typeof(object) && type != typeof(UnityEngine.Object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(InstanceMembers | BindingFlags.DeclaredOnly))
            {
                if (!RefsInside(field.GetValue(value), root, depth + 1)) return false;
            }
        }
        return true;
    }

    private static void RequireInside(Transform target, Transform root, string what)
    {
        if (target != null && !target.IsChildOf(root))
        {
            throw new InvalidOperationException("copied " + what + " points outside the copy");
        }
    }
}
