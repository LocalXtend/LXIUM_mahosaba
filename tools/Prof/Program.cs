// LXIUM 프로파일러: 게임 실행 중 WPR로 잡은 ETW 추적(.etl)에서 manosaba.exe의 CPU 샘플을 함수 이름으로 묶어 본다.
//   map <게임 폴더> <map.tsv>           GameAssembly.dll(IL2CPP)의 메서드 주소표를 Cpp2IL로 만듦 (PDB가 없으므로)
//   cpu <trace.etl> <map.tsv> [시작초 끝초] [-thread 이름]
//                                        구간의 CPU 샘플을 스레드·모듈·함수별로 집계. UnityPlayer.dll은 Unity 심볼 서버의 PDB로,
//                                        GameAssembly.dll은 map.tsv로 이름을 붙임
//   timeline <trace.etl> [칸ms]         메인 스레드 CPU 사용을 칸마다 출력 (긴 프레임·로딩 구간 찾기)
//   frames <trace.etl> <map.tsv> [기준ms] [시작초 끝초]
//                                        DXGI Present 간격으로 프레임 시간 통계를 내고, 기준보다 긴 프레임마다
//                                        메인 스레드가 무엇을 했는지 보여 줌 (lite.wprp로 잡은 추적)
//   tree <trace.etl> <map.tsv> <시작초> <끝초> [최소%]
//                                        구간의 메인 스레드 호출 트리 (위에서 아래로, 최소% 미만 가지는 생략)
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Symbols;

const string Process = "manosaba.exe";
const ulong ImageBase = 0x180000000;

switch (args.Length > 0 ? args[0] : "")
{
    case "map": Map(args[1], args[2]); break;
    case "cpu": await Cpu(args); break;
    case "timeline": Timeline(args[1], args.Length > 2 ? double.Parse(args[2]) : 100); break;
    case "frames": await Frames(args); break;
    case "tree": await Tree(args); break;
    default: Console.WriteLine("map | cpu | timeline (Program.cs 맨 위 참고)"); break;
}

static void Map(string game, string output)
{
    Cpp2IL.Core.Cpp2IlApi.Init();
    Cpp2IL.Core.Cpp2IlApi.ConfigureLib(false);
    Cpp2IL.Core.Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(game, "GameAssembly.dll"),
        Path.Combine(game, "manosaba_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
        AssetRipper.Primitives.UnityVersion.Parse("6000.0.48f1"), false);
    var ctx = Cpp2IL.Core.Cpp2IlApi.CurrentAppContext;
    var rows = new Dictionary<ulong, string>();
    foreach (var asm in ctx.Assemblies)
    foreach (var t in asm.Types)
    foreach (var m in t.Methods)
        if (m.UnderlyingPointer >= ImageBase) rows.TryAdd(m.UnderlyingPointer - ImageBase, t.FullName + "::" + m.Name);
    // 내보낸 il2cpp 런타임 함수 (il2cpp_*, GC 등)
    foreach (var (name, rva) in Exports(Path.Combine(game, "GameAssembly.dll"))) rows.TryAdd(rva, "[export] " + name);
    File.WriteAllLines(output, rows.OrderBy(r => r.Key).Select(r => $"{r.Key:X}\t{r.Value}"));
    Console.WriteLine($"{rows.Count} methods -> {output}");
}

// PE 내보내기 표 (이름, RVA)
static IEnumerable<(string, ulong)> Exports(string path)
{
    using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
    var dir = pe.PEHeaders.PEHeader.ExportTableDirectory;
    if (dir.Size == 0) yield break;
    var img = pe.GetEntireImage().GetContent().ToArray();
    int Off(int rva)
    {
        foreach (var s in pe.PEHeaders.SectionHeaders)
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize) return rva - s.VirtualAddress + s.PointerToRawData;
        throw new InvalidDataException();
    }
    int e = Off(dir.RelativeVirtualAddress);
    int names = BitConverter.ToInt32(img.AsSpan(e + 24)), funcs = BitConverter.ToInt32(img.AsSpan(e + 28)),
        nameRvas = BitConverter.ToInt32(img.AsSpan(e + 32)), ords = BitConverter.ToInt32(img.AsSpan(e + 36));
    for (int i = 0; i < names; i++)
    {
        int nameOff = Off(BitConverter.ToInt32(img.AsSpan(Off(nameRvas) + i * 4)));
        int end = Array.IndexOf(img, (byte)0, nameOff);
        ushort ord = BitConverter.ToUInt16(img.AsSpan(Off(ords) + i * 2));
        uint rva = BitConverter.ToUInt32(img.AsSpan(Off(funcs) + ord * 4));
        yield return (System.Text.Encoding.ASCII.GetString(img, nameOff, end - nameOff), rva);
    }
}

static async Task Cpu(string[] args)
{
    string etl = args[1];
    var map = LoadMap(args[2]);
    int ti = Array.IndexOf(args, "-thread");
    string threadFilter = ti > 0 ? args[ti + 1] : null;
    // 시작초 끝초 쌍을 여러 개 주면 한 번 읽어서 구간마다 집계
    var nums = args.Skip(3).Where((a, i) => ti < 0 || (i + 3 != ti && i + 3 != ti + 1)).Select(double.Parse).ToList();
    var windows = new List<(double From, double To)>();
    for (int i = 0; i + 1 < nums.Count; i += 2) windows.Add((nums[i], nums[i + 1]));
    if (windows.Count == 0) windows.Add((0, double.MaxValue));

    using var trace = TraceProcessor.Create(etl);
    var pendingCpu = trace.UseCpuSamplingData();
    var pendingSymbols = trace.UseSymbols();
    trace.Process();
    // Unity 심볼 서버(UnityPlayer)와 Microsoft 심볼 서버(Windows·드라이버)
    string cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(etl)), "symbols");
    await pendingSymbols.Result.LoadSymbolsAsync(new SymCachePath(Path.Combine(cache, "symcache")),
        new SymbolPath($"srv*{cache}*https://symbolserver.unity3d.com", $"srv*{cache}*https://msdl.microsoft.com/download/symbols"),
        null, new[] { Process }, new[] { "GameAssembly.dll" });

    foreach (var (from, to) in windows)
    {
        var self = new Dictionary<string, double>();
        var total = new Dictionary<string, double>();
        var threads = new Dictionary<string, double>();
        double all = 0;
        foreach (var s in pendingCpu.Result.Samples)
        {
            if (s.Process?.ImageName != Process) continue;
            double sec = (double)s.Timestamp.RelativeTimestamp.TotalSeconds;
            if (sec < from || sec > to) continue;
            string thread = $"{s.Thread?.Name ?? "?"}#{s.Thread?.Id}";
            if (threadFilter != null && !thread.Contains(threadFilter)) continue;
            double ms = (double)s.Weight.TotalMilliseconds;
            all += ms;
            threads[thread] = threads.GetValueOrDefault(thread) + ms;
            var frames = s.Stack?.Frames;
            if (frames == null || frames.Count == 0) continue;
            var seen = new HashSet<string>();
            for (int i = 0; i < frames.Count; i++)
            {
                string name = Name(frames[i], map);
                if (i == 0) self[name] = self.GetValueOrDefault(name) + ms;
                if (seen.Add(name)) total[name] = total.GetValueOrDefault(name) + ms;
            }
        }
        Console.WriteLine($"######## samples {all:F0} ms ({from}~{(to == double.MaxValue ? "끝" : to)} s)");
        Console.WriteLine("== threads"); Top(threads, all, 12);
        Console.WriteLine("== self (함수 자체)"); Top(self, all, 50);
        Console.WriteLine("== total (호출한 것 포함)"); Top(total, all, 80);
    }
}

static async Task Frames(string[] args)
{
    string etl = args[1];
    var map = LoadMap(args[2]);
    double threshold = args.Length > 3 ? double.Parse(args[3]) : 16;
    double from = args.Length > 5 ? double.Parse(args[4]) * 1000 : 0, to = args.Length > 5 ? double.Parse(args[5]) * 1000 : double.MaxValue;
    var dxgi = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    using var trace = TraceProcessor.Create(etl);
    var pendingCpu = trace.UseCpuSamplingData();
    var pendingEvents = trace.UseGenericEvents();
    var pendingSymbols = trace.UseSymbols();
    trace.Process();
    string cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(etl)), "symbols");
    await pendingSymbols.Result.LoadSymbolsAsync(new SymCachePath(Path.Combine(cache, "symcache")),
        new SymbolPath($"srv*{cache}*https://symbolserver.unity3d.com", $"srv*{cache}*https://msdl.microsoft.com/download/symbols"),
        null, new[] { Process }, new[] { "GameAssembly.dll" });

    // Present 시작 (DXGI 이벤트 42)
    var presents = pendingEvents.Result.Events
        .Where(e => e.ProviderId == dxgi && e.Id == 42 && e.Process?.ImageName == Process)
        .Select(e => (double)e.Timestamp.RelativeTimestamp.TotalMilliseconds)
        .Where(t => t >= from && t <= to).OrderBy(t => t).ToArray();
    if (presents.Length < 2) { Console.WriteLine("Present 이벤트 없음"); return; }
    var dts = new double[presents.Length - 1];
    for (int i = 0; i < dts.Length; i++) dts[i] = presents[i + 1] - presents[i];
    var sorted = dts.OrderBy(x => x).ToArray();
    double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
    Console.WriteLine($"frames={dts.Length} avg={dts.Average():F2}ms fps={1000 / dts.Average():F1} p50={P(0.5):F2} p95={P(0.95):F2} p99={P(0.99):F2} max={sorted[^1]:F2}");
    foreach (var lim in new[] { 16.7, 33.3, 50, 100, 250 })
        Console.WriteLine($"  >{lim}ms: {dts.Count(d => d > lim)}");

    // 메인 스레드 = UnityMain을 지나는 샘플이 가장 많은 스레드
    var samples = pendingCpu.Result.Samples.Where(s => s.Process?.ImageName == Process && s.Stack?.Frames != null).ToList();
    var mainId = samples.Where(s => s.Stack.Frames.Any(f => f.Symbol?.FunctionName == "UnityMain"))
        .GroupBy(s => s.Thread?.Id).OrderByDescending(g => g.Count()).First().Key;
    var main = samples.Where(s => s.Thread?.Id == mainId)
        .Select(s => ((double)s.Timestamp.RelativeTimestamp.TotalMilliseconds, s.Stack.Frames.Select(f => Name(f, map)).Distinct().ToArray()))
        .OrderBy(x => x.Item1).ToArray();
    // 거의 모든 샘플에 있는 이름(메인 루프 등)은 빼고 보여 줌
    var common = main.SelectMany(m => m.Item2).GroupBy(n => n).Where(g => g.Count() > main.Length * 0.9).Select(g => g.Key).ToHashSet();
    var times = main.Select(m => m.Item1).ToArray();

    var longFrames = Enumerable.Range(0, dts.Length).Where(i => dts[i] >= threshold).OrderByDescending(i => dts[i]).Take(25).OrderBy(i => i);
    foreach (int i in longFrames)
    {
        int a = Array.BinarySearch(times, presents[i]); if (a < 0) a = ~a;
        int b = Array.BinarySearch(times, presents[i + 1]); if (b < 0) b = ~b;
        var counts = new Dictionary<string, int>();
        for (int k = a; k < b; k++) foreach (var n in main[k].Item2) if (!common.Contains(n)) counts[n] = counts.GetValueOrDefault(n) + 1;
        Console.WriteLine($"== {presents[i] / 1000:F3}s  {dts[i]:F1}ms  (메인 샘플 {b - a})");
        foreach (var (n, c) in counts.OrderByDescending(x => x.Value).Take(14))
            Console.WriteLine($"    {c,4}  {(n.Length > 140 ? n[..140] : n)}");
    }
}

static async Task Tree(string[] args)
{
    string etl = args[1];
    var map = LoadMap(args[2]);
    double from = double.Parse(args[3]) * 1000, to = double.Parse(args[4]) * 1000;
    double minPct = args.Length > 5 ? double.Parse(args[5]) : 3;
    using var trace = TraceProcessor.Create(etl);
    var pendingCpu = trace.UseCpuSamplingData();
    var pendingSymbols = trace.UseSymbols();
    trace.Process();
    string cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(etl)), "symbols");
    await pendingSymbols.Result.LoadSymbolsAsync(new SymCachePath(Path.Combine(cache, "symcache")),
        new SymbolPath($"srv*{cache}*https://symbolserver.unity3d.com", $"srv*{cache}*https://msdl.microsoft.com/download/symbols"),
        null, new[] { Process }, new[] { "GameAssembly.dll" });
    var samples = pendingCpu.Result.Samples.Where(s => s.Process?.ImageName == Process && s.Stack?.Frames != null).ToList();
    var mainId = samples.Where(s => s.Stack.Frames.Any(f => f.Symbol?.FunctionName == "UnityMain"))
        .GroupBy(s => s.Thread?.Id).OrderByDescending(g => g.Count()).First().Key;
    var root = new Node();
    foreach (var s in samples)
    {
        double t = (double)s.Timestamp.RelativeTimestamp.TotalMilliseconds;
        if (s.Thread?.Id != mainId || t < from || t > to) continue;
        var node = root;
        root.Count++;
        // 프레임 목록은 맨 안쪽(잎)부터라서 뒤집어서 루트부터 내려감. 커널 프레임은 생략
        for (int i = s.Stack.Frames.Count - 1; i >= 0; i--)
        {
            string n = Name(s.Stack.Frames[i], map);
            if (n.StartsWith("ntoskrnl") || n.StartsWith("ntdll") || n.StartsWith("kernel32") || n.StartsWith("BepInEx") || n.Contains("IL_STUB")) continue;
            if (!node.Children.TryGetValue(n, out var child)) node.Children[n] = child = new Node();
            child.Count++;
            node = child;
        }
    }
    Console.WriteLine($"main samples {root.Count} ({from / 1000:F3}~{to / 1000:F3}s)");
    void Print(Node node, string name, int depth)
    {
        // 자식이 하나뿐인 사슬은 접어서 한 줄로
        var chain = new List<string> { name };
        while (node.Children.Count == 1 && node.Children.First().Value.Count == node.Count)
        {
            var only = node.Children.First();
            chain.Add(only.Key);
            node = only.Value;
        }
        string label = chain.Count > 3 ? $"{chain[0]} -> ... -> {chain[^2]} -> {chain[^1]}" : string.Join(" -> ", chain);
        Console.WriteLine($"{new string(' ', depth * 2)}{node.Count * 100.0 / root.Count,5:F1}% {(label.Length > 200 ? label[..200] : label)}");
        foreach (var (k, v) in node.Children.OrderByDescending(x => x.Value.Count))
            if (v.Count * 100.0 / root.Count >= minPct) Print(v, k, depth + 1);
    }
    foreach (var (k, v) in root.Children.OrderByDescending(x => x.Value.Count)) Print(v, k, 0);
}

static void Timeline(string etl, double binMs)
{
    using var trace = TraceProcessor.Create(etl);
    var pendingCpu = trace.UseCpuSamplingData();
    trace.Process();
    var bins = new SortedDictionary<long, Dictionary<string, double>>();
    foreach (var s in pendingCpu.Result.Samples)
    {
        if (s.Process?.ImageName != Process) continue;
        long bin = (long)((double)s.Timestamp.RelativeTimestamp.TotalMilliseconds / binMs);
        if (!bins.TryGetValue(bin, out var d)) bins[bin] = d = new();
        string th = s.Thread?.Name ?? $"#{s.Thread?.Id}";
        d[th] = d.GetValueOrDefault(th) + (double)s.Weight.TotalMilliseconds;
    }
    foreach (var (bin, d) in bins)
        Console.WriteLine($"{bin * binMs / 1000:F2}s  " + string.Join("  ", d.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key}:{x.Value:F0}")));
}

static (ulong[] Rva, string[] Name) LoadMap(string path)
{
    var lines = File.ReadAllLines(path);
    var rva = new ulong[lines.Length];
    var name = new string[lines.Length];
    for (int i = 0; i < lines.Length; i++)
    {
        int tab = lines[i].IndexOf('\t');
        rva[i] = Convert.ToUInt64(lines[i][..tab], 16);
        name[i] = lines[i][(tab + 1)..];
    }
    return (rva, name);
}

static string Name(StackFrame frame, (ulong[] Rva, string[] Name) map)
{
    var image = frame.Image;
    string module = image?.FileName ?? "?";
    if (module.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
    {
        ulong rva = (ulong)(frame.Address.Value - image.AddressRange.BaseAddress.Value);
        int i = Array.BinarySearch(map.Rva, rva);
        if (i < 0) i = ~i - 1;
        return i >= 0 ? "GA!" + map.Name[i] : $"GA+{rva:X}";
    }
    string fn = frame.Symbol?.FunctionName;
    return fn != null ? $"{Path.GetFileNameWithoutExtension(module)}!{fn}" : module;
}

static void Top(Dictionary<string, double> d, double all, int n)
{
    foreach (var (k, v) in d.OrderByDescending(x => x.Value).Take(n))
        Console.WriteLine($"{v,9:F1} ms {v / all * 100,5:F1}%  {(k.Length > 150 ? k[..150] : k)}");
}

sealed class Node
{
    public int Count;
    public readonly Dictionary<string, Node> Children = new();
}
