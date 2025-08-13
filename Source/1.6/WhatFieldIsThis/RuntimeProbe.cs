// RuntimeProbe.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RuntimeProbe
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony("runtimeprobe.rimworld.introspection");
                // Patch the concrete FieldInfo.GetValue once we resolve it
                var target = Prober.ResolveFieldGetValueTarget();
                if (target != null)
                {
                    harmony.Patch(original: target,
                                  prefix: new HarmonyMethod(typeof(FieldInfoGetValuePatch), nameof(FieldInfoGetValuePatch.Prefix)));
                }

                Prober.LogRuntimeFacts(target);

                // Trigger GetValue once (so our patch logs the actual runtime type used)
                FieldInfoGetValuePatch.TryTriggerOnce();
            }
            catch (Exception e)
            {
                Log.Error($"[RuntimeProbe] Initialization failed: {e}");
            }
        }
    }

    public static class Prober
    {
        // Try common names first; fall back to scanning the FieldInfo assembly
        private static readonly string[] FieldInfoImplCandidates =
        {
            "System.Reflection.MonoField",        // older Unity/Mono
            "System.Reflection.RuntimeFieldInfo", // .NET / newer Mono
            "System.Reflection.RtFieldInfo"       // some Mono builds
        };

        public static MethodInfo? ResolveFieldGetValueTarget()
        {
            var asm = typeof(FieldInfo).Assembly;

            // Direct candidates
            foreach (var name in FieldInfoImplCandidates)
            {
                var t = asm.GetType(name, throwOnError: false);
                var m = t?.GetMethod("GetValue",
                          BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                          binder: null, types: new[] { typeof(object) }, modifiers: null);
                if (m != null && !m.IsAbstract) return m;
            }

            // Fallback: scan all non-abstract FieldInfo subclasses for a concrete GetValue(object)
            foreach (var t in asm.GetTypes())
            {
                if (t.IsAbstract) continue;
                if (!typeof(FieldInfo).IsAssignableFrom(t)) continue;

                var m = t.GetMethod("GetValue",
                          BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                          null, new[] { typeof(object) }, null);
                if (m != null && m.DeclaringType == t && !m.IsAbstract)
                    return m;
            }
            return null;
        }

        public static void LogRuntimeFacts(MethodInfo? fieldGetValueImpl)
        {
            // 1) Concrete FieldInfo impl (and assembly) for GetValue(object)
            var implType = fieldGetValueImpl?.DeclaringType;
            Log.Message($"[RuntimeProbe] FieldInfo.GetValue impl: " +
                        (implType != null
                          ? $"{implType.FullName} (asm: {implType.Assembly.GetName().Name} v{implType.Assembly.GetName().Version})"
                          : "NOT FOUND"));

            // 2) Dictionary internals present?
            var dictTT = typeof(Dictionary<,>).MakeGenericType(typeof(int), typeof(int));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var buckets = dictTT.GetField("_buckets", flags) ?? dictTT.GetField("buckets", flags);
            var entries = dictTT.GetField("_entries", flags) ?? dictTT.GetField("entries", flags);
            var entryType = dictTT.GetNestedType("Entry", BindingFlags.NonPublic);

            string BucketsName(FieldInfo? f) => f?.Name ?? "NONE";
            string EntriesName(FieldInfo? f) => f?.Name ?? "NONE";

            Log.Message($"[RuntimeProbe] Dictionary<,> internals: buckets='{BucketsName(buckets)}', entries='{EntriesName(entries)}', EntryType={(entryType != null ? "present" : "missing")}");

            if (entryType != null)
            {
                var fHash = entryType.GetField("hashCode", flags) != null;
                var fNext = entryType.GetField("next", flags) != null;
                var fKey = entryType.GetField("key", flags) != null;
                var fVal = entryType.GetField("value", flags) != null;
                Log.Message($"[RuntimeProbe] Dictionary<,>.Entry fields: hashCode={fHash}, next={fNext}, key={fKey}, value={fVal}");
            }

            // 3) string first-char field (if any)
            var s_first = typeof(string).GetField("_firstChar", flags) ??
                          typeof(string).GetField("m_firstChar", flags);
            Log.Message($"[RuntimeProbe] string first-char field: {(s_first != null ? s_first.Name : "NONE")}");

            // 4) Which MemoryMarshal are we seeing (helps with Harmony collisions)?
            var mm = Type.GetType("System.Runtime.InteropServices.MemoryMarshal, System.Runtime.Extensions", throwOnError: false)
                     ?? Type.GetType("System.Runtime.InteropServices.MemoryMarshal"); // pick whatever resolves
            if (mm != null)
            {
                var a = mm.Assembly.GetName();
                Log.Message($"[RuntimeProbe] MemoryMarshal provider: {a.Name} v{a.Version}");
            }
            else
            {
                Log.Message("[RuntimeProbe] MemoryMarshal provider: NOT RESOLVED");
            }
        }
    }

    // Harmony patch to confirm which concrete GetValue(object) is actually invoked.
    [HarmonyPatch]
    public static class FieldInfoGetValuePatch
    {
        private static volatile bool _logged;
        private static string? _lastImplType;

        // Harmony asks us which method to patch; we delegate to our resolver via Bootstrap
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var m = Prober.ResolveFieldGetValueTarget();
            if (m != null) yield return m;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Prefix(FieldInfo __instance)
        {
            if (_logged) return;
            _logged = true;
            _lastImplType = __instance.GetType().FullName;
            Log.Message($"[RuntimeProbe] Harmony confirmed GetValue() impl instance type: {_lastImplType}");
        }

        // Force a single call to GetValue so the prefix logs once
        public static void TryTriggerOnce()
        {
            if (_logged) return;

            try
            {
                var dummy = new Dummy { X = 7 };
                var fi = typeof(Dummy).GetField(nameof(Dummy.X), BindingFlags.Public | BindingFlags.Instance);
                _ = fi?.GetValue(dummy);
            }
            catch (Exception e)
            {
                Log.Warning($"[RuntimeProbe] Trigger GetValue() failed: {e.Message}");
            }
        }

        private sealed class Dummy { public int X; }
    }
}