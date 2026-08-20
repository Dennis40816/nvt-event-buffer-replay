# NVT Event Buffer Replay 0.0.3 重構 TODO

更新日期：2026-08-20

0.0.3 定位為穩定化與模組化版本。本版本先降低 UI freeze、取消失效、狀態互相干擾與視覺回歸風險，再為 custom register profile、raw waveform、kernel log 與 live input 建立可持續擴充的落點。

> 核准門檻：本文件先供 owner review。未取得確認前，不修改 production code、release tag 或既有 `0.0.2` 歷史。

## Baseline

- Branch：`0.0.3`
- 參考 commit / tag：`1e2567b` / `0.0.2`
- Production handwritten C# / XAML：約 18,662 行，仍在 18,000–22,000 目標區間內。
- Core / parser / rendering tests：168 / 168 passed。
- Avalonia tests：47 / 47 passed。
- 私有長 KingstVIS golden：4,160 physical records / 2,080 logical frames。
- 已知 release identity 不一致：git tag `0.0.2`、branch `0.0.3`，但 `VERSION` 為 `0.1.0`。
- `MainWindow.axaml.cs` 約 3,874 行、`MainWindow.axaml` 約 1,910 行；兩者合計約占 production 31%。

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

- [ ] 將 `VERSION`、正式 tag、package name、assembly metadata 與 manifest 的版本策略統一。
- [ ] 0.0.3 使用 `VERSION=0.0.3`；正式 tag 固定為 `vX.Y.Z`。
- [ ] 不重寫已推送的 `0.0.2` tag；在 release note 標示它是 milestone tag。
- [ ] CI 新增 release identity gate：tag、`VERSION`、package metadata 不一致即失敗。
- [ ] 固定 baseline test 數、長 golden hash/records/frames、效能機器與 commit。
- [ ] 重生目前 source 對應的 Paint、Output、Heatmap、Settings approved screenshots，淘汰過期 artifact。

完成條件：從任一正式 tag 產出的檔名、manifest、版本文字與 GitHub Release 身分完全一致。

## Phase 1 — 可取消的 ReplayWorkspaceBuilder

- [ ] 新增 `ReplayWorkspaceBuildRequest`、`ReplayWorkspaceBuildResult` 與 `ReplayWorkspaceBuilder`。
- [ ] 將 Common decode、Desay assemble/decode、replay materialization、extent、trail index、auto-pause index 移出 UI thread。
- [ ] `CancellationToken` 傳入 `CaptureSession.DecodeCommon`、`DecodeDesay97`、`Desay97Assembler` 與所有長迴圈。
- [ ] 加入 operation generation；只有最後一個仍有效的 operation 可以提交 UI state。
- [ ] 完整 workspace 建立成功後一次性切換；取消或失敗時保留上一份完整結果。
- [ ] 進度階段至少區分 `Decode`、`Replay index`、`Trail index`、`Review index`、`Ready`。

完成條件：載入／解碼期間 UI 可操作 Cancel；取消後無背景 CPU 長跑、無 stale continuation、raw explorer 與上一份有效結果仍可用。

## Phase 2 — Replay index 與長軌跡效能

- [ ] Replay snapshots、extent、trail history 與 auto-pause index 改為單次線性掃描建置。
- [ ] 十點 contact presence 優先使用 `ushort` bitmask，避免每 frame 建立多個 `HashSet<byte>`。
- [ ] Host state 評估改為固定 11-slot array/struct，避免每 frame 複製 Dictionary。
- [ ] Checkpoint 位置改用直接索引或 binary search，移除 `SortedDictionary.Last(...)` 熱路徑。
- [ ] 保留 2,048-point 無損 trail chunk 與完成 chunk cache；切片只影響繪圖批次，不刪除線或 report points。
- [ ] 量測 allocation、GC、workspace build time、random seek 與 3-minute Loop memory curve。

完成條件：

- 100,003 report points 完整保留。
- 250 次亂序 seek 不重建完整軌跡。
- 私有 2,080-frame golden 在 0.01×、0.1×、1×、10×、MAX 與 Loop 下無 freeze。
- 3 分鐘 Loop 後記憶體不持續線性成長，速度／Loop／Mark 操作回應目標小於 100 ms。

## Phase 3 — MP4 Preview / Export frame plan

- [ ] `ReplayFramePlan` 直接建立 RLE repeat count，不逐一 materialize 每個 output frame。
- [ ] 保存 cumulative output end index，以 binary search 完成 output-frame -> logical-frame 對應。
- [ ] frame count、repeat count 與 duration calculation 使用 `long` 並加入 overflow / hard-limit protection。
- [ ] 先以常數時間 estimate 顯示長影片警告，再於背景建立完整 plan。
- [ ] planning、rendering、FFmpeg piping 全程共用 cancellation 與 progress。
- [ ] Preview scrub、repeat、fullscreen 與 final MP4 共用同一份 immutable plan。
- [ ] 統一 UI 與 CLI 的 FPS 上限；180 FPS 必須在兩邊具有相同結果與驗證。

完成條件：Recorded timing 包含長 idle gap 時不阻塞 UI；取消後不留下 partial output，Preview 與 final manifest 的 frame count、duration、range 完全相同。

## Phase 4 — MainWindow 漸進式拆分

依下列順序進行，每一步保持既有 UI 與測試行為：

- [ ] 抽出 `PlaybackController`：clock、speed、play/pause、step、Loop、auto-pause、generation。
- [ ] 抽出 `OutputWorkspaceViewModel` 與 `ExportJobService`：content type、video options、preview、progress、cancel。
- [ ] 抽出 `PaintWorkspaceViewModel`：panel extent、point view、trail、Trace/Points、axis transform、canvas safe area。
- [ ] 抽出 `ReviewInspectorViewModel`：current frame、contact selection、finding navigation、marker lifecycle。
- [ ] 抽出 `CaptureDecodeController`：source choice、version/profile validation、automatic decode、register annotation。
- [ ] 將 Paint、Output、Review、Inspector 拆為獨立 View/UserControl。
- [ ] 將 window-local styles 拆入 typography、buttons、fields、transport、inspector、output 等 ResourceDictionary。
- [ ] 移除拆分後的 hidden legacy controls、重複 state 與不再使用的 event handlers。

完成條件：`MainWindow` 不再直接包含 replay/export/decode 演算法；ViewModel/controller 可在無 Window 的情況下測試。結構抽離前後 approved screenshot 應維持零預期差異。

## Phase 5 — 共用 format / application pipeline

- [ ] 將目前 descriptor-only format registry 升級為 UI/CLI 共用的 executable provider。
- [ ] provider 封裝 descriptor、configuration validation、decode、replay projection 與 inspector/raw presentation。
- [ ] 移除 UI 與 CLI 中 Common／Desay 重複 switch 與近似 command pipeline。
- [ ] Desay two-transaction 保留獨立 assembler；補 cancellation 與 transport-stream/slave scope。
- [ ] CRC ownership 明確化，避免 assembler 與 decoder 重複執行同一個 validation。
- [ ] 新 LA adapter 套用共用 acceptance contract：probe ambiguity、malformed row、cancellation、stable ID、address normalization、ACK unavailable、transaction boundary、simulator round trip、real-export provenance。

完成條件：新增第三種 Event Buffer family 時不需要同時修改 MainWindow、CLI 三條 command flow 與 Rendering switch。

## Phase 6 — Register annotation projection

- [ ] `SourceRecord` 只保存 transport normalization 與原始 provenance，不因 IC profile 切換而複製整份 capture。
- [ ] 新增 `RegisterAnnotationIndex`，以 record stable ID 與 profile identity/hash 建立 projection。
- [ ] register diagnostics 與 transport/parser diagnostics 分離。
- [ ] profile 切換只重建 annotation projection 與其專屬 findings。
- [ ] 為下一版 custom register profile JSON 預留 schema version、profile ID、IC/FW scope 與 SHA-256 identity。

完成條件：切換 IC profile 不再 O(N) 複製所有 `SourceRecord`；同名但內容不同的 profile 不會被視為同一份設定。

## Phase 7 — Design system 與視覺驗收

- [ ] 建立正式 typography：12.5 / 14 / 16 / 20；移除 9.5 / 10.5 的關鍵資訊文字。
- [ ] 建立 spacing：4 / 8 / 12 / 16 / 24；control height：30 compact / 36 default / 40 accessible。
- [ ] Accent、Success、Severity、Contact ID、Heat density 使用獨立色彩角色。
- [ ] 所有 Button、Toggle、ComboBox 具有 rest、hover、pressed、checked、focus、disabled 非純色彩差異。
- [ ] Header 以 capture filename 為主要 identity；adapter、format、version、IC profile 為次要 metadata。
- [ ] floating toolbar/settings bounds 納入 label placement 與 Fit 的 canvas safe area。
- [ ] Inspector 恢復 320–520 px contract，label/value layout 自適應。
- [ ] MP4、Heatmap、Data Package 維持相同 7:3 左內容／右設定骨架。
- [ ] Transport auto-pause 改 anchored Flyout；空間不足時將低頻項目放入 overflow，不使用固定 margin 或裁切。
- [ ] 增加 Comfortable / Compact 或 100% / 110% / 125% UI scale 設計與驗證。

### Approved screenshot matrix

- [ ] Paint：1920×1080 dark/light、1672×720、1180×720。
- [ ] Paint states：1／5／10 contacts、Finger／Glove／Palm、Alarm、All Break、Mark、Loop、auto-pause。
- [ ] Output：MP4、Heatmap、Data Package，各自 1920／1180，並包含互動與 loading/cancel 狀態。
- [ ] Raw／Decoded：1180 且 Review/Inspector rail 展開，確認 table 不裁切。
- [ ] Inspector：320／380／520，Protocol／Raw／Review 與 Source identity。
- [ ] Settings：dark/light、五個 navigation section、125% UI scale。
- [ ] CI 對 approved baseline 執行 pixel 或 perceptual diff，產生可人工 review 的差異圖；不再只輸出同次執行 hash。

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

- [ ] `dotnet build -c Release --no-restore` 無 warning/error。
- [ ] Core / parser / rendering、Avalonia、CLI contract tests 全部通過。
- [ ] Common 0x82–0x85 與 Desay 0x97 Standard/Benz Palm golden 結果沒有未核准差異。
- [ ] 私有 KingstVIS、DSL、NDS 長 golden smoke 通過，來源檔不進 git。
- [ ] 100k frame/point、長 recorded gap、180 FPS export、cancel/race、3-minute Loop gates 通過。
- [ ] Approved screenshot matrix dark/light/narrow/accessibility 全部通過。
- [ ] 工作樹 clean，無 firmware BIN、private golden、secret 或大型可重建 artifact。
- [ ] `VERSION=0.0.3`，正式 tag `v0.0.3`，package/manifest/release identity 一致。

