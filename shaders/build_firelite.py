"""FireLite 셰이더 번들 생성 (docs/ai/mahosaba-render.md C장)

Unity 에디터 없이 만든다. 게임 번들의 SH_Vefects_VFX_URP_Fire_Flames_01 셰이더에서 첫 패스(화면 그리기)의
속성·바인딩 메타데이터만 남기고, D3D11 정점·픽셀 코드를 FireLite.hlsl을 컴파일한 것으로 바꾼다.
원본의 다른 패스와 Vulkan·OpenGL 코드는 모두 뺀다. 게임과 같은 직렬화 형식이라 버전 호환 문제가 없다.

사용 (Windows, Python 3 + UnityPy, d3dcompiler_47.dll):
    python -I build_firelite.py <게임 폴더> <출력 번들 경로>
"""
import ctypes
import hashlib
import os
import struct
import sys

import lz4.block
import UnityPy

BUNDLE = "manosaba_Data/StreamingAssets/aa/StandaloneWindows64/general-prefabs_assets_all.bundle"
SOURCE_SHADER = "SH_Vefects_VFX_URP_Fire_Flames_01"
NAME = "LXIUM/FireLite"
D3D11 = 4                  # ShaderCompilerPlatform.D3D
UNITY_HEADER = 38          # Unity가 DXBC 앞에 붙이는 헤더 길이 (서브프로그램 버전 201509030 이후)


def blob_bytes(blob):
    vt = ctypes.cast(ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
    ptr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vt[3])(blob)
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vt[4])(blob)
    return ctypes.string_at(ptr, size)


def compile_hlsl(src, entry, target):
    d3d = ctypes.WinDLL("d3dcompiler_47.dll")
    f = d3d.D3DCompile
    f.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p,
                  ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint, ctypes.c_uint,
                  ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
    code, err = ctypes.c_void_p(), ctypes.c_void_p()
    hr = f(src, len(src), b"FireLite.hlsl", None, None, entry.encode(), target.encode(),
           1 << 15, 0, ctypes.byref(code), ctypes.byref(err))  # D3DCOMPILE_OPTIMIZATION_LEVEL3
    if hr != 0:
        raise SystemExit(f"{entry}: " + (blob_bytes(err).decode(errors="replace") if err else hex(hr & 0xFFFFFFFF)))
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


def main():
    game, out = sys.argv[1], sys.argv[2]
    src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "FireLite.hlsl"), "rb").read()
    vs, ps = compile_hlsl(src, "vert", "vs_5_0"), compile_hlsl(src, "frag", "ps_5_0")

    env = UnityPy.load(os.path.join(game, BUNDLE))
    bundle = env.file
    cab = next(k for k in bundle.files if not k.endswith((".resS", ".resource")))
    sf = bundle.files[cab]
    shader = next(o for o in sf.objects.values()
                  if o.type.name == "Shader" and o.read_typetree()["m_ParsedForm"]["m_Name"] == SOURCE_SHADER)
    ab = next(o for o in sf.objects.values() if o.type.name == "AssetBundle")

    t = shader.read_typetree()
    pf = t["m_ParsedForm"]
    p = pf["m_SubShaders"][0]["m_Passes"][0]
    vprog, fprog = p["progVertex"], p["progFragment"]
    # 하위 목록 가운데 D3D11 항목(GPU 프로그램 종류 16 = 정점 SM5, 18 = 픽셀 SM5)만 남김
    for prog, kind in ((vprog, 16), (fprog, 18)):
        subs, params = prog["m_PlayerSubPrograms"], prog["m_ParameterBlobIndices"]
        for i, lst in enumerate(subs):
            keep = [j for j, s in enumerate(lst) if s["m_GpuProgramType"] == kind]
            subs[i] = [lst[j] for j in keep]
            params[i] = [params[i][j] for j in keep]
    vsub = next(s for lst in vprog["m_PlayerSubPrograms"] for s in lst)
    fsub = next(s for lst in fprog["m_PlayerSubPrograms"] for s in lst)
    vpar = next(i for lst in vprog["m_ParameterBlobIndices"] for i in lst)
    fpar = next(i for lst in fprog["m_ParameterBlobIndices"] for i in lst)

    k = t["platforms"].index(D3D11)
    off, clen, dlen = t["offsets"][k][0], t["compressedLengths"][k][0], t["decompressedLengths"][k][0]
    old = split_entries(lz4.block.decompress(bytes(t["compressedBlob"][off:off + clen]), uncompressed_size=dlen))
    # 첫 패스가 쓰는 네 항목만 새 번호(0~3)로 남김
    entries = [old[vpar], replace_code(old[vsub["m_BlobIndex"]], vs), old[fpar], replace_code(old[fsub["m_BlobIndex"]], ps)]
    vprog["m_ParameterBlobIndices"] = [[0] if lst else [] for lst in vprog["m_ParameterBlobIndices"]]
    fprog["m_ParameterBlobIndices"] = [[2] if lst else [] for lst in fprog["m_ParameterBlobIndices"]]
    vsub["m_BlobIndex"], fsub["m_BlobIndex"] = 1, 3
    raw = join_entries(entries)
    packed = lz4.block.compress(raw, mode="high_compression", store_size=False)

    pf["m_Name"] = NAME
    pf["m_SubShaders"][0]["m_Passes"] = [p]
    pf["m_FallbackName"] = ""
    pf["m_CustomEditorName"] = ""
    t["m_Name"] = NAME
    t["platforms"] = [D3D11]
    t["offsets"], t["compressedLengths"], t["decompressedLengths"] = [[0]], [[len(packed)]], [[len(raw)]]
    t["compressedBlob"] = list(packed)
    t["stageCounts"] = [t["stageCounts"][k]]
    t["m_Dependencies"] = []
    guid = hashlib.md5(NAME.encode()).digest()
    t["m_AssetGUID"] = {f"data[{i}]": u32(guid, 4 * i) for i in range(4)}
    shader.save_typetree(t)

    a = ab.read_typetree()
    pptr = {"m_FileID": 0, "m_PathID": shader.path_id}
    a["m_Name"] = a["m_AssetBundleName"] = "lxium_mahosaba"
    a["m_PreloadTable"] = [pptr]
    a["m_Container"] = [[NAME.lower(), {"preloadIndex": 0, "preloadSize": 1, "asset": pptr}]]
    a["m_MainAsset"] = {"preloadIndex": 0, "preloadSize": 0, "asset": {"m_FileID": 0, "m_PathID": 0}}
    a["m_Dependencies"] = []
    a["m_SceneHashes"] = []
    ab.save_typetree(a)

    for pid in [pid for pid in sf.objects if pid not in (shader.path_id, ab.path_id)]:
        del sf.objects[pid]
    # 남은 두 객체의 타입 정보만 남김 (게임 스크립트 타입 트리 등을 빼서 번들을 작게)
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
    print(f"{out}: {len(data)} bytes, vs {len(vs)} ps {len(ps)} bytes")


if __name__ == "__main__":
    main()
