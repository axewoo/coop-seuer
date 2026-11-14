using System;
using System.Collections.Generic;

internal static partial class MainMenuButton
{
    internal const string CancelWaitLabel = "Cancel";

    internal static void WaitLabels(List<string> labels, List<Func<bool>> actions)
    {
        labels.Add(CancelWaitLabel);
        actions.Add(Session.CancelHostReload);
    }
}
