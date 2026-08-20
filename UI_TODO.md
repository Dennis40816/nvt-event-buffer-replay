# NVT Event Buffer Replay UI TODO

更新日期：2026-08-20

> 0.0.3 的跨模組穩定化、效能、MainWindow 拆分與 release gate 請以 [`TODO_0.0.3.md`](TODO_0.0.3.md) 為主；本文件繼續追蹤 UI 細節與視覺驗收。

本文件追蹤目前 Paint、Output、Settings 與 replay transport 的近期工作。`ROADMAP.md` 保留產品長期方向；這裡只放可直接實作、測試與視覺驗收的項目。

## 本輪需求

| ID | 項目 | 狀態 | 完成條件 |
|---|---|---|---|
| UI-01 | Contact label 回到 baseline 的近點配置，不再沿長軌跡漂到遠端 | 已實作；長 golden dark 實機通過 | 長 golden 中 label 保持靠近 contact；碰撞時不遮點、不互相重疊；沒有跨畫布瞬移感 |
| UI-02 | `Clear trail` 移出 Canvas Settings，改為預設開啟的 `Trace` | 已實作；長 golden dark 實機通過 | Paint 浮動快捷列可立即開關軌跡；關閉只影響顯示，不改動來源或歷史資料 |
| UI-03 | 修正 Save 按鈕不一致的 hover/focus 顏色 | 已完成；dark/light/narrow approved | Save/Review/Export header actions 使用同一無框樣式，沒有灰色殘留框 |
| UI-04 | 只有 Paint 顯示下方 replay transport | 已實作；測試與 Output 實機通過 | Raw Explorer、Decoded Events、Output、Settings 均不保留底部進度列；回 Paint 後立即恢復 |
| UI-05 | Mark 恢復單 frame 語義並在 timeline 顯示旗標 | 已實作；測試與長 golden 實機通過 | 每次 Mark 只標目前 frame；旗標位置正確；rename、clear、unmark 與重疊 marker 選單正常 |
| UI-06 | 調查並降低 Paint 播放卡頓 | 已完成 synthetic sustained-loop gate | 軌跡切片不再逐 frame 掃描／複製完整 gesture；完整線段改用 2,048 點無損 chunk 與 Skia 批次快取；21,600 advances（120 Hz 3-minute equivalent）第二輪 allocation 不成長 |
| UI-07 | ComboBox 關閉後移除 focus 殘留外框與顏色 | 已完成；互動測試通過 | 選取或取消選單後焦點回 workspace；不保留 lime/灰色框；鍵盤快捷鍵可立即恢復 |
| UI-08 | 選單未變更時不重新 decode/render/export preview | 已完成；revision/build counter tests 通過 | Event Buffer version、Paint mode、trail、panel resolution、Output video 與 Heatmap 設定值不變時不觸發昂貴刷新 |
| UI-09 | Settings 與主頁視覺一致，左側導覽可點擊 | 已實作；測試與導覽實機通過 | 五個導覽項可捲到對應 section；active/hover 樣式使用主題 token；設定列不再像另一套 UI |
| UI-10 | Heatmap 可隨時切回 MP4 / Data package | 已實作；測試與 Heatmap→MP4 實機通過 | Output content selector 永遠位於共用頂列，不隨內容 panel 隱藏 |
| UI-11 | Paint 直接提供 Break / All Break 暫停選項 | 已實作；測試與面板實機通過 | transport 的 Auto-pause 面板可切 Alarm/QA、reported Break、decoded All Break；與 Settings 共用同一狀態 |
| UI-12 | 修正 Unmark ContextMenu 關閉後的透明 overlay 攔截 | 已實作，測試通過 | menu 一律先 Close 再解除；刪除 marker 後 timeline 空白區仍可 seek，右鍵不留下 stale menu |
| UI-13 | 修正啟動時假滿版與底部 transport 落到螢幕外 | 已修正；冷啟動實機通過 | 不再用 `MaxHeight` 手動扣除 non-client 高度；由 Windows working area 管理 Maximized，視窗左上角與工作區對齊，Paint transport 完整可見 |
| UI-14 | 逐張與核准參考圖重新比對所有細小差異 | 已完成本版範圍；20 張 exact approved | Paint、Output、Settings、Heatmap 與 advanced Review/Inspector 逐張人工核對；Alarm、10-contact、Raw/Register、Marker 另有 focused baseline |
| UI-15 | 軌跡線與真實 report points 分層顯示 | 已實作；長 golden dark 實機通過 | `Trace` 顯示完整、未抽樣的背景線；`Points` 預設開啟並顯示每一筆 report point；兩者可獨立切換且不重新 decode，Paint／MP4 語義一致 |
| UI-16 | 十萬點無損軌跡與快速 frame 切換 | 已實作；synthetic gate 與長 golden 通過 | 100,003 點拆成 49 個 chunk；完整 chunk 快取、每次 seek 最多重建 2,047 點尾端；250 次亂序 frame 切換不重建完整歷史；120 Hz 長 golden 播放可到尾端 |
| UI-17 | Contact label 穩定定位 | 已完成；Paint/MP4 共用 layout tests | 初始方向由 contact 相對群組中心與畫布可用空間決定，ID 不帶方位語意；40 px leader 距離；互動播放保留上一個有效 anchor，只在碰撞或越界時換位 |
| UI-18 | Heatmap 與 MP4 使用相同 Output 左預覽／右設定骨架 | 已完成；1920/1180 dark/light approved | 切換內容只替換左側 preview renderer；Heatmap 不再橫跨整頁，右側設定 rail 與 MP4 維持相同層級與 padding |
| UI-19 | Output type 移入右側設定動線 | 已完成；切換與 stale-state tests 通過 | 輸出類型固定在右欄頂端；左側只保留動態說明、preview 與 export action，操作順序為先選類型再調整該類型參數 |
| UI-20 | 重排並整合 Inspector 的 Protocol／Raw／Review 資訊架構 | 待規劃 | 消除跨分頁重複欄位；固定 health、contact、transport、byte layout、source identity 與 review actions 的層級；320／380／520 px 均可讀，且 Raw/Register 與 decoded frame projector 不互相覆蓋 |

## 參考圖逐張對照

以下對照不是「看起來接近」即可；每輪都要把成品截圖與對應聊天附件並排檢查。

| 參考附件 | 對照重點 |
|---|---|
| `codex-clipboard-78e6c637-638f-4f33-a523-2ba11aebb1f2.png` | Paint 畫布比例、label 與 contact 距離、軌跡密度、浮動快捷列位置 |
| `codex-clipboard-817dda79-6d81-4ea7-afd1-4faeeddc98a4.png` | Trace 與 Canvas Settings 的分工、浮動面板尺寸與間距 |
| `codex-clipboard-cbeb278b-e348-494c-abb7-69db46034ba4.png` | Output 右側設定列 hierarchy、row 高度、focus 狀態與 Export action |
| `codex-clipboard-7f24a342-f0e7-4a4e-8ab2-67ec84b926dd.png` | Header source/version/profile 的 inline hierarchy 與 Edit action |
| `codex-clipboard-641cb667-9b89-4077-9591-0daf0c005a83.png` | Settings navigation、section spacing、選取狀態與主頁 token 一致性 |
| `codex-clipboard-2d30da53-959a-411e-b10d-5dd8bf68b2ab.png` | Heatmap 色階、color bar、數值門檻、plot padding 與底部說明 |
| `codex-clipboard-f7aaca5c-1afa-46f3-9a18-500d6449996d.png` | 已核准的 Paint baseline：近點雙行 label、軌跡與畫布整體密度 |

## 視覺 completion gate

- [ ] 私有 KingstVIS 長 golden 已完成 CLI smoke 與人工播放；正式 approved Paint PNG 使用可公開、可重現 fixture，未把私有 capture 帶入 repo。
- [x] 驗證冷啟動後視窗使用 Windows working area，沒有右下偏移、超出螢幕或底部 transport 裁切。
- [x] Paint 1920×1080 與最小支援寬度 1180×720 dark/light approved；1672×720 由 responsive bounds tests 覆蓋。
- [ ] Output MP4、Heatmap、Settings 的 1920/1180 dark/light 已 approved，且非 Paint 頁面沒有 transport；Data package 專屬 PNG 尚未核准。
- [ ] 逐張並排比對上表參考圖：控制項位置／尺寸誤差目標不超過 4 px，字級誤差不超過 1 px，顏色必須使用既有 design token。
- [x] ComboBox closed、hover、keyboard focus、mouse focus paths 有 style/interaction tests；關閉後不殘留強調框。
- [x] 1、5、10 contacts 與 Finger、Glove、Palm 有 presenter/layout tests；10-contact narrow approved 顯示 ID10 selection 與三種類型統計。
- [x] Mark 旗標、Loop bracket、Break/All Break auto-pause panel 與 Unmark menu 有實際 interaction tests。
- [x] Heatmap／MP4／Data package 可往返切換，content selector 不隨內容 panel 消失；stale background result 不回寫。

### 細小差異回歸清單

- [x] Header：Save、Review、Output、Settings 的 icon 光學尺寸、基線、間距與無框狀態一致；1180 不重疊或出現灰塊。
- [x] Paint toolbar：Panel、Point view、Trail、Length 等 row 高度與文字垂直置中一致；選單關閉後不保留亮框。
- [x] Paint canvas：label 維持近點雙行格式與穩定 anchor；1／5／10 點有碰撞、bounds 與 selection tests。
- [x] Floating tools：Trace、Grid、Flip X、Flip Y、Swap XY、Fit、Settings 對齊同一基線，不使用多餘的大外框。
- [x] Trail evidence：Trace line 與 Points 都保留 retention 範圍內的完整 report 序列；切片僅是無損繪圖批次，不刪除線段或報點。Lines only、Points only、Both、Neither 均有測試，並以長 golden 實機驗收前三種可視狀態。
- [x] Transport：Play、clock、speed、Loop、Mark、Auto-pause、marker 旗標、Loop bracket 與時間文字具有 bounds/click/keyboard tests；1180 chrome approved。
- [x] Inspector：Frame／time／contact／CRC／ASIL hierarchy 在 320、380、520 px 有 bounds tests；selected contact 與畫布 highlight 同色。
- [x] Output：共用 content selector、MP4 preview controls、右側設定 rail、Heatmap color bar 與輸出 action spacing 一致；頁面不出現 Paint transport。
- [x] Settings：左側 active item 與五個 section 使用主頁 tokens；1920/1180 dark/light approved。
- [x] Light mode：Paint、Heatmap、Settings 與 Alarm Inspector 有獨立 approved baseline，不直接沿用 dark mode 判定。

## 效能 completion gate

- [ ] 最新 KingstVIS `063ad09…` 已重新驗證 568 frames 並人工播放；所有速度組合目前由 deterministic playback tests/21,600-advance gate覆蓋，尚未逐一保存私有 capture 的實時量測 artifact。
- [x] 播放中連續切換速度與 Loop 有 rapid-change/latest-generation tests，不產生多個 playback loop或錯亂 Play/Pause state。
- [x] UI thread 不逐 frame 重建 Raw/Decoded list；Inspector details 節流時 headless current frame/copy/contact selection 仍同步。
- [x] 21,600 advances（120 Hz 3-minute equivalent）第二輪 allocation 不成長；100,003-point history與 geometry revision 不重建。
- [x] CLI Smoke performance 結果保留於 ignored artifacts 並摘要於本文件；包含 OS/.NET/CPU、source size、frame count、working set、load/seek/render metrics。

## 目前驗證紀錄

- Avalonia：94 / 94 passed；20 張 approved PNG exact compare 通過。
- Core / parser / rendering / CLI：289 / 289 passed。
- 私有 Golden smoke：KingstVIS `063ad09…` 568 frames、DSL `e7e277…` 435 frames、NDS `0e32907…` 518 frames；來源檔均未進 git。
- 100,003-point trail history 建立 allocation 降低 93.7%；250 次 random seek Build allocation 降低 69.3%。這是資料／幾何層量測，不等同桌面 Paint 的實際呈現 FPS。
- 2026-08-20 Smoke performance：16.0 MiB source load 233.5 ms／52.2 MiB peak；10,000 physical records load 340.7 ms／78.1 MiB peak；8-hour、1,000-frame timeline load 119.5 ms、2,000 seeks 0.074 ms、60 rendered frames 105.1 ms（570.9 FPS）、55.4 MiB peak，gate pass。
