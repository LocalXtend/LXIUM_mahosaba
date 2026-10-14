"""LXIUM 셰이더 번들 생성: FireLite, CourtLite (docs/ai/mahosaba-render.md C장)

Unity 에디터 없이 만든다. 게임 번들의 원본 셰이더에서 첫 패스(화면 그리기)의 속성·바인딩 메타데이터만 남기고,
고른 D3D11 변형의 정점·픽셀 코드를 LXIUM HLSL을 컴파일한 것으로 바꾼다. 원본의 다른 패스·변형과
Vulkan·OpenGL 코드는 모두 뺀다. 게임과 같은 직렬화 형식이라 버전 호환 문제가 없다.

사용 (Windows, Python 3 + UnityPy, d3dcompiler_47.dll):
    python -I build_shaders.py <게임 폴더> <출력 번들 경로>
"""
import ctypes
import hashlib
import os
import struct
import sys

import lz4.block
import UnityPy

BUNDLE = "manosaba_Data/StreamingAssets/aa/StandaloneWindows64/general-prefabs_assets_all.bundle"
D3D11 = 4                  # ShaderCompilerPlatform.D3D
UNITY_HEADER = 38          # Unity가 DXBC 앞에 붙이는 헤더 길이 (서브프로그램 버전 201509030 이후)

# GPU 프로그램 종류: 15 = DX11 정점 SM4, 16 = 정점 SM5, 17 = 픽셀 SM4, 18 = 픽셀 SM5
LIT_BASE = {"_MAIN_LIGHT_SHADOWS", "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHT_SHADOWS",
            "_REFLECTION_PROBE_BLENDING", "_REFLECTION_PROBE_BOX_PROJECTION", "_SCREEN_SPACE_OCCLUSION"}
SHADERS = [
    {
        "source": "SH_Vefects_VFX_URP_Fire_Flames_01", "name": "LXIUM/FireLite", "hlsl": "FireLite.hlsl",
        # (종류, 남길 변형의 키워드 집합 또는 None = 그 종류 전부, 정의, 대상, 진입점)
        "vertex": [(16, None, [], "vs_5_0", "vert")],
        "fragment": [(18, None, [], "ps_5_0", "frag")],
    },
    {
        "source": "Universal Render Pipeline/Lit", "name": "LXIUM/CourtLite", "hlsl": "CourtLite.hlsl",
        "vertex": [(15, {"_MAIN_LIGHT_SHADOWS", "_ADDITIONAL_LIGHTS", "_NORMALMAP"}, ["CL_VS"], "vs_4_0", "vert")],
        "fragment": [
            (17, LIT_BASE | {"_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP"}, ["CL_PS", "CL_MAPS"], "ps_4_0", "frag"),
            (17, LIT_BASE | {"_ALPHATEST_ON"}, ["CL_PS", "CL_CUTOUT"], "ps_4_0", "frag"),
            (17, LIT_BASE | {"_SURFACE_TYPE_TRANSPARENT", "_ALPHAPREMULTIPLY_ON"}, ["CL_PS", "CL_PREMUL"], "ps_4_0", "frag"),
        ],
    },
]


def blob_bytes(blob):
    vt = ctypes.cast(ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
    ptr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vt[3])(blob)
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vt[4])(blob)
    return ctypes.string_at(ptr, size)


class Macro(ctypes.Structure):
    _fields_ = [("name", ctypes.c_char_p), ("definition", ctypes.c_char_p)]


def compile_hlsl(src, name, entry, target, defines):
    d3d = ctypes.WinDLL("d3dcompiler_47.dll")
    f = d3d.D3DCompile
    f.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p,
                  ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint, ctypes.c_uint,
                  ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
    macros = (Macro * (len(defines) + 1))(*[Macro(d.encode(), b"1") for d in defines], Macro(None, None))
    code, err = ctypes.c_void_p(), ctypes.c_void_p()
    hr = f(src, len(src), name.encode(), ctypes.cast(macros, ctypes.c_void_p), None, entry.encode(), target.encode(),
           1 << 15, 0, ctypes.byref(code), ctypes.byref(err))  # D3DCOMPILE_OPTIMIZATION_LEVEL3
    if hr != 0:
        raise SystemExit(f"{name} {defines}: " + (blob_bytes(err).decode(errors="replace") if err else hex(hr & 0xFFFFFFFF)))
    return blob_bytes(code)


def u32(b, p):
    return struct.unpack_from("<I", b, p)[0]


def align4(n):
    return (n + 3) & ~3


def split_entries(raw):
    """플랫폼 블롭 = 개수 + (오프셋, 길이, 세그먼트) 표 + 항목들 (서브프로그램·파라미터 블롭)"""
    n = u32(raw, 0)
    return [raw[o:o + l] for o, l, _ in (struct.unpack_from("<III", raw, 4 + 12 * i) for i in range(n))]


def join_entries(entries):
    head, body = struct.pack("<I", len(entries)), b""
    base = 4 + 12 * len(entries)
    for e in entries:
        head += struct.pack("<III", base + len(body), len(e), 0)
        body += e
    return head + body


def replace_code(entry, dxbc):
    """서브프로그램: 버전, 종류, 통계 4개, 키워드 목록, 코드(길이 + Unity 헤더 + DXBC, 4바이트 정렬), 나머지"""
    p = 24
    count = u32(entry, p)
    p += 4
    for _ in range(count):
        p += 4 + align4(u32(entry, p))
    length = u32(entry, p)
    code = entry[p + 4:p + 4 + length]
    if code[UNITY_HEADER:UNITY_HEADER + 4] != b"DXBC":
        raise SystemExit("unexpected subprogram layout")
    rest = entry[p + 4 + align4(length):]
    new = code[:UNITY_HEADER] + dxbc
    return entry[:p] + struct.pack("<I", len(new)) + new + b"\0" * (align4(len(new)) - len(new)) + rest


def make_lite(shader, spec, src_dir):
    """원본 셰이더 객체를 spec대로 바꿔 저장. 고른 변형만 남기고 코드는 LXIUM HLSL로"""
    src = open(os.path.join(src_dir, spec["hlsl"]), "rb").read()
    t = shader.read_typetree()
    pf = t["m_ParsedForm"]
    names = pf["m_KeywordNames"]
    p = pf["m_SubShaders"][0]["m_Passes"][0]

    k = t["platforms"].index(D3D11)
    old = b""
    blob = bytes(t["compressedBlob"])
    for off, clen, dlen in zip(t["offsets"][k], t["compressedLengths"][k], t["decompressedLengths"][k]):
        old += lz4.block.decompress(blob[off:off + clen], uncompressed_size=dlen)
    old = split_entries(old)

    entries, moved = [], {}

    def take(index, data=None):
        # 같은 파라미터 블롭을 여러 변형이 함께 쓰면 한 번만 옮김
        if data is None and index in moved:
            return moved[index]
        entries.append(old[index] if data is None else data)
        if data is None:
            moved[index] = len(entries) - 1
        return len(entries) - 1

    for stage in ("vertex", "fragment"):
        prog = p["progVertex" if stage == "vertex" else "progFragment"]
        rules = spec[stage]
        used = [0] * len(rules)
        for i, lst in enumerate(prog["m_PlayerSubPrograms"]):
            params = prog["m_ParameterBlobIndices"][i]
            keep_s, keep_p = [], []
            for j, s in enumerate(lst):
                kw = {names[x] for x in s["m_KeywordIndices"]}
                for r, (kind, keywords, defines, target, entry) in enumerate(rules):
                    if s["m_GpuProgramType"] != kind or (keywords is not None and kw != keywords):
                        continue
                    dxbc = compile_hlsl(src, spec["hlsl"], entry, target, defines)
                    keep_p.append(take(params[j]))
                    s["m_BlobIndex"] = take(None, replace_code(old[s["m_BlobIndex"]], dxbc))
                    keep_s.append(s)
                    used[r] += 1
                    break
            prog["m_PlayerSubPrograms"][i] = keep_s
            prog["m_ParameterBlobIndices"][i] = keep_p
        # 게임이 업데이트돼 변형 구성이 바뀌었으면 멈춤
        if any(u != 1 for u in used) and not all(rule[1] is None for rule in rules):
            raise SystemExit(f"{spec['source']} {stage}: 변형 일치 수 {used}")

    raw = join_entries(entries)
    packed = lz4.block.compress(raw, mode="high_compression", store_size=False)
    pf["m_Name"] = spec["name"]
    pf["m_SubShaders"][0]["m_Passes"] = [p]
    pf["m_FallbackName"] = ""
    pf["m_CustomEditorName"] = ""
    pf["m_Dependencies"] = []
    t["m_Name"] = spec["name"]
    t["platforms"] = [D3D11]
    t["offsets"], t["compressedLengths"], t["decompressedLengths"] = [[0]], [[len(packed)]], [[len(raw)]]
    t["compressedBlob"] = list(packed)
    t["stageCounts"] = [t["stageCounts"][k]]
    t["m_Dependencies"] = []
    guid = hashlib.md5(spec["name"].encode()).digest()
    t["m_AssetGUID"] = {f"data[{i}]": u32(guid, 4 * i) for i in range(4)}
    shader.save_typetree(t)
    return len(entries)


def main():
    game, out = sys.argv[1], sys.argv[2]
    src_dir = os.path.dirname(os.path.abspath(__file__))
    env = UnityPy.load(os.path.join(game, BUNDLE))
    bundle = env.file
    cab = next(k for k in bundle.files if not k.endswith((".resS", ".resource")))
    sf = bundle.files[cab]
    ab = next(o for o in sf.objects.values() if o.type.name == "AssetBundle")
    shaders = {}
    for o in sf.objects.values():
        if o.type.name == "Shader":
            shaders.setdefault(o.read_typetree()["m_ParsedForm"]["m_Name"], o)

    kept = []
    for spec in SHADERS:
        shader = shaders[spec["source"]]
        n = make_lite(shader, spec, src_dir)
        kept.append((spec["name"], shader))
        print(f"{spec['name']}: {n} entries")

    a = ab.read_typetree()
    a["m_Name"] = a["m_AssetBundleName"] = "lxium_mahosaba"
    a["m_PreloadTable"] = [{"m_FileID": 0, "m_PathID": s.path_id} for _, s in kept]
    a["m_Container"] = [[name.lower(), {"preloadIndex": i, "preloadSize": 1, "asset": {"m_FileID": 0, "m_PathID": s.path_id}}]
                        for i, (name, s) in enumerate(kept)]
    a["m_MainAsset"] = {"preloadIndex": 0, "preloadSize": 0, "asset": {"m_FileID": 0, "m_PathID": 0}}
    a["m_Dependencies"] = []
    a["m_SceneHashes"] = []
    ab.save_typetree(a)

    keep_ids = {ab.path_id} | {s.path_id for _, s in kept}
    for pid in [pid for pid in sf.objects if pid not in keep_ids]:
        del sf.objects[pid]
    # 남은 객체의 타입 정보만 남김 (게임 스크립트 타입 트리 등을 빼서 번들을 작게)
    used = sorted({o.type_id for o in sf.objects.values()})
    sf.types = [sf.types[i] for i in used]
    for o in sf.objects.values():
        o.type_id = used.index(o.type_id)
    sf.script_types, sf.ref_types, sf.externals = [], [], []
    # 게임 번들과 같은 내부 파일 이름이면 Unity가 이미 로드된 번들로 보고 거부하므로 바꿈
    new_cab = "CAB-" + hashlib.md5(b"LXIUM_mahosaba").hexdigest()
    sf.name = new_cab
    bundle.files = {new_cab: sf}
    data = bundle.save(packer="lz4")
    with open(out, "wb") as f:
        f.write(data)
    print(f"{out}: {len(data)} bytes")


if __name__ == "__main__":
    main()
