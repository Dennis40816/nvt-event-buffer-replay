# NVT Event Buffer Replay 0.0.3 重構 TODO

更新日期：2026-08-20

0.0.3 定位為穩定化與模組化版本。本版本先降低 UI freeze、取消失效、狀態互相干擾與視覺回歸風險，再為 custom register profile、raw waveform、kernel log 與 live input 建立可持續擴充的落點。

> Owner 已核准 0.0.3 重構方向。既有 `0.0.2` 歷史保持不變；以下勾選項只代表已有 commit 與測試證據，不等同正式 release gate 已關閉。

## Baseline

- Branch：`0.0.3`
- 參考 commit / tag：`1e2567b` / `0.0.2`
- Production handwritten C# / XAML：23,880 行；通過 25,000 行 architecture review threshold 與 30,000 行 hard cap。
- Core / parser / rendering / CLI tests：316 / 316 passed。
- Avalonia tests：111 / 111 passed；28 張 approved screenshots 由 exact pixel gate 驗證。
- 私有有效 KingstVIS golden `063ad09…`：568 logical frames；過期的 `4bec1b…` 不再作為 KingstVIS schema gate。
- Release identity 已統一為 `VERSION=0.0.3`；正式 `v0.0.3` tag 尚未建立。
- `MainWindow.axaml.cs` 只保留 142 行 constructor/bootstrap；Settings、Sidecar、Paint、Output、Review/Inspector、Playback、Capture、Shell 已拆為 8 個 partial，styles 位於獨立 resource dictionaries。`MainWindow.axaml` 的 UserControl 拆分保留給後續版本。

## 重構原則

- 原始 bytes、raw text、stable ID、source hash 與 physical provenance 永遠不可被 parser 或 UI 改寫。
- 結構抽離 commit 不混入視覺或行為變更；需要變更行為時使用獨立 commit 與測試。
- 不進行一次性的全面 MVVM 重寫；沿現有功能邊界逐步抽離。
- 不導入 docking framework、任意 DLL plugin 或新的大型 UI framework。
- Paint 與 MP4 仍共用 canonical `ReplayScene`；Preview 必須和 final export 使用相同 frame plan、layout 與 renderer。
- 每個昂貴工作必須可取消、可回報進度，且舊 operation 不可覆蓋新 session。
- 每項功能使用獨立 commit，完成測試與實際截圖 review 後才進下一項。

## 目標架構

```text
Source adapters
    -> CaptureDecodeController
        -> format decoder / Desay assembler
        -> ReplayWorkspaceBuilder (background + cancellation)
            -> immutable ReplayWorkspace
                -> PlaybackController
                -> PaintWorkspaceViewModel
                -> ReviewInspectorViewModel
                -> OutputWorkspaceViewModel
                    -> ExportJobService
```

`MainWindow` 最終只保留 application shell、workspace navigation、responsive rail、global command 與 window lifecycle。

## Phase 0 — Release identity 與可重現 baseline

- [x] 將 `VERSION`、package name、assembly metadata 與 manifest 的版本策略統一。
- [x] 0.0.3 使用 `VERSION=0.0.3`；正式 tag 固定為 `vX.Y.Z`（尚未建立 release tag）。
- [x] 不重寫已推送的 `0.0.2` tag；在 release note 標示它是 milestone tag。
- [x] CI 新增 release identity gate：tag、`VERSION`、package metadata 不一致即失敗。
- [x] 固定 baseline test 數、長 golden hash/records/frames、效能機器與 commit。
- [x] 重生目前 source 對應的 Paint、Output、Heatmap、Settings approved screenshots，並加入 advanced Review/Inspector states。

完成條件：從任一正式 tag 產出的檔名、manifest、版本文字與 GitHub Release 身分完全一致。

## Phase 1 — 可取消的 ReplayWorkspaceBuilder

- [x] 新增 `ReplayWorkspaceBuildRequest`、`ReplayWorkspaceBuildResult` 與 `ReplayWorkspaceBuilder`。
- [x] 將 Common decode、Desay assemble/decode、replay materialization、extent、trail index、auto-pause index 移出 UI thread。
- [x] `CancellationToken` 傳入 `CaptureSession.DecodeCommon`、`DecodeDesay97`、`Desay97Assembler` 與所有長迴圈。
- [x] 加入 operation generation；只有最後一個仍有效的 operation 可以提交 UI state。
- [x] 完整 workspace 建立成功後一次性切換；取消或失敗時保留上一份完整結果。
- [x] 進度使用 typed phases：Probe、Hash、Index、SourceReady、Select、Project、Decode、Build、Ready；stale generation progress 不可回寫 UI。

完成條件：載入／解碼期間 UI 可操作 Cancel；取消後無背景 CPU 長跑、無 stale continuation、raw explorer 與上一份有效結果仍可用。

## Phase 2 — Replay index 與長軌跡效能

- [x] Replay snapshots、extent、trail history 與 auto-pause index 各自只建立一次，並在 background linear pass 中完成。
- [x] Contact presence 使用固定寬度 bitmask，避免 workspace 每 frame 建立多個 `HashSet<byte>`。
- [ ] Host state checkpoint 仍保留小型 dictionary；固定 11-slot array/struct 屬後續 micro-optimization，現有 100k/長 Loop gate 未顯示它是瓶頸。
- [x] Checkpoint 位置改用直接索引或 binary search，移除 `SortedDictionary.Last(...)` 熱路徑。
- [x] 保留 2,048-point 無損 trail chunk；切片只影響繪圖批次，不刪除線或 report points。
- [x] 量測 trail history、random seek、21,600 次 replay advance（等效 120 Hz 連續 3 分鐘）與 stale generation allocation；第二輪 allocation 不成長。

完成條件：

- 100,003 report points 完整保留。
- 250 次亂序 seek 不重建完整軌跡。
- 私有 2,080-frame golden 在 0.01×、0.1×、1×、10×、MAX 與 Loop 下無 freeze。
- 3 分鐘 Loop 後記憶體不持續線性成長，速度／Loop／Mark 操作回應目標小於 100 ms。

## Phase 3 — MP4 Preview / Export frame plan

- [x] `ReplayFramePlan` 直接建立 RLE repeat count，不逐一 materialize 每個 output frame。
- [x] 保存 cumulative output end index，以 binary search 完成 output-frame -> logical-frame 對應。
- [x] frame count、repeat count 與 duration calculation 使用 `long` 並加入 overflow / hard-limit protection。
- [x] 先估算並檢查 100,000,000-frame hard limit，再以 cancellable single-flight background planner 建立完整 plan；超限不開 picker 或 export job。
- [x] planning、rendering、FFmpeg piping 全程共用 cancellation 與 progress。
- [x] Preview scrub、repeat、fullscreen 與 final MP4 共用同一份 immutable plan；final export 拒絕 stale replay/settings identity。
- [x] Output range 使用獨立 1-based From／To 欄位與 MP4 timeline handles；不再借用或改寫 Paint Loop range，Full 可一鍵恢復全 capture。
- [x] 統一 UI 與 CLI 的 FPS 上限為 240；180／240 FPS 具有相同驗證規則。

完成條件：Recorded timing 包含長 idle gap 時不阻塞 UI；取消後不留下 partial output，Preview 與 final manifest 的 frame count、duration、range 完全相同。

## Phase 4 — MainWindow 漸進式拆分

依下列順序進行，每一步保持既有 UI 與測試行為：

- [x] 抽出 `PlaybackController`：clock、speed、play/pause、step、Loop、auto-pause、generation。
- [x] 抽出 `ReplayOutputWorkspace`、`ReplayOutputPlanService`、`OutputReportService` 與 `ExportJobService`：content type、video options、preview、progress、cancel。
- [x] 抽出 `ReplayPaintWorkspace`：panel extent、point view、trail、Trace/Points、axis transform、annotations 與 invalidation contract。
- [x] 抽出 `ReviewInspectorWorkspace`：current frame、contact selection、finding navigation、marker lifecycle 與 sidecar state。
- [x] 抽出 `CaptureDecodeController`：source choice、version/profile validation、atomic decode、register annotation 與 latest-wins generation。
- [ ] 將 Paint、Output、Review、Inspector 的 XAML 視覺樹拆為獨立 View/UserControl；code-behind 已先按功能拆成 partial，避免此版同時承擔 NameScope/focus/event-routing 風險。
- [x] 將 window-local styles 拆入 typography、buttons、fields、transport、inspector、output 等 ResourceDictionary。
- [x] 移除 canonical Capture/Paint/Output/Review state 的重複 ownership；`MainWindow.axaml.cs` 降至 142 行，功能 handlers 分置 8 個 partial。

完成條件：`MainWindow` 不再直接包含 replay/export/decode 演算法；ViewModel/controller 可在無 Window 的情況下測試。結構抽離前後 approved screenshot 應維持零預期差異。

## Phase 5 — 共用 format / application pipeline

- [x] 將目前 descriptor-only format registry 升級為可供 UI/CLI 使用的 executable provider。
- [x] provider 封裝 descriptor、configuration validation、decode、replay projection、diagnostics 與 display identity；inspect presenter 保留 typed dispatch 以維持既有 JSON schema。
- [x] UI 與 CLI 都經 executable format registry；inspect 只保留 typed presenter dispatch 以維持既有 JSON/text schema。
- [x] Desay two-transaction 保留獨立 assembler；補 cancellation，並維持 transport-stream/slave transaction scope。
- [x] CRC ownership 明確化：Common decoder 單點驗證；Desay assembler 計算並攜帶 captured/computed CRC，semantic decoder 不重算。
- [x] KingstVIS、DSL、NDS 與 built-in LA adapters 套用共用 acceptance contract：probe ambiguity、malformed row、cancellation、stable ID、address normalization、ACK unavailable、transaction boundary、simulator round trip、provenance。
- [x] Header 提供 7-bit I²C decode target（預設 `0x01`）；Common／Desay／IC inference 只消費所選 slave，Raw Explorer 保留其他設備與原始 register byte 作為 evidence。

完成條件：新增第三種 Event Buffer family 時不需要同時修改 MainWindow、CLI 三條 command flow 與 Rendering switch。

## 已落地 commits 與證據

- `ab2264b`：0.0.3 release identity 與 CI gate。
- `47862f5`：可取消、線性建立的 replay workspace；含 100k-frame、取消與 Common/Desay 測試。
- `fdc148a`、`2f82531`：compact frame plan、長時段 overflow/hard-limit 與 240 FPS CLI contract。
- `4d27309`、`d2d6080`：executable format registry 與 CLI 共用 decode pipeline；Common 0x82–0x85、Desay Standard/Benz Palm 均有契約測試。
- `6eb350c`：MainWindow styles 拆為獨立資源；selector 數與順序一致，Avalonia 48/48 tests passed。
- `96aa3db`：load/decode 完整建置後原子切換；取消或失敗保留上一份結果。
- `3998683`、`7f65a6b`：register annotation 改為 profile projection；source records 不複製，Raw、Inspector 與 readable log 使用相同 projected semantics。
- `0abb612`、`c6132f5`：headless playback controller 與 MainWindow wiring；速度、Loop、auto-pause 與 stale tick generation 有獨立測試。
- `bd4b720`：無損 chunked trail history；100,003 points 完整保留，建立 allocation 降低 93.7%，250 次 random seek allocation 降低 69.3%。
- `5814e19`、`1fc4b0b`：deterministic UI capture 與顯式 candidate matrix；瑕疵圖不會直接成為 approved baseline。
- `7f54e5a`、`c048703`、`0c244c3`、`c3a4961`：Output workspace、immutable plan、background export、hard-limit、FFmpeg cancel cleanup 與 cancellable shared analysis。
- `ae65c98`、`3cec4e6`：UI decode 經 prepared capture controller；zero-frame、load-to-load、cancel、close 與 stale continuation race 有決定性測試。
- `185dae6`、`ee377c7`、`4cdc378`：Paint workspace canonical state、annotation/legend index、100k trail 與 hidden Output deferred rendering。
- `b7a6cd9`、`e1662a1`、`9aa08f1`：Review/Inspector canonical state、single-frame Mark、sidecar strict replace、100k finding navigation 與 capture/profile churn guard。
- `c79376c` 至 `800a94f`：MainWindow 純搬移成 8 個功能 partial；每一步 build/targeted tests 通過，root code-behind 降至 142 行。
- `2d1465a`、`022ba60`：既有 20 張 approved screenshots；本輪再加入 MP4／Heatmap／Reported Points／Data Package 的共用 Range 版面與 4 張 Reported Points baseline，目前 24 張 base + 4 張 advanced 具有 exact gate。

目前驗證基線：core/parser/rendering/CLI 316 tests passed；Avalonia 111 tests passed；28 張 approved PNG exact compare 通過。私有 Common KingstVIS／DSL／NDS smoke 已重跑。尚未關閉的 release gate 為私有 Desay golden、125% UI scale/相關 visual baseline、正式 tag 與 package/release publish。

## Phase 6 — Register annotation projection

- [x] `SourceRecord` 只保存 transport normalization 與原始 provenance，不因 IC profile 切換而複製整份 capture。
- [x] 新增 `RegisterAnnotationIndex`，以 record stable ID 與 profile identity/hash 建立 projection。
- [x] register diagnostics 與 transport/parser diagnostics 分離。
- [x] profile 切換只重建 annotation projection 與其專屬 findings。
- [x] 為下一版 custom register profile JSON 預留 schema version、profile ID、IC/FW scope、canonical serialization 與 SHA-256 identity；本版不啟用任意 profile 自動載入。

完成條件：切換 IC profile 不再 O(N) 複製所有 `SourceRecord`；同名但內容不同的 profile 不會被視為同一份設定。

## Phase 7 — Design system 與視覺驗收

- [x] 建立工程工具 typography hierarchy；關鍵資訊使用 12.5–20 px，不再依賴 9.5/10.5 px 小字。
- [x] 以 Compact Fluent density 與 4/8/12/16 spacing family 統整 controls；窄版 chrome 另有 bounds tests。
- [x] Accent、Success、Severity、Contact ID、Heat density 使用獨立色彩角色。
- [x] Button、Toggle、ComboBox 的 rest、hover、pressed、checked、focus、disabled 使用共用 style resources。
- [x] Header 以 capture filename 為主要 identity；adapter、format、version、IC profile 為次要 metadata。
- [x] Header 將 Load/Replace capture 移到來源 identity 一側，移除與主分頁重複的 Output 入口及只會重開既有下拉選單的 Edit。
- [x] floating toolbar/settings bounds 納入 label placement 與 Fit 的 canvas safe area。
- [x] Inspector 恢復 320–520 px contract，label/value layout 自適應；Alarm/All Break/10-contact 最壞狀態有 bounds 或 approved evidence。
- [x] MP4、Heatmap、Reported Points、Data Package 維持相同左內容／右設定骨架，非 Paint 頁面隱藏 transport。
- [x] Output type、即時設定、Output Info 與 Export footer 共用固定右欄；Export 永遠位於右下角，切換 Data Package 不再改變版面寬度。
- [x] Output resolution 唯一跟隨 Paint panel resolution；Frame-paced 120 Hz 與 120 FPS 保持預設，Output Info 顯示格式、解析度、frames/duration/range 與估計輸出大小。
- [x] Output Info 將 H.264／PNG 大小明確標為估計範圍，說明其受畫面內容與壓縮影響；匯出完成後改顯示實際檔案大小。
- [x] Reported Points PNG 保存選定範圍內每一筆有效 reported coordinate，以 Contact ID 固定色顯示，支援鄰近格索引 hover，並使用 Paint resolution 匯出；100,000-point contract 已納入測試。
- [x] Transport auto-pause 使用 anchored panel；窄版隱藏低優先說明文字並保留必要狀態與控制。
- [ ] 增加 Comfortable / Compact 或 100% / 110% / 125% UI scale 設計與驗證。

### Approved screenshot matrix

- [x] Paint：1920×1080、1180×720，dark/light；1672×720 由 responsive bounds tests 覆蓋，未另存 approved PNG。
- [x] Paint/Inspector states：1／5／10 contacts、Finger／Glove／Palm、Alarm、All Break、Mark、Loop、auto-pause 均有 interaction/bounds tests；10-contact、Alarm、Marker 另有 approved PNG。
- [x] Output：MP4、Heatmap、Reported Points、Data Package 均有 1920／1180 dark/light approved；loading/progress/cancel 由行為測試覆蓋。
- [ ] Raw／Decoded：Raw/Register 1920 dark 已 approved；1180 且兩側 rail 同時展開仍未建立 approved PNG。
- [x] Inspector：320／380／520 bounds tests，Protocol／Raw／Review 與 Source identity interaction tests；Alarm、10-contact、Raw/Register advanced PNG 已 approved。
- [ ] Settings：1920／1180 dark/light 已 approved；125% UI scale 未實作。
- [x] CI/test gate 對 approved baseline 執行 exact PNG compare；失敗時產生 actual/diff/metrics，candidate promotion 必須明確執行。

完成條件：`UI_TODO.md` 的視覺與效能 completion gate 全數通過，approved artifacts 與目前 commit 一致。

## 0.0.3 不包含

- raw waveform decoder 正式實作
- semantic kernel log parser
- growing file、socket 或 hardware live capture
- arbitrary third-party DLL plugin
- multi-capture synchronized comparison
- 完整 custom register profile import UI
- HTML/PDF report designer

以上能力只保留 interface、schema 與資料邊界，不在本次重構中同時實作，以避免重構與新功能交叉擴張。

## 建議 commit 邊界

1. `docs: plan 0.0.3 stabilization refactor`
2. `chore: align 0.0.3 release identity`
3. `refactor: introduce cancellable replay workspace builder`
4. `perf: build replay indexes in a single background pass`
5. `perf: make replay frame plans compact and searchable`
6. `refactor: extract playback controller`
7. `refactor: extract output workspace and export job service`
8. `refactor: extract paint and review inspector workspaces`
9. `refactor: share format decode pipeline across UI and CLI`
10. `refactor: project register annotations without copying source records`
11. `ui: extract shared design resources and accessibility scale`
12. `test: enforce approved visual and performance contracts`
13. `docs: close 0.0.3 gates and prepare release`

每個 commit 必須可 build、可 test、可回退；不得將純重構、視覺變更與 parser semantics 混在同一個 commit。

## Release gate

- [x] `dotnet build -c Release --no-restore`：0 warnings / 0 errors（隔離輸出，不關閉使用者已開啟的 App）。
- [x] Core / parser / rendering / CLI 316/316；Avalonia 111/111。
- [x] Public/synthetic Common 0x82–0x85 與 Desay 0x97 Standard/Benz Palm contract/golden 無未核准差異。
- [ ] 私有 Desay Standard/Benz Palm golden 尚未提供；正式 release 前仍需補一份可驗證證據。
- [x] 私有 KingstVIS、DSL、NDS smoke 通過，來源檔不進 git；結果記錄於 `golden/README.md`。
- [x] 100k point、長 recorded gap、180/240 FPS、cancel/race、21,600 advances（等效 3-minute 120 Hz Loop）與 Smoke performance gate 通過。
- [ ] 28 張 approved screenshot dark/light/narrow 全部通過；125% UI scale 與 Output loading/cancel 專屬 PNG 尚未核准。
- [x] 工作樹在驗收前 clean，無 firmware BIN、private golden、secret；驗收暫存會在 docs commit 前精確清除。
- [ ] `VERSION=0.0.3`、package/manifest identity 已一致；正式 `v0.0.3` tag 與 GitHub release 尚未建立。
