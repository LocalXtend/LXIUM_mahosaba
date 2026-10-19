using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;

namespace LXIUM_manosaba
{
    // Naninovel ResourceProvider.IsLocationCached<T>(path)를 경로 색인으로 바꿈.
    // 원래 코드는 지금까지 찾은 모든 리소스 위치(LocationsCache, 수만 개)를 매번 처음부터 훑음
    //   LocationsCache.Any(r => r.Path == path && AreTypesCompatible(r.Type, typeof(T)))
    // 대본을 불러올 때 명령마다 불려 재판 시작에 300~500 ms 멈춤이 생김 (12차 추적).
    // 여기서는 경로 → 형식 목록 색인을 두고(목록에 더해진 만큼만 이어서 색인), 형식 판정은 원래와 같은 AreTypesCompatible을 씀.
    // 제네릭 공유 구현(모든 참조형 T가 같이 씀)을 네이티브로 가로챔. 세 번째 인자가 IsLocationCached<T>의 메서드 정보
    static class ResourceIndex
    {
        // GameAssembly.dll 안의 위치와 그 앞부분 바이트. 게임이 업데이트돼 다르면 걸지 않음
        const int Rva = 0x754AF0;
        static readonly byte[] Prologue = Convert.FromHexString("48895c241048896c24184889742420574883ec2049837838");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte IsLocationCachedFn(IntPtr self, IntPtr path, IntPtr method);

        sealed class Index
        {
            public int Count;
            public readonly Dictionary<string, List<IntPtr>> Paths = new(StringComparer.Ordinal);
        }

        // 대리자가 GC로 사라지지 않게 붙잡아 둠
        static IsLocationCachedFn _original, _detour;
        static INativeDetour _hook;
        static readonly object Lock = new();
        static readonly Dictionary<IntPtr, Index> Indexes = new();
        static readonly Dictionary<IntPtr, IntPtr> TypeOfMethod = new();
        static readonly Dictionary<(IntPtr Provider, IntPtr Source, IntPtr Target), bool> Compatible = new();
        static int _calls, _mismatches, _verified;
        static long _ticks;
        static float _nextLog;

        public static void Install()
        {
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
            if (module == null) return;
            IntPtr target = module.BaseAddress + Rva;
            var head = new byte[Prologue.Length];
            Marshal.Copy(target, head, 0, head.Length);
            if (!head.AsSpan().SequenceEqual(Prologue))
            {
                Plugin.L.LogWarning("resource index: IsLocationCached 위치가 달라 걸지 않음 (게임 업데이트?)");
                return;
            }
            _detour = Detour;
            _hook = INativeDetour.CreateAndApply(target, _detour, out _original);
            Plugin.L.LogInfo("resource index: IsLocationCached 색인 켬");
        }

        static byte Detour(IntPtr self, IntPtr path, IntPtr method)
        {
            if (path == IntPtr.Zero) return _original(self, path, method);
            long t = Stopwatch.GetTimestamp(), a = System.GC.GetAllocatedBytesForCurrentThread();
            try
            {
                byte result;
                lock (Lock) result = Lookup(self, path, method);
                // 진단 모드에서는 원래 결과와 비교
                if (Settings.LayerDiag.Value && _verified++ < 200 && _original(self, path, method) != result)
                {
                    _mismatches++;
                    Plugin.L.LogWarning($"resource index: 결과 다름 {IL2CPP.Il2CppStringToManaged(path)} → 원래 결과 사용");
                    return (byte)(result ^ 1);
                }
                return result;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("resource index: " + e.Message);
                return _original(self, path, method);
            }
            finally
            {
                _calls++;
                _ticks += Stopwatch.GetTimestamp() - t;
                ModProf.Add(ModProf.Slot.Index, t, a);
            }
        }

        static byte Lookup(IntPtr self, IntPtr path, IntPtr method)
        {
            var provider = new Naninovel.ResourceProvider(self);
            var list = provider.LocationsCache;
            int count = list.Count;
            if (!Indexes.TryGetValue(self, out var index)) Indexes[self] = index = new Index();
            // 목록은 뒤에 더해지기만 함. 줄어들면 처음부터 다시
            if (count < index.Count)
            {
                index.Paths.Clear();
                index.Count = 0;
            }
            if (count > index.Count) Append(index, list, count);

            if (!index.Paths.TryGetValue(IL2CPP.Il2CppStringToManaged(path), out var candidates)) return 0;
            IntPtr target = TargetType(method);
            foreach (var source in candidates)
            {
                var key = (self, source, target);
                if (!Compatible.TryGetValue(key, out bool ok))
                    Compatible[key] = ok = provider.AreTypesCompatible(source == IntPtr.Zero ? null : new Il2CppSystem.Type(source), new Il2CppSystem.Type(target));
                if (ok) return 1;
            }
            return 0;
        }

        // 목록의 [index.Count, count) 항목을 색인에 더함. interop으로 항목마다 꺼내면 3~4 µs라(2만 개면 0.1초) IL2CPP 메모리를 직접 읽음:
        // List<T>는 +0x10 _items 배열, 배열 데이터는 +0x20부터, CachedResourceLocation은 (Path, Type) 16바이트, 문자열은 +0x10 길이·+0x14 글자.
        // 처음 한 번 interop 값과 맞춰 보고 다르면 interop으로만 읽음
        static bool? _rawOk;

        static void Append(Index index, Il2CppSystem.Collections.Generic.List<Naninovel.CachedResourceLocation> list, int count)
        {
            IntPtr items = Marshal.ReadIntPtr(list.Pointer + 0x10);
            if (_rawOk == null)
            {
                var first = list[index.Count];
                _rawOk = RawPath(items, index.Count) == first.Path && RawType(items, index.Count) == (first.Type?.Pointer ?? IntPtr.Zero);
                Plugin.L.LogInfo($"resource index: 직접 읽기 {(_rawOk == true ? "사용" : "불가, interop 사용")}");
            }
            for (int i = index.Count; i < count; i++)
            {
                string p;
                IntPtr type;
                if (_rawOk == true)
                {
                    p = RawPath(items, i);
                    type = RawType(items, i);
                }
                else
                {
                    var location = list[i];
                    p = location.Path;
                    type = location.Type?.Pointer ?? IntPtr.Zero;
                }
                if (p == null) continue;
                if (!index.Paths.TryGetValue(p, out var types)) index.Paths[p] = types = new List<IntPtr>(1);
                types.Add(type);
            }
            index.Count = count;
        }

        static string RawPath(IntPtr items, int i)
        {
            IntPtr str = Marshal.ReadIntPtr(items + 0x20 + i * 16);
            return str == IntPtr.Zero ? null : Marshal.PtrToStringUni(str + 0x14, Marshal.ReadInt32(str + 0x10));
        }

        static IntPtr RawType(IntPtr items, int i) => Marshal.ReadIntPtr(items + 0x20 + i * 16 + 8);

        // IsLocationCached<T>의 T (메서드 정보마다 한 번 구해 둠)
        static IntPtr TargetType(IntPtr method)
        {
            if (TypeOfMethod.TryGetValue(method, out var type)) return type;
            var info = new Il2CppSystem.Reflection.MethodInfo(IL2CPP.il2cpp_method_get_object(method, IntPtr.Zero));
            TypeOfMethod[method] = type = info.GetGenericArguments()[0].Pointer;
            return type;
        }

        // 진단 (Debug.LayerDiag): 5초마다 호출 수·걸린 시간·원래 결과와 다른 횟수
        internal static void DiagTick()
        {
            if (_hook == null || !Settings.LayerDiag.Value || UnityEngine.Time.unscaledTime < _nextLog) return;
            _nextLog = UnityEngine.Time.unscaledTime + 5f;
            if (_calls == 0) return;
            Plugin.L.LogInfo($"resource index 5s: calls={_calls} time={_ticks * 1000.0 / Stopwatch.Frequency:F1}ms (처음 200번은 원래 결과와 비교, 지금 {Math.Min(_verified, 200)}) mismatches={_mismatches} " +
                             $"paths={Indexes.Values.Sum(i => i.Paths.Count)}");
            _calls = _mismatches = 0;
            _ticks = 0;
        }
    }
}
