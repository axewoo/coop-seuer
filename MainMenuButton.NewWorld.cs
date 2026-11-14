using System;
using ObenseuerCore;

internal static partial class MainMenuButton
{
    internal const string NewWorldLabel = "New co-op world";
    private const string NewWorldNotice = "A character with the same name and its co-op folder will be deleted when you start a new game.";
    private const string NewWorldConfirm = "Press New co-op world again to confirm: a character with the same name and its co-op folder is deleted when the new game starts.";
    private const string NewWorldFailed = "The main menu is not open, so a new game cannot start.";
    private const int NewCall = 2;
    private static bool newWorldArmed;

    private static bool NewWorldPressed()
    {
        if (MainMenu.Instance == null)
        {
            notice = NewWorldFailed;
            return false;
        }
        if (!newWorldArmed)
        {
            newWorldArmed = true;
            notice = NewWorldConfirm;
            CoopNotice.Show("newgame|delete", NewWorldNotice, 10f);
            return true;
        }
        newWorldArmed = false;
        if (!Session.IsHost && !Session.Host()) return false;
        closePending = true;
        pendingCall = NewCall;
        return true;
    }

    private static void NewWorld()
    {
        try
        {
            MainMenu.Instance.NewGame();
        }
        catch (Exception e)
        {
            CoopLog.Once("MainMenuButton.newworld", e);
        }
    }
}
