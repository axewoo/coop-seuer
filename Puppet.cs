using System;
using System.Collections.Generic;
using System.Reflection;
using ObenseuerCore;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class Puppet
{
    private const float Smoothing = 10f;
    private const float LabelHeight = 2.1f;
    private const float LabelSize = 3f;
    private const float AnimDamp = 0.1f;
    private const float RunSpeed = 5f;
    private const float CrouchSpeed = 1f;
    private const float FaceSpeed = 0.2f;
    private const float BuildRetryInterval = 1f;
    private const float MaxBuildRetryInterval = 30f;
    private const float ApplyRetryDelay = 0.25f;
    private const float ParamsRetryInterval = 1f;
    private const int MaxApplyFails = 20;
    private const string SitParam = "SitArmsFolded";
    private static readonly FieldInfo BodyModels = typeof(Appearance).GetField("bodyModels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly Dictionary<ulong, Figure> figures = new Dictionary<ulong, Figure>();
    private static readonly Dictionary<ulong, Look> looks = new Dictionary<ulong, Look>();
    private static readonly HashSet<ulong> suppressed = new HashSet<ulong>();
    private static int waitScene = int.MinValue;

    private sealed class Figure
    {
        public GameObject Shell;
        public Animator Anim;
        public Dictionary<string, AnimatorControllerParameterType> Params;
        public float ParamsAt;
        public Appearance Ap;
        public TextMeshPro Label;
        public string Name;
        public Vector3 Goal;
        public float GoalYaw;
        public Vector3 MoveDir;
        public Look Applied;
        public Character Placeholder;
        public int ApplyFails;
        public float ApplyAt;
        public bool Visible;
        public bool Built;
        public float BuildAt;
        public float BuildDelay;
        public int BuildScene = int.MinValue;
        public readonly List<Material> Materials = new List<Material>();
    }

    public static void Target(ulong id, string name, Vector3 pos, float yaw)
    {
        try
        {
            Figure figure;
            if (!figures.TryGetValue(id, out figure) || figure.Shell == null)
            {
                figure = Create(name, pos, yaw);
                figures[id] = figure;
            }
            if (figure.Name != name) Rename(figure, name);
            Vector3 move = pos - figure.Goal;
            move.y = 0f;
            if (move.sqrMagnitude > 0f) figure.MoveDir = move;
            figure.Goal = pos;
            figure.GoalYaw = yaw;
        }
        catch (Exception e)
        {
            CoopLog.Once("Puppet.Target", e);
        }
    }

    public static void SetLook(ulong id, string text)
    {
        try
        {
            Look last;
            if (looks.TryGetValue(id, out last) && last.Raw == text) return;
            Look look;
            if (!Look.Parse(text, out look))
            {
                CoopLog.Log.Debug("puppet.look", "ignored malformed APP from " + id);
                return;
            }
            looks[id] = look;
            Figure puppet;
            if (figures.TryGetValue(id, out puppet)) puppet.ApplyFails = 0;
        }
        catch (Exception e)
        {
            CoopLog.Once("Puppet.SetLook", e);
        }
    }

    public static bool HasLook(ulong id)
    {
        return looks.ContainsKey(id);
    }

    public static void Suppress(ulong id, bool on)
    {
        if (on)
        {
            if (!suppressed.Add(id)) return;
            SetVisible(id, false);
            return;
        }
        suppressed.Remove(id);
    }

    public static Transform ShellOf(ulong id)
    {
        Figure figure;
        return figures.TryGetValue(id, out figure) && figure.Shell != null && figure.Visible ? figure.Shell.transform : null;
    }

    public static void SetVisible(ulong id, bool visible)
    {
        Figure figure;
        if (!figures.TryGetValue(id, out figure) || figure.Shell == null || figure.Visible == visible) return;
        if (visible && suppressed.Contains(id)) return;
        if (visible && !figure.Built && !TryBuildBody(id, figure)) return;
        figure.Visible = visible;
        if (visible)
        {
            figure.Shell.transform.position = figure.Goal;
            figure.Shell.transform.rotation = Quaternion.Euler(0f, figure.GoalYaw, 0f);
            figure.Applied = null;
        }
        figure.Shell.SetActive(visible);
        if (visible && figure.Anim != null && figure.Anim.runtimeAnimatorController != null)
        {
            FillParams(figure);
            figure.ParamsAt = 0f;
        }
    }

    public static void Remove(ulong id)
    {
        Figure figure;
        if (!figures.TryGetValue(id, out figure)) return;
        figures.Remove(id);
        Destroy(figure);
    }

    public static void RemoveAll()
    {
        foreach (ulong id in new List<ulong>(figures.Keys))
        {
            Remove(id);
        }
    }

    public static void ForgetAll()
    {
        RemoveAll();
        looks.Clear();
    }

    public static void Tick(float dt)
    {
        try
        {
            if (figures.Count == 0) return;
            float blend = 1f - Mathf.Exp(-Smoothing * dt);
            Camera camera = Camera.main;
            foreach (KeyValuePair<ulong, Figure> pair in figures)
            {
                Figure figure = pair.Value;
                if (figure.Shell == null || !figure.Visible) continue;
                SitWire.SitRecord seat = SitSync.Get(pair.Key);
                if (seat != null) Seat(figure, seat, blend, dt);
                else Walk(pair.Key, figure, blend, dt);
                if (figure.Ap != null && Ready(figure.Ap)) ApplyLook(pair.Key, figure);
                if (figure.Label != null && camera != null) figure.Label.transform.rotation = camera.transform.rotation;
            }
        }
        catch (Exception e)
        {
            CoopLog.Once("Puppet.Tick", e);
        }
    }

    private static void Walk(ulong id, Figure figure, float blend, float dt)
    {
        Transform tr = figure.Shell.transform;
        Vector3 before = tr.position;
        tr.position = Vector3.Lerp(before, figure.Goal, blend);
        PoseSync.PoseRecord pose = PoseSync.Get(id);
        float speed = PlanarSpeed(pose, tr, before, dt);
        bool moving = speed > FaceSpeed && figure.MoveDir.sqrMagnitude > 0f;
        float faceYaw = moving ? Mathf.Atan2(figure.MoveDir.x, figure.MoveDir.z) * Mathf.Rad2Deg : figure.GoalYaw;
        float bodyYaw = tr.eulerAngles.y;
        tr.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(bodyYaw, faceYaw, blend), 0f);
        Animate(figure, pose, speed, Mathf.DeltaAngle(bodyYaw, faceYaw) * Mathf.Deg2Rad, dt, false);
    }

    private static void Seat(Figure figure, SitWire.SitRecord seat, float blend, float dt)
    {
        Transform tr = figure.Shell.transform;
        tr.position = Vector3.Lerp(tr.position, new Vector3(seat.X, seat.Y, seat.Z), blend);
        tr.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(tr.eulerAngles.y, seat.Yaw, blend), 0f);
        Animate(figure, null, 0f, 0f, dt, true);
    }

    private static float PlanarSpeed(PoseSync.PoseRecord pose, Transform tr, Vector3 before, float dt)
    {
        if (pose != null && pose.Speed.HasValue) return pose.Speed.Value;
        if (dt <= 0f) return 0f;
        Vector3 step = tr.position - before;
        step.y = 0f;
        return step.magnitude / dt;
    }

    private static void Animate(Figure figure, PoseSync.PoseRecord pose, float speed, float turn, float dt, bool seated)
    {
        if (figure.Anim == null || dt <= 0f) return;
        EnsureParams(figure);
        bool grounded = pose == null || (pose.Grounded ?? true);
        if (Has(figure, "Forward", AnimatorControllerParameterType.Float))
        {
            bool crouching = pose != null && pose.Crouching == true;
            figure.Anim.SetFloat("Forward", Mathf.InverseLerp(0f, crouching ? LocalCrouchSpeed() : LocalRunSpeed(), speed), AnimDamp, dt);
        }
        if (Has(figure, "Turn", AnimatorControllerParameterType.Float))
        {
            figure.Anim.SetFloat("Turn", turn, AnimDamp, dt);
        }
        if (Has(figure, "Crouch", AnimatorControllerParameterType.Bool)) figure.Anim.SetBool("Crouch", pose != null && pose.Crouching == true);
        if (Has(figure, "OnGround", AnimatorControllerParameterType.Bool)) figure.Anim.SetBool("OnGround", grounded);
        if (Has(figure, SitParam, AnimatorControllerParameterType.Bool)) figure.Anim.SetBool(SitParam, seated);
        else if (seated) CoopLog.Once("Puppet.sit", "the puppet controller has no " + SitParam + " bool, a seated peer is placed on the seat but keeps the standing pose");
        if (!grounded && Has(figure, "Jump", AnimatorControllerParameterType.Float))
        {
            figure.Anim.SetFloat("Jump", pose.VerticalSpeed ?? 0f, AnimDamp, dt);
        }
    }

    private static bool Has(Figure figure, string name, AnimatorControllerParameterType type)
    {
        AnimatorControllerParameterType actual;
        return figure.Params.TryGetValue(name, out actual) && actual == type;
    }

    private static void EnsureParams(Figure figure)
    {
        if (figure.Params == null) figure.Params = new Dictionary<string, AnimatorControllerParameterType>();
        if (figure.Params.Count > 0) return;
        if (figure.Anim.runtimeAnimatorController == null) return;
        float now = Time.unscaledTime;
        if (now < figure.ParamsAt) return;
        figure.ParamsAt = now + ParamsRetryInterval;
        FillParams(figure);
    }

    private static void FillParams(Figure figure)
    {
        figure.Params = new Dictionary<string, AnimatorControllerParameterType>();
        foreach (AnimatorControllerParameter parameter in figure.Anim.parameters) figure.Params[parameter.name] = parameter.type;
    }

    private static float LocalRunSpeed()
    {
        OpenSewerCharacterController controller = OpenSewerCharacterController.instance;
        float run = controller != null ? controller.runSpeed : 0f;
        return run > 0f ? run : RunSpeed;
    }

    private static float LocalCrouchSpeed()
    {
        OpenSewerCharacterController controller = OpenSewerCharacterController.instance;
        float crouch = controller != null ? controller.crouchSpeed : 0f;
        return crouch > 0f ? crouch : CrouchSpeed;
    }

    private static bool Ready(Appearance ap)
    {
        return BodyModels != null && BodyModels.GetValue(ap) != null;
    }

    private static string Short(ulong id)
    {
        return (id % 10000UL).ToString("0000");
    }

    private static string SceneName()
    {
        Scene scene = SceneManager.GetActiveScene();
        return string.IsNullOrEmpty(scene.name) ? "the current scene" : scene.name;
    }

    private static void ApplyLook(ulong id, Figure figure)
    {
        Look wanted;
        if (!looks.TryGetValue(id, out wanted) || wanted == figure.Applied || Time.unscaledTime < figure.ApplyAt) return;
        try
        {
            if (PlayerLook.Apply(wanted.ToCore(figure.Ap), figure.Ap))
            {
                figure.ApplyFails = 0;
                figure.Applied = wanted;
                ReleasePlaceholder(figure);
                CoopLog.Log.Info("puppet look applied to peer " + Short(id) + " in " + SceneName());
                return;
            }
            Refused(id, figure, wanted);
        }
        catch (Exception e)
        {
            CoopLog.Once("Puppet.ApplyLook", e);
            Refused(id, figure, wanted);
        }
    }

    private static void ReleasePlaceholder(Figure figure)
    {
        Character placeholder = figure.Placeholder;
        if (placeholder == null) return;
        if (figure.Ap != null && figure.Ap.createdCharacter == placeholder) return;
        figure.Placeholder = null;
        UnityEngine.Object.Destroy(placeholder);
    }

    private static void Refused(ulong id, Figure figure, Look wanted)
    {
        if (SaveData.Busy)
        {
            figure.ApplyAt = Time.unscaledTime + ApplyRetryDelay;
            return;
        }
        figure.ApplyFails++;
        if (figure.ApplyFails < MaxApplyFails)
        {
            figure.ApplyAt = Time.unscaledTime + ApplyRetryDelay;
            CoopLog.Once("Puppet.ApplyLook.refused", "PlayerLook.Apply did not apply the look, retrying");
            if (CoopDebug.On("look")) CoopDebug.Event("look", "refused", "the look of peer " + Short(id) + " was refused by PlayerLook.Apply, attempt " + figure.ApplyFails + " of " + MaxApplyFails + " in " + SceneName());
            return;
        }
        figure.Applied = wanted;
        CoopLog.Log.Error("coop: the look of peer " + Short(id) + " could not be applied in " + MaxApplyFails + " tries, it is left as the game rolled it");
        if (CoopDebug.On("look")) CoopDebug.Event("look", "giveup", "the look of peer " + Short(id) + " was refused " + MaxApplyFails + " times, it is taken as applied in " + SceneName());
    }

    private static Figure Create(string name, Vector3 pos, float yaw)
    {
        GameObject shell = new GameObject("CoopPuppet");
        shell.SetActive(false);
        Figure figure = new Figure { Shell = shell, Name = name, Goal = pos, GoalYaw = yaw };
        try
        {
            figure.Label = BuildLabel(shell, name);
            shell.transform.position = pos;
            shell.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            return figure;
        }
        catch
        {
            Destroy(figure);
            throw;
        }
    }

    private static bool TryBuildBody(ulong id, Figure figure)
    {
        float now = Time.unscaledTime;
        int scene = SceneManager.GetActiveScene().handle;
        if (figure.BuildScene != scene)
        {
            figure.BuildScene = scene;
            figure.BuildDelay = 0f;
            figure.BuildAt = 0f;
        }
        if (now < figure.BuildAt) return false;
        figure.BuildAt = now + BuildRetryInterval;
        if (!GameRefs.BodyReady)
        {
            LogWaitingOnce();
            return false;
        }
        bool active = figure.Shell.activeSelf;
        figure.Shell.SetActive(false);
        try
        {
            Animator anim;
            Appearance ap;
            Character placeholder;
            try
            {
                PuppetBody.Build(figure.Shell.transform, figure.Materials, out anim, out ap, out placeholder);
            }
            catch (Exception e)
            {
                CoopLog.Once("Puppet.body", e);
                figure.BuildDelay = figure.BuildDelay > 0f ? Mathf.Min(figure.BuildDelay * 2f, MaxBuildRetryInterval) : BuildRetryInterval;
                figure.BuildAt = now + figure.BuildDelay;
                return false;
            }
            figure.Anim = anim;
            FillParams(figure);
            figure.Ap = ap;
            figure.Placeholder = placeholder;
            figure.Applied = null;
            figure.Built = true;
            figure.BuildDelay = 0f;
            if (Ready(figure.Ap)) ApplyLook(id, figure);
            return true;
        }
        finally
        {
            figure.Shell.SetActive(active);
        }
    }

    private static void LogWaitingOnce()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.handle == waitScene) return;
        waitScene = scene.handle;
        CoopLog.Log.Info("Puppets wait for the local player body in scene " + scene.name);
    }

    private static void Destroy(Figure figure)
    {
        if (figure.Shell != null) UnityEngine.Object.Destroy(figure.Shell);
        ReleasePlaceholder(figure);
        if (figure.Ap != null && figure.Ap.createdCharacter != null) UnityEngine.Object.Destroy(figure.Ap.createdCharacter);
        foreach (Material material in figure.Materials)
        {
            if (material != null) UnityEngine.Object.Destroy(material);
        }
    }

    private static TextMeshPro BuildLabel(GameObject shell, string name)
    {
        TMP_FontAsset font = TMP_Settings.defaultFontAsset;
        if (font == null)
        {
            CoopLog.Once("Puppet.font", "TMP default font is null, puppets have no name label");
            return null;
        }
        GameObject go = new GameObject("Name");
        go.transform.SetParent(shell.transform, false);
        go.transform.localPosition = new Vector3(0f, LabelHeight, 0f);
        TextMeshPro text = go.AddComponent<TextMeshPro>();
        text.font = font;
        text.fontSize = LabelSize;
        text.alignment = TextAlignmentOptions.Center;
        text.text = name;
        return text;
    }

    private static void Rename(Figure figure, string name)
    {
        figure.Name = name;
        if (figure.Label != null) figure.Label.text = name;
    }
}
