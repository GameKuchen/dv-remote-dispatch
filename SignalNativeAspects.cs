using HarmonyLib;
using Signals.Common.Aspects;
using Signals.Game.Aspects;
using System;
using System.Reflection;

namespace DvMod.RemoteDispatch
{
    internal static class SignalNativeAspects
    {
        public static void NormalizeRestrictions(IAspect[] aspects, SignalAspectInfo[] info, int stop,
            Action<AspectBaseDefinition, bool> rememberOriginal)
        {
            for (int i = 0; i < aspects.Length; i++)
            {
                bool? restrictive = i == stop ? true : info[i].ShuntingPermission ? false : (bool?)null;
                if (!restrictive.HasValue) continue;
                var definition = aspects[i].GetDefinition();
                if (definition.DisallowPassing != restrictive.Value)
                {
                    rememberOriginal(definition, definition.DisallowPassing);
                    definition.DisallowPassing = restrictive.Value;
                }
                info[i].DisallowPassing = restrictive.Value;
            }
        }

        public static MethodInfo Condition(Type aspectType) =>
            AccessTools.GetDeclaredMember(AccessTools.Method(aspectType, "MeetsConditions"));
    }
}
