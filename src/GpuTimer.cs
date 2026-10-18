using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LXIUM_mahosaba
{
    // 카메라별 GPU 시간 (진단 설정 Debug.GpuTimer). D3D11 타임스탬프 쿼리를 카메라 렌더 앞뒤에 넣는다.
    // 쿼리는 Unity 렌더 스레드에서 직접 D3D11 컨텍스트를 써야 하므로 CommandBuffer.IssuePluginEvent로 렌더 순서 안에서 부른다.
    // 결과는 4프레임 뒤에 읽음 (GPU를 기다리지 않음). D3D11이 아니면 꺼짐.
    // 개발 빌드가 아니고 RenderDoc을 붙이면 BepInEx가 로드되지 않아(winhttp 프록시) 모드가 켜진 프레임을 잴 방법이 이것뿐
    static unsafe class GpuTimer
    {
        const int Slots = 4;          // 결과를 기다리는 프레임 수
        const int MaxMarks = 64;      // 프레임당 타임스탬프 수
        const int D3D11_QUERY_TIMESTAMP = 2, D3D11_QUERY_TIMESTAMP_DISJOINT = 3;

        enum Kind { Frame, Mark, FrameEnd }

        struct Command
        {
            public Kind Kind;
            public int Slot, Mark;
            // FrameEnd: 이 프레임의 표시 이름 사본과 개수
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
        static bool _ready, _failed;
        static readonly IntPtr[] Disjoint = new IntPtr[Slots];
        static readonly IntPtr[,] Stamps = new IntPtr[Slots, MaxMarks];
        // 메인 스레드가 프레임마다 적어 두는 표시 이름 (렌더 스레드가 결과를 읽은 뒤 메인이 합침)
        static readonly string[][] Names = new string[Slots][];
        static readonly int[] MarkCount = new int[Slots];
        // 렌더 스레드: 슬롯마다 마지막으로 끝낸 프레임의 이름·개수 (다음에 그 슬롯을 쓸 때 결과를 읽음)
        static readonly string[][] IssuedNames = new string[Slots][];
        static readonly int[] IssuedCount = new int[Slots];
        // 렌더 스레드가 읽은 결과 (슬롯별 ms). 메인 스레드가 가져감
        static readonly double[][] Results = new double[Slots][];
        static readonly string[][] ResultNames = new string[Slots][];
        static readonly int[] ResultCount = new int[Slots];
        static readonly bool[] ResultReady = new bool[Slots];
        static readonly object Lock = new();

        static int _slot = -1, _frame = -1;
        static readonly Dictionary<string, (double Ms, int Count)> Sums = new();
        static double _frameSum;
        static int _frames;
        static float _nextLog;
        internal static string Summary = "";

        public static void Install()
        {
            // 진단용이고 아직 검증 중: 설정을 켠 경우에만 D3D11 객체를 건드림
            if (!Settings.GpuTimer.Value || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) return;
            for (int i = 0; i < Slots; i++)
            {
                Names[i] = new string[MaxMarks];
                Results[i] = new double[MaxMarks];
            }
            _callback = OnRenderEvent;
            _callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback);
            _cb = new CommandBuffer();
            // 아무 텍스처의 네이티브 포인터 → ID3D11Device (실제 생성은 렌더 스레드 첫 이벤트에서)
            var tex = new Texture2D(1, 1);
            IntPtr resource = tex.GetNativeTexturePtr();
            if (resource == IntPtr.Zero) return;
            IntPtr device;
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)VTable(resource, 3))(resource, &device);   // ID3D11DeviceChild::GetDevice
            _device = device;
            RenderPipelineManager.add_beginContextRendering((Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)OnBeginContext);
            RenderPipelineManager.add_beginCameraRendering((Action<ScriptableRenderContext, Camera>)OnBeginCamera);
            RenderPipelineManager.add_endCameraRendering((Action<ScriptableRenderContext, Camera>)OnEndCamera);
            Plugin.L.LogInfo("gpu timer: 켬");
        }

        static IntPtr VTable(IntPtr obj, int index) => ((IntPtr*)*(IntPtr*)obj)[index];

        // 메인 스레드: 렌더 스레드에 보낼 명령 하나를 적고 CommandBuffer로 렌더 순서에 끼움
        static void Issue(ScriptableRenderContext ctx, Kind kind, int mark, string[] names = null)
        {
            int id = _commandHead++ & (Commands.Length - 1);
            Commands[id] = new Command { Kind = kind, Slot = _slot, Mark = mark, Names = names };
            _cb.Clear();
            _cb.IssuePluginEvent(_callbackPtr, id);
            // 콜백으로 받은 ScriptableRenderContext를 interop으로 넘기면 접근 위반(19:04 충돌) → 즉시 실행 큐에 넣음
            Graphics.ExecuteCommandBuffer(_cb);
        }

        static void OnBeginContext(ScriptableRenderContext ctx, Il2CppSystem.Collections.Generic.List<Camera> cams)
        {
            if (!Settings.GpuTimer.Value || _failed) return;
            // 한 프레임에 렌더 루프가 여러 번(캐릭터 합성 카메라 등) 돌 수 있어 프레임 번호로 묶음
            if (_frame != Time.frameCount)
            {
                // 지난 프레임을 닫음 (한 프레임에 렌더 루프가 여러 번 돌아서 마지막 끝을 알 수 없으므로 다음 프레임 시작에서 닫음)
                if (_slot >= 0) EndFrame(ctx);
                Collect();
                _frame = Time.frameCount;
                _slot = (_slot + 1) % Slots;
                MarkCount[_slot] = 0;
                Issue(ctx, Kind.Frame, 0);
            }
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!Settings.GpuTimer.Value || _failed || _slot < 0 || _frame != Time.frameCount) return;
            int m = MarkCount[_slot];
            if (m + 2 >= MaxMarks) return;
            var rt = cam.targetTexture;
            Names[_slot][m] = rt == null ? cam.name : $"{cam.name} {rt.width}x{rt.height}";
            MarkCount[_slot] = m + 2;
            Issue(ctx, Kind.Mark, m);
        }

        static void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!Settings.GpuTimer.Value || _failed || _slot < 0 || _frame != Time.frameCount) return;
            int m = MarkCount[_slot];
            if (m < 2) return;
            Issue(ctx, Kind.Mark, m - 1);
        }

        static void EndFrame(ScriptableRenderContext ctx)
        {
            int n = MarkCount[_slot];
            var names = new string[n];
            Array.Copy(Names[_slot], names, n);
            Issue(ctx, Kind.FrameEnd, n, names);
        }

        // 렌더 스레드
        static void OnRenderEvent(int id)
        {
            try
            {
                var c = Commands[id & (Commands.Length - 1)];
                if (!_ready && !Create()) return;
                switch (c.Kind)
                {
                    case Kind.Frame:
                        Read(c.Slot);
                        ((delegate* unmanaged<IntPtr, IntPtr, void>)VTable(_context, 27))(_context, Disjoint[c.Slot]);   // Begin
                        break;
                    case Kind.Mark:
                        ((delegate* unmanaged<IntPtr, IntPtr, void>)VTable(_context, 28))(_context, Stamps[c.Slot, c.Mark]);   // End
                        break;
                    case Kind.FrameEnd:
                        ((delegate* unmanaged<IntPtr, IntPtr, void>)VTable(_context, 28))(_context, Disjoint[c.Slot]);   // End
                        IssuedNames[c.Slot] = c.Names;
                        IssuedCount[c.Slot] = c.Mark;
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
            int n = IssuedCount[slot];
            IssuedCount[slot] = 0;
            if (n == 0) return;
            var getData = (delegate* unmanaged<IntPtr, IntPtr, void*, int, int, int>)VTable(_context, 29);   // GetData
            ulong* dj = stackalloc ulong[2];
            if (getData(_context, Disjoint[slot], dj, 16, 1) != 0 || ((int*)dj)[2] != 0) return;
            double freq = dj[0];
            ulong t0 = 0;
            for (int m = 0; m < n; m += 2)
            {
                ulong a, b;
                if (getData(_context, Stamps[slot, m], &a, 8, 1) != 0 || getData(_context, Stamps[slot, m + 1], &b, 8, 1) != 0) return;
                if (m == 0) t0 = a;
                Results[slot][m] = (b - a) * 1000.0 / freq;
            }
            lock (Lock)
            {
                ResultNames[slot] = IssuedNames[slot];
                ResultCount[slot] = n;
                ResultReady[slot] = true;
            }
        }

        // 메인 스레드: 읽힌 슬롯 결과를 합침
        static void Collect()
        {
            for (int s = 0; s < Slots; s++)
            {
                lock (Lock)
                {
                    if (!ResultReady[s]) continue;
                    ResultReady[s] = false;
                    double frame = 0;
                    for (int m = 0; m < ResultCount[s]; m += 2)
                    {
                        string name = ResultNames[s][m] ?? "?";
                        var (ms, count) = Sums.GetValueOrDefault(name);
                        Sums[name] = (ms + Results[s][m], count + 1);
                        frame += Results[s][m];
                    }
                    _frameSum += frame;
                    _frames++;
                }
            }
            if (Time.unscaledTime < _nextLog || _frames == 0) return;
            _nextLog = Time.unscaledTime + 5f;
            var parts = new List<string>();
            foreach (var (name, (ms, count)) in Sums) parts.Add($"{name}={ms / _frames:F3}ms/{(float)count / _frames:F2}");
            parts.Sort();
            Summary = $"GPU {_frameSum / _frames:F3} ms/frame";
            Plugin.L.LogInfo($"gpu timer {_frames} frames: total={_frameSum / _frames:F3}ms  " + string.Join("  ", parts));
            Sums.Clear();
            _frameSum = 0;
            _frames = 0;
        }
    }
}
