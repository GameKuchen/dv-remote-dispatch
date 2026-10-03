using DvMod.RemoteDispatch;
using HarmonyLib;
using Signals.Common.Aspects;
using Signals.Game.Aspects;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

internal static class NativePatchTests
{
    private static bool? forced;
    private static int assertions;

    private static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Pass the game directory and DVSignals directory.");
        var directories = new[] { Path.Combine(args[0], "DerailValley_Data", "Managed"),
            Path.Combine(args[0], "DerailValley_Data", "Managed", "UnityModManager"), args[1] };
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
            string file = new AssemblyName(request.Name).Name + ".dll";
            string? path = directories.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        Run();
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    private static bool Before(IAspect __instance, ref bool __result)
    {
        if (!forced.HasValue) return true;
        __result = forced.Value;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var harmony = new Harmony("RemoteDispatch.NativePatchRegression");
        var prefix = new HarmonyMethod(typeof(NativePatchTests), nameof(Before));
        var inherited = AccessTools.PropertyGetter(typeof(CombinationAspect), nameof(IAspect.DisallowPassing));
        Check(inherited.ReflectedType != inherited.DeclaringType, "Regression must exercise an inherited getter.");
        try
        {
            harmony.Patch(inherited, prefix: prefix);
            throw new Exception("Expected the old 1.4.0 target to be rejected.");
        }
        catch (ArgumentException error)
        {
            Check(error.Message.Contains("declared method"), "Reproduce the user's Harmony failure.");
        }

        int count = 0;
        try
        {
            foreach (var type in typeof(IAspect).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IAspect).IsAssignableFrom(t)))
            {
                var getter = AccessTools.PropertyGetter(type, nameof(IAspect.DisallowPassing));

                // Avoid Unity constructors/rendering. These are the real native aspect
                // and definition classes; only the getter's managed backing field is needed.
                var aspect = (IAspect)FormatterServices.GetUninitializedObject(type);
                var property = AccessTools.Property(type, "Definition");
                var definition = (AspectBaseDefinition)FormatterServices.GetUninitializedObject(property.PropertyType);
                AccessTools.Field(property.DeclaringType, "<Definition>k__BackingField").SetValue(aspect, definition);

                definition.DisallowPassing = false;
                var info = new[] { new SignalAspectInfo { Id = "Ms1", DisallowPassing = false } };
                bool? original = null;
                SignalNativeAspects.NormalizeRestrictions(new[] { aspect }, info, 0, (_, old) => original = old);
                Check((bool)getter.Invoke(aspect, null), "Native getter must report stop after correction: " + type.Name);
                Check(original == false && info[0].DisallowPassing, "Preserve original stop metadata for restoration.");
                definition.DisallowPassing = true;
                info[0] = new SignalAspectInfo { Id = "Ms2", DisallowPassing = true, ShuntingPermission = true };
                original = null;
                SignalNativeAspects.NormalizeRestrictions(new[] { aspect }, info, -1, (_, old) => original = old);
                Check(!(bool)getter.Invoke(aspect, null), "Native getter must allow white shunting after correction: " + type.Name);
                Check(original == true && !info[0].DisallowPassing, "Preserve original shunting metadata for restoration.");
                info[0] = new SignalAspectInfo { Id = "S2", DisallowPassing = false };
                original = null;
                SignalNativeAspects.NormalizeRestrictions(new[] { aspect }, info, -1, (_, old) => original = old);
                Check(!original.HasValue && !(bool)getter.Invoke(aspect, null), "Other native aspects must remain unchanged.");
                definition.DisallowPassing = true;
                info[0].DisallowPassing = true;
                SignalNativeAspects.NormalizeRestrictions(new[] { aspect }, info, 0, (_, old) => original = old);
                Check(!original.HasValue, "Correct stop metadata must not need mutation.");

                var condition = SignalNativeAspects.Condition(type);
                Check(condition.ReflectedType == condition.DeclaringType, "Condition target must be declared.");
                harmony.Patch(condition, prefix: prefix);
                forced = true;
                Check((bool)condition.Invoke(aspect, null), "White indication prefix must allow " + type.Name);
                forced = false;
                Check(!(bool)condition.Invoke(aspect, null), "White indication prefix must deny " + type.Name);
                count++;
            }
            Console.WriteLine($"PASS: {assertions} native Harmony assertions across {count} DV Signals aspect types (Harmony {typeof(Harmony).Assembly.GetName().Version}).");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
