using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace LXIUM_mahosaba
{
    // 카메라별 GPU 시간 (진단 설정 Debug.GpuTimer). D3D11 타임스탬프 쿼리를 프레임 첫 렌더 앞, 카메라 렌더 앞뒤,
    // 프레임 끝(WaitForEndOfFrame: Overlay UI·IMGUI까지 그린 뒤)에 넣는다. 프레임 합계 = 첫 렌더 ~ 프레임 끝.
    // 쿼리는 Unity 렌더 스레드에서 D3D11 컨텍스트를 써야 하므로 CommandBuffer.IssuePluginEvent로 렌더 순서 안에서 부른다.
    // 결과는 4프레임 뒤에 읽음 (GPU를 기다리지 않음). D3D11이 아니면 꺼짐.
    // RenderDoc을 붙이면 BepInEx가 로드되지 않아 모드가 켜진 프레임은 이것으로만 잰다 (mahosaba-render.md 7장)
    static unsafe class GpuTimer
    {
        const int Slots = 4;          // 결과를 기다리는 프레임 수
        const int MaxMarks = 64;      // 프레임당 타임스탬프 수. 0 = 프레임 시작, 1 = 프레임 끝, 2부터 카메라 앞뒤 쌍
        const int D3D11_QUERY_TIMESTAMP = 2, D3D11_QUERY_TIMESTAMP_DISJOINT = 3;

        enum Kind { Frame, Mark, Tail }

        struct Command
        {
            public Kind Kind;
            public int Slot, Mark;
            // Tail: 이 프레임의 표시 이름 사본
            public string[] Names;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void RenderEvent(int id);

        static RenderEvent _callback;
        static IntPtr _callbackPtr;
        static readonly Command[] Commands = new Command[4096];
        static int _commandHead;
        static CommandBuffer _cb;
        static IntPtr _device, _context;
        static bool _ready, _failed, _installed;
        static readonly IntPtr[] Disjoint = new IntPtr[Slots];
        static readonly IntPtr[,] Stamps = new IntPtr[Slots, MaxMarks];
        // 메인 스레드가 프레임마다 적는 표시 이름
        static readonly string[][] Names = new string[Slots][];
        static readonly int[] MarkCount = new int[Slots];
        // 렌더 스레드: 슬롯마다 끝낸 프레임의 이름 (다음에 그 슬롯을 쓸 때 결과를 읽음)
        static readonly string[][] IssuedNames = new string[Slots][];
        // 렌더 스레드가 읽은 결과. 메인 스레드가 가져감
        static readonly double[][] Results = new double[Slots][];
        static readonly string[][] ResultNames = new string[Slots][];
        static readonly bool[] ResultReady = new bool[Slots];
        static readonly object Lock = new();

        static int _slot = -1, _frame = -1;
        static bool _tailIssued = true;

        // 누적 값 (실험실은 앞뒤 차이로 구간 값을 냄)
        internal static double TotalMs;
        internal static int Frames;
        internal static readonly Dictionary<string, (double Ms, int Count)> Cameras = new();
        internal static bool Active => _installed && !_failed;

        static double _logTotal;
        static int _logFrames;
        static readonly Dictionary<string, (double Ms, int Count)> LogCameras = new();
        static float _nextLog;
        internal static string Summary = "";

        public static void Install(MonoBehaviour host)
        {
            // 진단용: 설정을 켠 경우에만 D3D11 객체를 건드림
            if (!Settings.GpuTimer.Value || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) return;
            for (int i = 0; i < Slots; i++)
            {
                Names[i] = new string[MaxMarks];
                Results[i] = new double[MaxMarks];
            }
            _callback = OnRenderEvent;
            _callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback);
            _cb = new CommandBuffer();
            // 아무 텍스처의 네이티브 포인터 → ID3D11Device (쿼리 생성은 렌더 스레드 첫 이벤트에서)
            var tex = new Texture2D(1, 1);
            IntPtr resource = tex.GetNativeTexturePtr();
            if (resource == IntPtr.Zero) return;
            IntPtr device;
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)VTable(resource, 3))(resource, &device);   // ID3D11DeviceChild::GetDevice
            _device = device;
            RenderPipelineManager.add_beginContextRendering((Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)OnBeginContext);
            RenderPipelineManager.add_beginCameraRendering((Action<ScriptableRenderContext, Camera>)OnBeginCamera);
            RenderPipelineManager.add_endCameraRendering((Action<ScriptableRenderContext, Camera>)OnEndCamera);
            host.StartCoroutine(EndOfFrame().WrapToIl2Cpp());
            _installed = true;
            Plugin.L.LogInfo("gpu timer: 켬");
        }

        static IntPtr VTable(IntPtr obj, int index) => ((IntPtr*)*(IntPtr*)obj)[index];

        // 메인 스레드: 렌더 스레드에 보낼 명령 하나를 적고 즉시 실행 큐에 넣음.
        // URP 콜백이 넘긴 ScriptableRenderContext를 interop으로 쓰면 접근 위반이 나서(0.6.1 개발 중 충돌) 쓰지 않음
        static void Issue(Kind kind, int mark, string[] names = null)
        {
            int id = _commandHead++ & (Commands.Length - 1);
            Commands[id] = new Command { Kind = kind, Slot = _slot, Mark = mark, Names = names };
            _cb.Clear();
            _cb.IssuePluginEvent(_callbackPtr, id);
            Graphics.ExecuteCommandBuffer(_cb);
        }

        static void OnBeginContext(ScriptableRenderContext ctx, Il2CppSystem.Collections.Generic.List<Camera> cams)
        {
            if (_failed || _frame == Time.frameCount) return;
            // 프레임 끝이 오지 않은 프레임(렌더를 건너뛴 프레임 등)은 여기서 닫음
            if (!_tailIssued) IssueTail();
            Collect();
            _frame = Time.frameCount;
            _slot = (_slot + 1) % Slots;
            MarkCount[_slot] = 2;
            _tailIssued = false;
            Issue(Kind.Frame, 0);
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            // 실험실의 프레임당 카메라 렌더 수 (GPU 타이머가 켜져 있을 때만 셈)
            RenderLab.CountCamera();
            if (_failed || _slot < 0 || _frame != Time.frameCount || _tailIssued) return;
            int m = MarkCount[_slot];
            if (m + 2 > MaxMarks) return;
            var rt = cam.targetTexture;
            Names[_slot][m] = rt == null ? cam.name : $"{cam.name} {rt.width}x{rt.height}";
            MarkCount[_slot] = m + 2;
            Issue(Kind.Mark, m);
        }

        static void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_failed || _slot < 0 || _frame != Time.frameCount || _tailIssued) return;
            int m = MarkCount[_slot];
            if (m < 4) return;
            Issue(Kind.Mark, m - 1);
        }

        static IEnumerator EndOfFrame()
        {
            var wait = new WaitForEndOfFrame();
            while (!_failed)
            {
                yield return wait;
                if (!_tailIssued && _frame == Time.frameCount) IssueTail();
            }
        }

        static void IssueTail()
        {
            int n = MarkCount[_slot];
            var names = new string[n];
            Array.Copy(Names[_slot], names, n);
            Issue(Kind.Tail, n, names);
            _tailIssued = true;
        }

        // 렌더 스레드
        static void OnRenderEvent(int id)
        {
            try
            {
                var c = Commands[id & (Commands.Length - 1)];
                if (!_ready && !Create()) return;
                var end = (delegate* unmanaged<IntPtr, IntPtr, void>)VTable(_context, 28);   // End
                switch (c.Kind)
                {
                    case Kind.Frame:
                        Read(c.Slot);
                        ((delegate* unmanaged<IntPtr, IntPtr, void>)VTable(_context, 27))(_context, Disjoint[c.Slot]);   // Begin
                        end(_context, Stamps[c.Slot, 0]);
                        break;
                    case Kind.Mark:
                        end(_context, Stamps[c.Slot, c.Mark]);
                        break;
                    case Kind.Tail:
                        end(_context, Stamps[c.Slot, 1]);
                        end(_context, Disjoint[c.Slot]);
                        IssuedNames[c.Slot] = c.Names;
                        break;
                }
            }
            catch (Exception)
            {
                _failed = true;
            }
        }

        static bool Create()
        {
            IntPtr ctx;
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)VTable(_device, 40))(_device, &ctx);   // GetImmediateContext
            _context = ctx;
            var create = (delegate* unmanaged<IntPtr, int*, IntPtr*, int>)VTable(_device, 24);   // CreateQuery
            int* desc = stackalloc int[2];
            for (int s = 0; s < Slots; s++)
            {
                desc[0] = D3D11_QUERY_TIMESTAMP_DISJOINT; desc[1] = 0;
                IntPtr q;
                if (create(_device, desc, &q) != 0) { _failed = true; return false; }
                Disjoint[s] = q;
                desc[0] = D3D11_QUERY_TIMESTAMP;
                for (int m = 0; m < MaxMarks; m++)
                {
                    if (create(_device, desc, &q) != 0) { _failed = true; return false; }
                    Stamps[s, m] = q;
                }
            }
            _ready = true;
            return true;
        }

        // 렌더 스레드: 그 슬롯의 지난 결과를 읽음 (준비 안 됐으면 버림)
        static void Read(int slot)
        {
            var names = IssuedNames[slot];
            IssuedNames[slot] = null;
            if (names == null) return;
            int n = names.Length;
            var getData = (delegate* unmanaged<IntPtr, IntPtr, void*, int, int, int>)VTable(_context, 29);   // GetData
            ulong* dj = stackalloc ulong[2];
            if (getData(_context, Disjoint[slot], dj, 16, 1) != 0 || ((int*)dj)[2] != 0) return;
            double freq = dj[0];
            ulong a, b;
            if (getData(_context, Stamps[slot, 0], &a, 8, 1) != 0 || getData(_context, Stamps[slot, 1], &b, 8, 1) != 0) return;
            Results[slot][0] = (b - a) * 1000.0 / freq;
            for (int m = 2; m + 1 < n; m += 2)
            {
                if (getData(_context, Stamps[slot, m], &a, 8, 1) != 0 || getData(_context, Stamps[slot, m + 1], &b, 8, 1) != 0) return;
                Results[slot][m] = (b - a) * 1000.0 / freq;
            }
            lock (Lock)
            {
                ResultNames[slot] = names;
                ResultReady[slot] = true;
            }
        }

        // 메인 스레드: 읽힌 슬롯 결과를 누적
        static void Collect()
        {
            for (int s = 0; s < Slots; s++)
            {
                lock (Lock)
                {
                    if (!ResultReady[s]) continue;
                    ResultReady[s] = false;
                    var names = ResultNames[s];
                    TotalMs += Results[s][0];
                    Frames++;
                    _logTotal += Results[s][0];
                    _logFrames++;
                    for (int m = 2; m + 1 < names.Length; m += 2)
                    {
                        string name = names[m] ?? "?";
                        var (ms, count) = Cameras.GetValueOrDefault(name);
                        Cameras[name] = (ms + Results[s][m], count + 1);
                        (ms, count) = LogCameras.GetValueOrDefault(name);
                        LogCameras[name] = (ms + Results[s][m], count + 1);
                    }
                }
            }
            if (Time.unscaledTime < _nextLog || _logFrames == 0) return;
            _nextLog = Time.unscaledTime + 5f;
            var parts = new List<string>();
            double cams = 0;
            foreach (var (name, (ms, count)) in LogCameras)
            {
                parts.Add($"{name}={ms / _logFrames:F3}ms/{(float)count / _logFrames:F2}");
                cams += ms;
            }
            parts.Sort();
            Summary = $"GPU  {_logTotal / _logFrames:F2} ms (카메라 밖 {(_logTotal - cams) / _logFrames:F2})";
            Plugin.L.LogInfo($"gpu timer {_logFrames} frames: total={_logTotal / _logFrames:F3}ms outsideCameras={(_logTotal - cams) / _logFrames:F3}ms  " + string.Join("  ", parts));
            LogCameras.Clear();
            _logTotal = 0;
            _logFrames = 0;
        }
    }
}
