using ObenseuerCore;

internal static class LobbyKeys
{
    public static bool Busy()
    {
        ThirdPersonCameraCollision view = ThirdPersonCameraCollision.instance;
        if (view == null) return false;
        if (view.controlsDisabled) return true;
        if (GameUIController.instance != null && GameUIController.instance.IsMenuVisible) return true;
        if (ShopController.instance != null && ShopController.instance.menuIsOpen) return true;
        if (PauseMenu.instance != null && PauseMenu.instance.PauseIsActive()) return true;
        if (DialogueController.instance != null && DialogueController.instance.DialogueActive) return true;
        if (DeveloperConsole.Instance != null && DeveloperConsole.Instance.consoleOpened) return true;
        return UiState.IsPaused || UiState.IsInputBlocked;
    }
}
