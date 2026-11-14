using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class GameRefs
{
    private const float BodyScanInterval = 0.5f;
    private const int RigBonus = 1000;
    private const int FieldBonus = 500;
    private static readonly FieldInfo BodyModels = typeof(Appearance).GetField("bodyModels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static int areaHandle = int.MinValue;
    private static string areaId;
    private static int bodyScene = int.MinValue;
    private static float bodyNextScan;
    private static int bodyScanScene = int.MinValue;
    private static Appearance bodyAppearance;
    private static Animator bodyAnimator;
    private static GameObject bodyRig;

    public static Transform LocalPlayer
    {
        get
        {
            if (MainMenu.Instance != null) return null;
            PlayerLocator locator = PlayerLocator.instance;
            return locator != null ? locator.transform : null;
        }
    }

    public static Appearance LocalAppearance
    {
        get
        {
            ResolveBody();
            return bodyAppearance;
        }
    }

    public static Animator LocalAnimator
    {
        get
        {
            ResolveBody();
            return bodyAnimator;
        }
    }

    public static GameObject LocalRig
    {
        get
        {
            ResolveBody();
            return bodyRig;
        }
    }

    public static bool BodyReady
    {
        get
        {
            Appearance appearance = LocalAppearance;
            return appearance != null && appearance.maleHeads != null && LocalAnimator != null && LocalRig != null;
        }
    }

    public static bool AppearanceReady(Appearance appearance)
    {
        return appearance != null && BodyModels != null && BodyModels.GetValue(appearance) != null;
    }

    public static string AreaId
    {
        get
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.handle != areaHandle)
            {
                areaHandle = scene.handle;
                areaId = string.IsNullOrEmpty(scene.name) ? null : scene.name;
            }
            return areaId;
        }
    }

    private static void ResolveBody()
    {
        int scene = SceneManager.GetActiveScene().handle;
        if (scene == bodyScene && bodyAppearance != null) return;
        if (scene == bodyScanScene && Time.unscaledTime < bodyNextScan) return;
        bodyScanScene = scene;
        bodyNextScan = Time.unscaledTime + BodyScanInterval;
        bodyAppearance = null;
        bodyAnimator = null;
        bodyRig = null;
        PlayerLocator locator = PlayerLocator.instance;
        if (MainMenu.Instance != null || locator == null) return;
        Animator driven = DrivenAnimator(locator);
        GameObject rig = RigOf(locator, driven);
        Appearance appearance = rig != null ? AppearanceIn(locator, rig.transform, driven) : null;
        Animator animator = rig != null ? AnimatorIn(rig.transform, driven) : null;
        if (appearance == null || animator == null)
        {
            appearance = FieldAppearance(locator, driven);
            if (appearance == null) return;
            animator = Usable(driven, locator) && driven.runtimeAnimatorController != null ? driven : AnimatorFor(locator, appearance, driven);
            rig = RigFor(locator, appearance, animator);
        }
        if (appearance == null || animator == null || rig == null) return;
        bodyAppearance = appearance;
        bodyAnimator = animator;
        bodyRig = rig;
        bodyScene = scene;
        CoopLog.Log.Info(string.Format(
            "Local body: appearance {0}, animator {1}, rig {2}, controller {3}. PlayerLocator fields set: appearance {4}, animator {5}, rig {6}.",
            PathOf(bodyAppearance.transform),
            PathOf(bodyAnimator.transform),
            PathOf(bodyRig.transform),
            bodyAnimator.runtimeAnimatorController != null ? bodyAnimator.runtimeAnimatorController.name : "none",
            locator.playerAppearance != null, locator.playerAnimator != null, locator.playerRig != null));
    }

    private static Animator DrivenAnimator(PlayerLocator locator)
    {
        OpenSewerCharacterController controller = OpenSewerCharacterController.instance;
        Animator driven = controller != null ? controller.animator : null;
        if (Usable(driven, locator)) return driven;
        return Usable(locator.playerAnimator, locator) ? locator.playerAnimator : null;
    }

    private static bool Usable(Animator animator, PlayerLocator locator)
    {
        if (animator == null) return false;
        Transform t = animator.transform;
        return (t == locator.transform || t.IsChildOf(locator.transform)) && !IsFirstPerson(t);
    }

    private static GameObject RigOf(PlayerLocator locator, Animator animator)
    {
        if (animator == null) return null;
        foreach (GameObject named in new GameObject[] { locator.playerRig, locator.playerRigUnrigified })
        {
            if (named != null && named.transform != locator.transform && named.transform.IsChildOf(locator.transform) && animator.transform.IsChildOf(named.transform)) return named;
        }
        Transform stop = locator.transform;
        bool inside = false;
        for (Transform t = animator.transform; t != null; t = t.parent)
        {
            if (t == stop)
            {
                inside = true;
                break;
            }
        }
        if (!inside) return null;
        GameObject best = null;
        for (Transform t = animator.transform; t != null && t != stop; t = t.parent)
        {
            if (AppearanceIn(locator, t, animator) != null) best = t.gameObject;
        }
        return best;
    }

    private static Appearance FieldAppearance(PlayerLocator locator, Animator animator)
    {
        if (locator.playerAppearance != null && !IsFirstPerson(locator.playerAppearance.transform)) return locator.playerAppearance;
        Appearance own = locator.GetComponent<Appearance>();
        if (own != null && !IsFirstPerson(own.transform)) return own;
        return NearestAppearance(locator, animator);
    }

    private static Appearance NearestAppearance(PlayerLocator locator, Animator animator)
    {
        Appearance best = null;
        int bestScore = int.MinValue;
        foreach (Appearance candidate in locator.GetComponentsInChildren<Appearance>(true))
        {
            if (candidate == null) continue;
            int score = IsFirstPerson(candidate.transform) ? -RigBonus : 0;
            if (candidate == locator.playerAppearance) score += FieldBonus;
            if (candidate.transform.parent == locator.transform) score += 100;
            if (animator != null) score += Depth(CommonParent(candidate.transform, animator.transform));
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private static Appearance AppearanceIn(PlayerLocator locator, Transform rig, Animator animator)
    {
        Appearance best = null;
        int bestScore = int.MinValue;
        foreach (Appearance candidate in rig.GetComponentsInChildren<Appearance>(true))
        {
            if (candidate == null || IsFirstPerson(candidate.transform)) continue;
            int score = 0;
            if (candidate == locator.playerAppearance) score += FieldBonus;
            if (animator != null)
            {
                if (candidate.transform.IsChildOf(animator.transform)) score += RigBonus;
                score += Depth(CommonParent(candidate.transform, animator.transform));
            }
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private static Animator AnimatorIn(Transform rig, Animator driven)
    {
        if (driven != null && driven.transform.IsChildOf(rig)) return driven;
        foreach (Animator candidate in rig.GetComponentsInChildren<Animator>(true))
        {
            if (candidate == null || IsFirstPerson(candidate.transform)) continue;
            if (candidate.runtimeAnimatorController != null) return candidate;
        }
        return null;
    }

    private static Animator AnimatorFor(PlayerLocator locator, Appearance appearance, Animator driven)
    {
        Animator best = null;
        int bestScore = int.MinValue;
        foreach (Animator candidate in locator.GetComponentsInChildren<Animator>(true))
        {
            if (!Usable(candidate, locator)) continue;
            int score = candidate.runtimeAnimatorController != null ? 100 : 0;
            if (candidate == driven) score += 50;
            if (appearance != null) score += Depth(CommonParent(appearance.transform, candidate.transform));
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        if (best != null) return best;
        return Usable(driven, locator) ? driven : null;
    }

    private static GameObject RigFor(PlayerLocator locator, Appearance appearance, Animator animator)
    {
        if (appearance == null || animator == null) return null;
        GameObject shaped = RigOf(locator, animator);
        if (shaped != null && appearance.transform.IsChildOf(shaped.transform)) return shaped;
        foreach (GameObject candidate in new GameObject[] { locator.playerRig, locator.playerRigUnrigified })
        {
            if (candidate != null && candidate.transform != locator.transform && candidate.transform.IsChildOf(locator.transform) && appearance.transform.IsChildOf(candidate.transform) && animator.transform.IsChildOf(candidate.transform)) return candidate;
        }
        Transform common = CommonParent(appearance.transform, animator.transform);
        return common != null && common != locator.transform && common.IsChildOf(locator.transform) ? common.gameObject : null;
    }

    private static bool IsFirstPerson(Transform t)
    {
        return t.GetComponentInParent<FirstPersonHands>() != null;
    }

    private static Transform CommonParent(Transform a, Transform b)
    {
        for (Transform t = a; t != null; t = t.parent)
        {
            if (b.IsChildOf(t)) return t;
        }
        return null;
    }

    private static int Depth(Transform t)
    {
        int depth = 0;
        for (; t != null; t = t.parent) depth++;
        return depth;
    }

    private static string PathOf(Transform t)
    {
        return t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
    }
}
