using Newtonsoft.Json;
using ObenseuerCore;
using UnityEngine;

public class PageInput
{
    public string type;
    public string action;
    public string dir;
}

internal static class LobbyInput
{
    private const float Repeat = 0.25f;
    private const float AxisDead = 0.5f;
    private const float EscapeGuard = 0.3f;
    private static float nextMove;

    public static void Forward(WebView view, bool ready)
    {
        if (view == null || !ready) return;
        if (Input.GetKeyDown(KeyCode.Joystick1Button0))
        {
            Send(view, "press", null);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Joystick1Button1))
        {
            Send(view, "back", null);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (!LobbyPage.PageSawEscape(EscapeGuard)) Send(view, "back", null);
            return;
        }
        string dir = Direction();
        if (dir == null || Time.unscaledTime < nextMove) return;
        nextMove = Time.unscaledTime + Repeat;
        Send(view, "move", dir);
    }

    private static string Direction()
    {
        float vertical = Input.GetAxisRaw("DPadVertical") + Input.GetAxisRaw("LeftStickInventoryVertical");
        if (vertical >= AxisDead) return "up";
        if (vertical <= -AxisDead) return "down";
        float horizontal = Input.GetAxisRaw("DPadHorizontal") + Input.GetAxisRaw("LeftStickInventoryHorizontal");
        if (horizontal >= AxisDead) return "right";
        if (horizontal <= -AxisDead) return "left";
        return null;
    }

    private static void Send(WebView view, string action, string dir)
    {
        PageInput message = new PageInput();
        message.type = "input";
        message.action = action;
        message.dir = dir;
        view.Post(JsonConvert.SerializeObject(message));
    }
}
