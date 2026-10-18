using System.Reflection;
using System.Security.Cryptography;

static class HookCheck
{
    const ulong ImageBase = 0x180000000;
    const string UnityVersion = "6000.0.48f1";

    internal static void Run(string gameDir, string[] extraTargets = null)
    {
        string binary = Path.Combine(gameDir, "GameAssembly.dll");
        string metadata = Path.Combine(gameDir, "manosaba_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        foreach (string path in new[] { binary, metadata })
        {
            using var input = File.OpenRead(path);
            Console.WriteLine($"{Path.GetFileName(path)} sha256={Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()}");
        }
        var assembly = typeof(Cpp2IL.Core.Cpp2IlApi).Assembly;
        Console.WriteLine($"Cpp2IL={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString()} Unity={UnityVersion}");

        Cpp2IL.Core.Cpp2IlApi.Init();
        Cpp2IL.Core.Cpp2IlApi.ConfigureLib(false);
        Cpp2IL.Core.Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
            AssetRipper.Primitives.UnityVersion.Parse(UnityVersion), false);

        // map.tsv는 주소당 한 이름만 남기므로 ICF 검사에는 쓰지 않음.
        var methods = Cpp2IL.Core.Cpp2IlApi.CurrentAppContext.Assemblies
            .SelectMany(a => a.Types).SelectMany(t => t.Methods.Select(m =>
                (Address: m.UnderlyingPointer, Name: t.FullName + "::" + m.Name))).ToArray();
        var byAddress = methods.Where(m => m.Address >= ImageBase).GroupBy(m => m.Address)
            .ToDictionary(g => g.Key, g => g.ToArray());
        string[] targets = extraTargets is { Length: > 0 } ? extraTargets : new[]
        {
            "Naninovel.UI.RevealFader::Update",
            "Naninovel.UI.RevealFader::HandleTextChanged",
            "Naninovel.UI.RevealFader::OnEnable",
            "Naninovel.UI.RevealFader::OnDisable",
            "Naninovel.UI.RevealState::SetLast",
            "Naninovel.UI.RevealState::Reset",
            "Naninovel.UI.RevealState::SetLatAppendIndex"
        };
        // 렌더링 쪽 패치 대상 (check-render-hooks)
        int failed = 0;
        Console.WriteLine($"scope={methods.Length} method declarations; {byAddress.Count} native addresses");
        foreach (string target in targets)
        {
            var matches = methods.Where(m => m.Name == target).ToArray();
            if (matches.Length != 1 || matches[0].Address < ImageBase)
            {
                Console.WriteLine($"FAIL {target}: declarations={matches.Length}, valid native entry required");
                failed++;
                continue;
            }
            var aliases = byAddress[matches[0].Address];
            Console.WriteLine($"{(aliases.Length == 1 ? "PASS" : "FAIL")} RVA=0x{matches[0].Address - ImageBase:X} declarations={aliases.Length} {target}");
            foreach (var alias in aliases) Console.WriteLine($"  {alias.Name}");
            if (aliases.Length != 1) failed++;
        }
        if (failed != 0) throw new InvalidDataException($"{failed} UI hook targets are absent or share native code; do not patch them.");
    }
}
