[English](README.md) | [繁體中文](README.zh-TW.md)

# NVT Event Buffer Replay

以 C# 與 Avalonia 開發的離線工作站，可按照 vehicle head unit 使用 Novatek
touch-controller 擷取資料的方式進行 replay、檢視 protocol 與 ASIL 證據、
標記重要範圍，並匯出可重現的結果。

此儲存庫是正式產品的實作。同層的 Python 專案 `nvt_event_buf_parser`
仍作為參考 decoder、simulator 與 golden generator；桌面應用程式在
runtime 不需要 Python。

## 狀態

Milestone 1 至 5 已實作完成。Milestone 6 的內建部分也已完成；自訂
register-profile 匯入仍須取得證據才能開放。已確立的產品範圍記載於
[產品規格](docs/product-spec.md)與 [roadmap](ROADMAP.md)。

第一個 vertical slice 包含：

- streaming NDS、Saleae、KingstVIS、DSL、Acute、Excel 與 canonical I2C adapters，
  具備穩定的來源位置與 transport provenance；
- 可辨識 register 的通訊列，具備依 profile 區分的 IC maps、已確認的 FW
  command/reset 意義，以及未知值的 raw-only fallback；
- 明確列出的 format registry，涵蓋 Common `0x82`–`0x85` 與 Desay `0x97`；
- source detection 絕不默默選定 Event Buffer Version 或 Benz Palm；
- CLI discovery 與 probe 命令，可選擇輸出 JSON；以及
- 以 Paint、Review Queue、Inspector 與 physical/logical/evidence timeline
  為中心的 Avalonia shell。

桌面應用程式目前會在背景載入支援的擷取資料並建立索引，在 semantic
configuration 尚未完成前先開啟 Raw Explorer，並在操作人員切換 Common
`0x82`–`0x85` 時，將已建立索引的 records 保留在記憶體中。沒有獨立的
Decode 動作：確認 Common 版本後立即開始 decoding，而 Desay `0x97`
會等到 IC 與 Palm profiles 都明確指定後才開始。選取 decoded frame
會在 Inspector 中同步其 physical record、來源位置、stable ID、raw
evidence 與 decoded fields。

Decoded-I2C adapters 會將 LA 匯出資料正規化為相同的 physical record model。
Page/write tracking 會將 `FF 09 90 00` 這類 read 解析至 `0x99000`，而
Raw Explorer 會保留 slave、commands、ACK/NAK 資料、來源特有欄位與
diagnostics。C# simulator 會產生等效的 Saleae、KingstVIS、DSL、Acute、
Excel 與 canonical TXT fixtures。Acute 目前遵循保守的英文
row-per-transaction report contract；continuation rows 仍須取得證據
才能支援。請參閱 [source adapters](docs/source-adapters.md)。

目前可透過 NDS inspection slice 進行 Common `0x82`–`0x85` decoding。
共用的 finger semantics 只實作一次；各版本特有的 tails、CRC evidence、
ASIL transitions、All Break/Break、bus counters、EMS bitmap 與全域
Palm diagnostics 仍可追溯至 source record。請參閱
[Common Event Buffer contract](docs/common-event-buffer.md)。

Desay `0x97` 在 decoding 前使用專用的 two-transaction assembler，並
要求明確指定 Standard 或 Benz Palm profile。目前的 offset `0x00`
full-reread contract 與其剩餘的 Benz evidence gate 記載於
[Desay Event Buffer contract](docs/desay97-event-buffer.md)。

Common replay slice 會將每個 logical frame 歸約為獨立的 Reported Frame
與 Host State 視圖。Paint 支援 deterministic 向前／向後 seek、sparse
checkpoints、Recorded 與 synthetic Frame clocks、0.01×–10× 及 MAX
播放、idle-gap compression，以及可拖曳的雙控制柄 loop range。
播放時變更 clock 或速度會立即重新排程；每個 UI tick 的 catch-up
工作量有上限，且會拒絕僅有一個 frame 的 Loop，以免形成 busy loop。
播放依 absolute clock 排程：忙碌的 UI 可略過已過時的中間繪製，以維持
時間同步，但絕不略過 Alarm 或 QA 暫停。
Loop 回到起點時會使用短暫的 crossfade，而非直接切換畫面。
每個 contact 的顏色在點、座標標籤與 trajectory 上保持一致。Trajectory
retention 可顯示最近 2–120 個 frame 的範圍、將每個 gesture 保留至其
Break，或在整個 session 中保留已完成的 gestures；Clear 只影響視圖，
絕不變更擷取證據。標籤採用依點位置調整的配置、兩行座標、leader lines
與 Finger/Glove/Palm/Reserved glyphs；canvas 包含對應的 ID/type legend
與可調整的座標網格。獨立的 Flip X、Flip Y 與 Swap XY 控制項只轉換
Paint 與 video-export 視圖；decoded 與 raw source 座標維持不變。
Legend 會對完整擷取資料評分一次，選擇佔用最少的角落，在播放期間保持
固定，並支援手動固定角落、直接切換 compact/expanded 模式，以及用 `L`
顯示／隱藏。
無效的 frames 仍作為證據顯示，且不變更 Host State；若擷取資料結束時
仍有 active contacts，會提出警告，而不會憑空產生 All Break。

Review Queue 將重複出現的 diagnostics 分組，且保留每次 occurrence；
它將擷取到的 ASIL Assert/Clear 與人工 acknowledgement、disposition
分開處理，並預設在 Alarm 或 QA Fail 時暫停 replay。選取 occurrence
會同步 Paint、decoded fields、transport、raw evidence 與來源位置。
請參閱 [Review Queue 與 ASIL lifecycle](docs/review-queue.md)。

Markers、ranges、QA case references、raw Kernel/FW log attachments、
review state 與 visibility preferences 可透過具版本的 `.nvtreplay.json`
sidecar 儲存與載回，不會修改擷取資料。儲存採 atomic 方式；擷取資料／
configuration 不相符時，必須由操作人員明確確認，且遺失或變更的證據
仍會顯示。請參閱 [replay review sidecars](docs/replay-sidecar.md)。

Analysis workspace 與 `nvt-replay analyze` 會匯出相同且可重現的
anomaly/ASIL aggregates、已解析的 event 與 diagnostic JSON/CSV、
source/config manifest，以及 deterministic Reported Frame heatmap PNG。
每個 aggregate 都連結回 diagnostic、event 與 physical source IDs。
請參閱 [analysis outputs](docs/analysis-outputs.md)。

Avalonia Paint 與 deterministic headless export 目前共用同一個
`ReplayScene`。桌面 MP4 預覽也使用完全相同的 export frame plan 與
1280×720 raster；只有螢幕上的呈現會縮放。Preview RGB/RGBA buffers
與 Avalonia bitmap 會在 frames 之間重複使用，以避免播放期間的 GC churn。
`nvt-replay export` 將選定範圍 render 為 raw RGB，並透過 pipe 傳送至
操作人員已審查的 FFmpeg executable。Windows release packages 包含
固定版本且經 hash 驗證的 LGPL FFmpeg runtime；encoder 遺失或不相容
時會回報錯誤，絕不默默將 MP4 輸出改成其他格式。請參閱
[replay video export](docs/video-export.md)。

可重複執行的 performance gates 涵蓋 1 GiB 輸入、一百萬筆 physical
records、八小時的 timeline、sparse seek checkpoints，以及 60 FPS
Paint rendering。請參閱 [performance 與 scale gates](docs/performance.md)。

桌面 UI 包含 keyboard-first command palette、明確的 automation names、
文字形式的 severity cues、精簡的 34–36 px 控制項，以及已審查的
dark/light palettes。以 Skia 為基礎的 Avalonia headless suite 會在 CI
中驗證 auto-decode、layout、rail defaults 與兩種 rendered themes。
請參閱 [UI 互動與 accessibility](docs/ui-accessibility.md)。

支援的輸入，以及刻意保留至取得證據後才進行的 post-MVP 工作，彙整於
[MVP 證據範圍與延後項目](docs/mvp-limitations.md)。

## 建置

需求：.NET SDK 10.0.303 或相容的較新 .NET 10 feature band，以及 Python 3.10 或更新版本。
第一個命令會從 GitHub Release 下載 Nvt.Core package 並檢查其 hash。

```powershell
python -B scripts/fetch_core_packages.py
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project src/Nvt.Replay.Cli -- formats
dotnet run --project src/Nvt.Replay.Cli -- probe ./capture.txt --json
dotnet run --project src/Nvt.Replay.Cli -- inspect ./capture.txt --event-buffer-version 0x83 --i2c-address 0x01
dotnet run --project src/Nvt.Replay.Cli -- analyze ./capture.txt --event-buffer-version 0x83 --output ./analysis
dotnet run --project src/Nvt.Replay.Cli -- export ./capture.txt --event-buffer-version 0x83 --output ./replay.mp4
dotnet run --project src/Nvt.Replay.Avalonia
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x83
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x97 --register-profile 51927 --palm-profile Benz-Palm
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x83 --register-profile 51927
dotnet run --project src/Nvt.Replay.Cli -- readable ./capture.txt --output ./analysis --register-profile 51927
```

桌面 startup options 與 IC register profiles 由操作人員明確選擇，以便
進行可重現的 QA 與 screenshot runs。它們不會推斷 Event Buffer Version
或 Benz Palm。桌面 header 與 CLI `--i2c-address` 選項會選定 IC inference
與 Event Buffer decoding 使用的 7-bit slave（預設為 `0x01`）。
Raw Explorer 仍會保留每個擷取到的裝置，並分別顯示 7-bit address、W/R
方向、已解析的 register address 與原始 register byte。

Windows preview 與 stable packaging 使用固定版本、已提交的 dependency
locks、封閉的 payload allowlist、SHA-256 manifests，以及重新解壓後的
smoke checks。請參閱 [release process](docs/release.md)。

不得提交私人擷取資料、firmware、QA records 或 golden payloads。僅提交
synthetic fixtures、schemas、hashes、provenance 與已審查的觀察結果。

## 授權

Copyright (c) 2026 Dennis Liu. All rights reserved.

本軟體為 proprietary software。未經 Dennis Liu 事先書面授權，不授予
使用、複製、修改或散布本軟體的權限。請參閱 [LICENSE](LICENSE)。
第三方元件各自保留其授權條款。
