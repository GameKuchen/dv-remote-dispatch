using HarmonyLib;
using Signals.Game;
using Signals.Game.Aspects;
using Signals.Game.Controllers;
using Signals.Common.Aspects;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityModManagerNet;
using DvSignal = Signals.Game.Signal;

namespace DvMod.RemoteDispatch
{
    public static class SignalIntegration
    {
        private static bool patched;
        private static bool applying;
        private static Harmony? patcher;
        private static readonly HashSet<Type> shuntingTypes = new HashSet<Type>();
        private static readonly Dictionary<IAspect, DvSignal> shuntingOwners = new Dictionary<IAspect, DvSignal>();
        private static readonly Dictionary<AspectBaseDefinition, bool> originalRestrictions = new Dictionary<AspectBaseDefinition, bool>();
        private static readonly Dictionary<DvSignal, bool> originalShunting = new Dictionary<DvSignal, bool>();
        private static readonly Dictionary<DvSignal, SignalAspectInfo[]> aspectInfo = new Dictionary<DvSignal, SignalAspectInfo[]>();
        [ThreadStatic] public static DvSignal? Evaluating;
        [ThreadStatic] public static bool EvaluatingShunting;
        public static bool Available => UnityModManager.FindMod("DVSignals")?.Active == true && SignalManager.Running;
        public static IEnumerable<DvSignal> AllSignals => SignalManager.Instance.AllControllers
            .SelectMany(c => c.AllSignals).SelectMany(s => s.DistantSignal == null ? new[] { s } : new[] { s, s.DistantSignal });

        public static void Install(Harmony harmony)
        {
            if (patched) return;
            patcher = harmony;
            harmony.Patch(AccessTools.Method(typeof(DvSignal), nameof(DvSignal.UpdateAspect)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeUpdate)));
            harmony.Patch(AccessTools.Method(typeof(DvSignal), nameof(DvSignal.ChangeAspect)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeChange)));
            harmony.Patch(AccessTools.Method(typeof(DvSignal), nameof(DvSignal.SetShuntingStatus)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeShuntingStatus)));
            harmony.Patch(AccessTools.Method(typeof(ShuntingAllowedAspect), nameof(ShuntingAllowedAspect.MeetsConditions)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeShuntingCondition)));
            harmony.Patch(AccessTools.Method(typeof(TrainDetectedAspect), nameof(TrainDetectedAspect.MeetsConditions)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeTrainDetected)));
            harmony.Patch(AccessTools.Method(typeof(SpecialRequireReservationAspect), nameof(SpecialRequireReservationAspect.MeetsConditions)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeRequireReservation)));
            harmony.Patch(AccessTools.Method(typeof(TrackReservedAspect), nameof(TrackReservedAspect.MeetsConditions)),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeReservation)));
            harmony.Patch(AccessTools.Method(typeof(Signals.Game.Railway.TrackReserver), "ReserveForSignal", new[] { typeof(DvSignal) }),
                prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforeReserve)));
            patched = true;
        }
        public static void Reset() { patched = false; patcher = null; shuntingTypes.Clear(); }
        public static void ReleaseControl()
        {
            foreach (var pair in originalShunting)
                if (pair.Key.Definition != null) { pair.Key.SetShuntingStatus(pair.Value); pair.Key.UpdateIndicators(); }
            originalShunting.Clear();
            foreach (var pair in originalRestrictions)
                if (pair.Key != null) pair.Key.DisallowPassing = pair.Value;
            originalRestrictions.Clear();
            aspectInfo.Clear();
            shuntingOwners.Clear();
        }
        private static void BeforeShuntingStatus(DvSignal __instance, ref bool allowed)
        {
            if (RouteManager.Controlling && !applying) allowed = RouteManager.IsShuntingAllowed(__instance);
        }
        private static bool BeforeShuntingCondition(ShuntingAllowedAspect __instance, ref bool __result)
        {
            if (!RouteManager.Controlling) return true;
            bool allowed = RouteManager.IsShuntingAllowed(__instance.Signal);
            __result = __instance.Definition.Invert ? !allowed : allowed;
            return false;
        }
        private static bool BeforePermissionAspect(IAspect __instance, ref bool __result)
        {
            if (!RouteManager.Controlling || !shuntingOwners.TryGetValue(__instance, out var signal)) return true;
            // Top-level shunting lamps/indicators must not inherit path, reservation or occupancy conditions.
            __result = RouteManager.IsShuntingAllowed(signal);
            return false;
        }
        private static bool BeforeReserve(DvSignal signal, ref bool __result)
        {
            if (RouteManager.MayReserve(signal)) return true;
            __result = false;
            return false;
        }
        private static bool BeforeUpdate(DvSignal __instance, bool forced)
        {
            if (!RouteManager.Controlling) return true;
            Apply(__instance, RouteManager.GetAspect(__instance), forced);
            return false;
        }
        private static bool BeforeChange(DvSignal __instance, ref int newAspect)
        {
            if (RouteManager.Controlling && !applying) newAspect = RouteManager.GetAspect(__instance);
            return true;
        }
        private static bool BeforeTrainDetected(TrainDetectedAspect __instance, ref bool __result)
        {
            if (Evaluating != __instance.Signal || !EvaluatingShunting) return true;
            __result = false;
            return false;
        }
        private static bool BeforeRequireReservation(SpecialRequireReservationAspect __instance, ref bool __result)
        {
            if (Evaluating != __instance.Signal) return true;
            __result = false;
            return false;
        }
        private static bool BeforeReservation(TrackReservedAspect __instance, ref bool __result)
        {
            if (Evaluating != __instance.Signal) return true;
            var def = __instance.Definition;
            bool reserved = def.BySelf || Signals.Game.Railway.TrackReserver.IsSignalReservedByAnother(__instance.Signal);
            __result = def.Invert ? !reserved : reserved;
            return false;
        }
        public static int StopAspect(DvSignal signal)
        {
            return SignalAspectRules.SelectStop(GetAspectInfo(signal), signal.Parent != null ||
                signal.Controller.Type == SignalType.Distant || signal.Controller.Type == SignalType.Repeater);
        }
        private static SignalAspectInfo[] GetAspectInfo(DvSignal signal)
        {
            if (aspectInfo.TryGetValue(signal, out var info)) return info;
            info = signal.AllAspects.Select(a => {
                var d = a.GetDefinition();
                return new SignalAspectInfo {
                    Id = a.Id, DisallowPassing = a.DisallowPassing, ShuntingPermission = IsShuntingAspect(d),
                    ShuntingDenied = d is ShuntingAllowedAspectDefinition s && s.Invert,
                    StopLamp = d.OnLights.Any(l => (l.Colour.r > .65f && l.Colour.g < .4f && l.Colour.b < .4f) ||
                        (l.Colour.b > .65f && l.Colour.r < .4f && l.Colour.g < .5f))
                };
            }).ToArray();
            aspectInfo.Add(signal, info);
            if (RouteManager.Controlling && signal.Parent == null && signal.Controller.Type != SignalType.Distant &&
                signal.Controller.Type != SignalType.Repeater)
            {
                // Some packs invert Ms1/Ms2 metadata. Correct the native definition
                // rather than patching inherited generic getters (unsupported by Harmony).
                SignalNativeAspects.NormalizeRestrictions(signal.AllAspects, info, SignalAspectRules.SelectStop(info, false),
                    (definition, original) => {
                        if (!originalRestrictions.ContainsKey(definition)) originalRestrictions.Add(definition, original);
                    });
            }
            foreach (var aspect in signal.AllAspects.Concat(signal.AllIndicators).Where(a => IsShuntingAspect(a.GetDefinition())))
            {
                shuntingOwners[aspect] = signal;
                var type = aspect.GetType();
                if (type != typeof(ShuntingAllowedAspect) && patcher != null && !shuntingTypes.Contains(type))
                {
                    patcher.Patch(SignalNativeAspects.Condition(type),
                        prefix: new HarmonyMethod(typeof(SignalIntegration), nameof(BeforePermissionAspect)));
                    shuntingTypes.Add(type);
                }
            }
            return info;
        }
        private static bool ShuntingCondition(AspectBaseDefinition definition) =>
            definition is ShuntingAllowedAspectDefinition shunting && !shunting.Invert ||
            definition is CombinationAspectDefinition combination && combination.Conditions.Any(ShuntingCondition);
        private static bool IsShuntingAspect(AspectBaseDefinition definition) => ShuntingCondition(definition) ||
            definition.Id.Equals("Ms2", StringComparison.OrdinalIgnoreCase) ||
            definition.OnLights.Length > 0 && definition.OnLights.All(l => l.Colour.r > .65f && l.Colour.g > .65f && l.Colour.b > .65f);
        public static int ShuntingAspect(DvSignal signal)
        {
            // Combined main signals may show shunting through an additional white indicator, while the main aspect stays red.
            return SignalAspectRules.SelectShunting(GetAspectInfo(signal), StopAspect(signal));
        }
        public static bool IsShuntingHead(DvSignal signal) => signal.IsShunting || signal.Controller.Type == SignalType.Shunting ||
            GetAspectInfo(signal).Any(a => a.ShuntingPermission) &&
            !GetAspectInfo(signal).Where((a, i) => i != StopAspect(signal)).Any(a => !a.DisallowPassing && !a.ShuntingPermission);
        public static bool CanManualShunt(DvSignal signal) => signal.Parent == null && signal.Controller.PlacementInfo.HasValue &&
            signal.Controller.Type != SignalType.Distant && signal.Controller.Type != SignalType.Repeater &&
            (IsShuntingHead(signal) || signal.Controller is TrackSignalController && signal.Controller.Type != SignalType.Other);
        public static int Evaluate(DvSignal signal, bool shunting)
        {
            var previous = Evaluating;
            var previousShunting = EvaluatingShunting;
            Evaluating = signal;
            EvaluatingShunting = shunting;
            try
            {
                for (int i = 0; i < signal.AllAspects.Length; i++)
                    if (signal.AllAspects[i].MeetsConditions()) return i;
                return StopAspect(signal);
            }
            finally { Evaluating = previous; EvaluatingShunting = previousShunting; }
        }
        public static void Apply(DvSignal signal, int aspect, bool forced = false)
        {
            applying = true;
            try
            {
                if (!originalShunting.ContainsKey(signal)) originalShunting.Add(signal, signal.ShuntingAllowed);
                bool shuntingChanged = signal.SetShuntingStatus(RouteManager.IsShuntingAllowed(signal));
                if (!forced && !shuntingChanged && signal.CurrentAspectIndex == aspect) return;
                bool changed = signal.ChangeAspect(aspect);
                if (changed || forced || shuntingChanged)
                {
                    signal.UpdateDisplays(true);
                    signal.UpdateIndicators();
                    signal.UpdateHoverDisplay();
                }
            }
            finally { applying = false; }
        }
        public static bool CanSelect(DvSignal signal, bool shunting) => shunting ? CanManualShunt(signal) : signal.Parent == null &&
            signal.Controller is TrackSignalController && signal.Controller.PlacementInfo.HasValue &&
            !IsShuntingHead(signal) &&
            signal.Controller.Type != SignalType.Distant && signal.Controller.Type != SignalType.Repeater &&
            signal.Controller.Type != SignalType.Other && signal.AllAspects.Any(a => !a.DisallowPassing);
    }
}
