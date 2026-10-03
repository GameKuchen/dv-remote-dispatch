using System;
using System.Collections.Generic;

namespace DvMod.RemoteDispatch
{
    public sealed class SignalAspectInfo
    {
        public string Id = "";
        public bool DisallowPassing, ShuntingPermission, ShuntingDenied, StopLamp;
    }

    public static class SignalAspectRules
    {
        public static int SelectStop(IReadOnlyList<SignalAspectInfo> aspects, bool advanceWarning)
        {
            for (int i = 0; i < aspects.Count; i++)
            {
                var a = aspects[i];
                string id = a.Id.ToUpperInvariant();
                if (id == "MS1" || id == "S1" || id == "SP1" || id == "STOP" || a.ShuntingDenied ||
                    a.StopLamp && !a.ShuntingPermission) return i;
            }
            for (int i = 0; i < aspects.Count; i++)
                if (aspects[i].DisallowPassing && !aspects[i].ShuntingPermission) return i;
            return advanceWarning && aspects.Count > 0 ? 0 : -1;
        }
        public static int SelectShunting(IReadOnlyList<SignalAspectInfo> aspects, int stop)
        {
            for (int i = 0; i < aspects.Count; i++) if (aspects[i].ShuntingPermission) return i;
            return stop;
        }
    }
}
