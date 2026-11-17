# Twin 데이터 형식과 규칙

## `<hull>.twin.json` (format `shipyard-twin/1`)

`pipeline/export_twin.py`가 ERP API로 만든다. Unity는 `Resources/ShipyardTwin/<hull>.twin`으로 읽는다.

| 키 | 출처(ERP API) | 내용 |
|---|---|---|
| `hull` | `vessel_details` + `hull_geometry` | 선종, 선명, 전장, 선폭, 형심, 흘수, 프레임 간격 |
| `placement` | `hull_placements` + `location_geometry` | 도크 ID, 선체 기준점의 야드 좌표와 방향, 도크 기준점 |
| `blocks[]` | `erection_timeline?hull=` | 블록 ID, 구역, 중량, 박스(`x/y/z_min·max`), 무게중심(`cog_*`), 탑재 단계(선행 체인 길이), 선행 블록, 기준·예정·실적일, `model_asset` |

모델 파일은 `Resources/ShipyardTwin/Models/<model_asset>.bytes`에 둔다. 내용은 OBJ 텍스트다. 모델이 없는 블록은 박스로 그린다.

## 좌표계

| 좌표계 | 축 | Unity 변환 |
|---|---|---|
| 선박 | x 선수 방향(선미 끝 0), y 좌현, z 위(기선 0), m | (x, z, y) |
| 야드 | 크레인 텔레메트리 x/y, z 위, heading은 x에서 y 방향 각도 | (x, z, y), 회전 Y축 −heading |
| OBJ 모델 | (선박 x, 선박 z, −선박 y), 원점은 블록 무게중심 | (x, y, −z), 면 방향 반전 |

오른손 좌표계 두 개를 (x, z, y)로 옮기면, 위에서 볼 때 x는 오른쪽, y는 화면 위쪽이다. 좌우가 뒤집히지 않는다.

## 재생 규칙 (`ErectionSchedule`)

- 블록의 날짜는 실적일, 예정일, 기준일 순서로 정한다.
- 크레인 1대 기준이다. 같은 날짜의 블록은 앞 날짜의 마지막 탑재 이후부터 그 날짜까지, 단계와 ID 순서로 하나씩 인양한다. 마지막 블록이 그 날짜에 착지한다.
- 인양 시간은 `LiftDays`(기본 2일)와 할당 구간 중 짧은 쪽이다.
- 날짜가 없는 블록은 마지막 블록 뒤에 하나씩 이어 붙인다.
- 인양은 두 구간으로 나뉜다. 앞 절반은 우현 쪽에서 훅 높이로 들어오고, 뒤 절반은 제자리로 내려간다. 갠트리 크레인은 인양 중인 블록을 따라간다.
