## 에이전트 개요
마법소녀의 마녀재판 최적화 프로젝트 LXIUM_mahosaba, 최적화와 모드 호환성을 동시에 지키는 것이 목적.

## 프로젝트 설명
LXIUM 프로젝트라는 최적화 게임 프로젝트의 일부분입니다.
마법소녀의 마녀재판 최적화 프로젝트 LXIUM_mahosaba

ver.txt에 버전을 작성합니다.

## 애드온 구조 (필수)
이 프로젝트의 결과물은 LXIUM 런처가 설치·제거하는 애드온 패키지입니다.
- 규격: LXIUM 저장소의 `docs/ai/addon.md` (https://github.com/LocalXtend/LXIUM/blob/dev/docs/ai/addon.md. LXIUM 저장소의 `optimizeMods/` 안에 clone했으면 `../../docs/ai/addon.md`). 빌드·패키징 작업 전에 반드시 읽고 따릅니다.
- 이 프로젝트의 값: `id` = `LXIUM_mahosaba`, `game.id` = `mahosaba`.
- 규격으로 해결되지 않는 것이 있으면 임의로 확장하지 말고 규격 수정을 먼저 제안합니다.

## 문서
- `docs/human/`은 사람용, `docs/ai/`는 AI용 문서입니다. 같은 주제는 같은 파일 이름으로 양쪽에 두고, 한쪽을 고치면 다른 쪽도 같은 커밋에서 고칩니다.
- `mahosaba.md`: 게임 분석 (구조·근거), `mahosaba-render.md`: LXIUM 렌더링 설계·측정 기록

## 코드 작성법
- 생각을 위한 주석이 아닌 코드를 설명하는 주석을 작성하세요.
- 공개 저장소의 일부이므로 비밀값, 이 PC의 경로(`C:\Users\...`), 개인 정보를 코드에 넣지 않습니다.

## 버전 관리

버전은 x.y.z형태로 작성합니다. 
x: 프로젝트에 뿌리 자체가 변경되어 많은 변화가 예상될 때
(예시: 언어 변경(c++ -> c#), 새로운 전환 업데이트(지금까지 이것과 관련한 것만 있었는데 더 확장된 기능을 담당하거나) 같은)
y: 프로젝트에 새로운 기능이 생겼을 때 (기능 확장과 새로운 기능에 근본적인 차이는 새로운 기능은 그 배이스를 바탕이자만, 기능 확장은 새로운 배이스와 라이브러리를 별도 제작하는 경우)
z: 프로젝트에 오류나 원래 있던 기능이 달라졌을 때

## git 관리
git은 dev 브렌치를 사용합니다.
기본적으로 git 행위를 지속적으로 업데이트합니다.
항상 코드 수정을 할 때마다 git을 합니다.

git 저장소는 이 프로젝트 전용입니다 (https://github.com/LocalXtend/LXIUM_mahosaba). 런처·규격 문서가 있는 LXIUM 저장소와 따로 커밋합니다.
로컬에서는 LXIUM 저장소의 `optimizeMods/LXIUM_mahosaba/`에 clone해 둡니다. LXIUM 저장소는 `optimizeMods/`를 git에서 제외합니다.
릴리즈 태그·제목은 `<id>-v<버전>` 형식입니다 (예: `LXIUM_sephiria-v1.0.0`). `id`는 애드온 구조 절의 값입니다.
릴리즈에는 `dist/<id>-<버전>.zip`을 올리고, 본문은 LXIUM 저장소 AGENTS.md의 "git 릴리즈에 관해서" 형식을 따릅니다.
릴리즈한 뒤 LXIUM 저장소에서 `build.ps1`로 공식 애드온 모음(catalog.json)을 갱신해 커밋합니다 (LXIUM `docs/ai/catalog.md` 4장). 모음의 sha256과 같아야 하므로 릴리즈에는 `build.ps1`이 모은 zip을 그대로 올립니다.

커밋은 GitHub 계정 LocalXtend로 합니다 (`user.name=LocalXtend`, `user.email=285211027+LocalXtend@users.noreply.github.com`).

## Readme 작성법
이 저장소의 README.md는 아래 내용을 그대로 씁니다. 버전 줄만 바꿉니다.
(현재 = 방금 릴리즈한 버전)
```
# LXIUM_mahosaba

마법소녀의 마녀재판 게임의 최적화를 진행합니다.
또한 모드 호환성을 지켜냅니다.

## 버전
> 현재: `n.m.k`

- 2026 ⓒ iyu.e — https://iyu-e.tistory.com/
```