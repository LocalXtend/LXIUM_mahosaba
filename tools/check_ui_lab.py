"""1080p 재판 대사의 off/on/off/on 실험에서 대사 영역과 프레임 통계를 검증한다. Pillow 필요."""
import re
import sys
from pathlib import Path

from PIL import Image, ImageChops


def check(folder):
    names = ("00_revealOff", "01_revealOn", "02_revealOff", "03_revealOn")
    crops = []
    for name in names:
        with Image.open(folder / (name + ".jpg")) as image:
            assert image.size == (1920, 1080), "이 검사는 1080p 재판 대사 영역 전용"
            crops.append(image.convert("RGB").crop((410, 850, 1300, 1040)))
    for name, crop in zip(names[1:], crops[1:]):
        assert ImageChops.difference(crops[0], crop).getbbox() is None, name + ": 대사 영역이 다름"
    rows = {name: (fps, ms) for name, fps, ms in re.findall(
        r"^(\d\d_reveal\w+) .*?fps=([\d.]+) avg=([\d.]+)ms",
        (folder / "lab.txt").read_text(encoding="utf-8"), re.M)}
    # 이름마다 fps·평균 ms 두 값을 유지하며 네 구간이 모두 끝났는지 확인한다.
    assert set(rows) == set(names), "완료된 네 측정 구간 필요"
    print("PASS: four dialogue crops are byte-identical (decoded RGB)")
    for name in names:
        print(name, rows[name])


if __name__ == "__main__":
    check(Path(sys.argv[1]))
