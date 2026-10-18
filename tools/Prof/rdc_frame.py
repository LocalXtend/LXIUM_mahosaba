# RenderDoc 캡처(.rdc)의 GPU 시간을 패스(그리는 대상)·호출별로 정리. qrenderdoc 안의 파이썬으로 실행:
#   qrenderdoc.exe --python rdc_frame.py   (환경 변수 RDC_IN = 캡처 경로, RDC_OUT = 결과 텍스트 경로)
# 끝나면 프로세스를 바로 종료함 (UI를 띄우지 않음)
import os
import renderdoc as rd

src, dst = os.environ["RDC_IN"], os.environ["RDC_OUT"]
out = open(dst, "w", encoding="utf-8")


def log(s):
    out.write(s + "\n")
    out.flush()


try:
    rd.InitialiseReplay(rd.GlobalEnvironment(), [])
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    status, ctrl = cap.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError("replay failed: " + str(status))

    sf = ctrl.GetStructuredFile()
    names = {r.resourceId: r.name for r in ctrl.GetResources()}
    texinfo = {t.resourceId: t for t in ctrl.GetTextures()}
    dur = {r.eventId: r.value.d for r in ctrl.FetchCounters([rd.GPUCounter.EventGPUDuration])}

    rows = []  # (eventId, path, name, flags, target, gpu ms)

    def walk(actions, path):
        for a in actions:
            name = a.GetName(sf)
            target = ""
            outs = [o for o in a.outputs if o != rd.ResourceId.Null()]
            if outs:
                t = texinfo.get(outs[0])
                size = f" {t.width}x{t.height}" if t else ""
                target = names.get(outs[0], str(outs[0])) + size
            elif a.copyDestination != rd.ResourceId.Null():
                target = "copy->" + names.get(a.copyDestination, str(a.copyDestination))
            rows.append((a.eventId, path, name, a.flags, target, dur.get(a.eventId, 0.0) * 1000.0, a.numIndices, a.numInstances))
            if a.children:
                walk(a.children, path + "/" + name)

    walk(ctrl.GetRootActions(), "")
    leaf = [r for r in rows if r[3] & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch | rd.ActionFlags.Clear | rd.ActionFlags.Copy | rd.ActionFlags.Resolve | rd.ActionFlags.Present)]
    total = sum(r[5] for r in leaf)
    draws = [r for r in leaf if r[3] & rd.ActionFlags.Drawcall]
    log(f"actions={len(rows)} draws={len(draws)} gpu_total={total:.3f}ms")

    log("== by render target (GPU ms, draws)")
    by_target = {}
    for r in leaf:
        k = r[4] or "(none)"
        ms, n = by_target.get(k, (0.0, 0))
        by_target[k] = (ms + r[5], n + (1 if r[3] & rd.ActionFlags.Drawcall else 0))
    for k, (ms, n) in sorted(by_target.items(), key=lambda x: -x[1][0])[:30]:
        log(f"{ms:8.3f} ms {n:4d} draws  {k}")

    log("== by marker path (GPU ms)")
    by_path = {}
    for r in leaf:
        by_path[r[1]] = by_path.get(r[1], 0.0) + r[5]
    for k, ms in sorted(by_path.items(), key=lambda x: -x[1])[:30]:
        log(f"{ms:8.3f} ms  {k[-160:]}")

    log("== top actions")
    for r in sorted(leaf, key=lambda r: -r[5])[:40]:
        log(f"{r[5]:8.3f} ms  eid {r[0]:5d}  {r[2][:70]:70s} idx={r[6]} inst={r[7]}  -> {r[4]}")

    log("== sequence (target changes)")
    last = None
    for r in leaf:
        if r[4] != last:
            log(f"eid {r[0]:5d}  {r[4]}")
            last = r[4]

    ctrl.Shutdown()
    cap.Shutdown()
    rd.ShutdownReplay()
except Exception as e:
    import traceback
    log("ERROR " + repr(e))
    log(traceback.format_exc())
finally:
    out.close()
    os._exit(0)
