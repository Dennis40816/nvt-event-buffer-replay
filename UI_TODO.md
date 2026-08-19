# NVT Event Buffer Replay UI TODO

更新日期：2026-08-19

本文件追蹤目前 Paint、Output、Settings 與 replay transport 的近期工作。`ROADMAP.md` 保留產品長期方向；這裡只放可直接實作、測試與視覺驗收的項目。

## 本輪需求

| ID | 項目 | 狀態 | 完成條件 |
|---|---|---|---|
| UI-01 | Contact label 回到 baseline 的近點配置，不再沿長軌跡漂到遠端 | 已實作；長 golden dark 實機通過 | 長 golden 中 label 保持靠近 contact；碰撞時不遮點、不互相重疊；沒有跨畫布瞬移感 |
| UI-02 | `Clear trail` 移出 Canvas Settings，改為預設開啟的 `Trace` | 已實作；長 golden dark 實機通過 | Paint 浮動快捷列可立即開關軌跡；關閉只影響顯示，不改動來源或歷史資料 |
| UI-03 | 修正 Save 按鈕不一致的 hover/focus 顏色 | 已實作，待視覺驗收 | Save/Review/Export header actions 使用同一無框樣式，沒有灰色殘留框 |
| UI-04 | 只有 Paint 顯示下方 replay transport | 已實作；測試與 Output 實機通過 | Raw Explorer、Decoded Events、Output、Settings 均不保留底部進度列；回 Paint 後立即恢復 |
| UI-05 | Mark 恢復單 frame 語義並在 timeline 顯示旗標 | 已實作；測試與長 golden 實機通過 | 每次 Mark 只標目前 frame；旗標位置正確；rename、clear、unmark 與重疊 marker 選單正常 |
| UI-06 | 調查並降低 Paint 播放卡頓 | 第二階段已實作；待 3 分鐘 Loop 壓測 | 軌跡切片不再逐 frame 掃描／複製完整 gesture；完整線段改用 2,048 點無損 chunk 與 Skia 批次快取；timeline drag 採 latest-frame-wins；MAX、慢放與 Loop 切換不凍結 UI |
| UI-07 | ComboBox 關閉後移除 focus 殘留外框與顏色 | 已實作，待互動驗收 | 選取或取消選單後焦點回 workspace；不保留 lime/灰色框；鍵盤快捷鍵可立即恢復 |
| UI-08 | 選單未變更時不重新 decode/render/export preview | 已實作，待計數驗證 | Event Buffer version、Paint mode、trail、panel resolution、Output video 與 Heatmap 設定值不變時不觸發昂貴刷新 |
| UI-09 | Settings 與主頁視覺一致，左側導覽可點擊 | 已實作；測試與導覽實機通過 | 五個導覽項可捲到對應 section；active/hover 樣式使用主題 token；設定列不再像另一套 UI |
| UI-10 | Heatmap 可隨時切回 MP4 / Data package | 已實作；測試與 Heatmap→MP4 實機通過 | Output content selector 永遠位於共用頂列，不隨內容 panel 隱藏 |
| UI-11 | Paint 直接提供 Break / All Break 暫停選項 | 已實作；測試與面板實機通過 | transport 的 Auto-pause 面板可切 Alarm/QA、reported Break、decoded All Break；與 Settings 共用同一狀態 |
| UI-12 | 修正 Unmark ContextMenu 關閉後的透明 overlay 攔截 | 已實作，測試通過 | menu 一律先 Close 再解除；刪除 marker 後 timeline 空白區仍可 seek，右鍵不留下 stale menu |
| UI-13 | 修正啟動時假滿版與底部 transport 落到螢幕外 | 已修正；冷啟動實機通過 | 不再用 `MaxHeight` 手動扣除 non-client 高度；由 Windows working area 管理 Maximized，視窗左上角與工作區對齊，Paint transport 完整可見 |
| UI-14 | 逐張與核准參考圖重新比對所有細小差異 | 進行中 | Paint、Output、Settings、Heatmap 逐張核對位置、padding、字級、顏色、focus、圖示、label、timeline 與窄視窗狀態；差異不得只以「大致相近」結案 |
| UI-15 | 軌跡線與真實 report points 分層顯示 | 已實作；長 golden dark 實機通過 | `Trace` 顯示完整、未抽樣的背景線；`Points` 預設開啟並顯示每一筆 report point；兩者可獨立切換且不重新 decode，Paint／MP4 語義一致 |
| UI-16 | 十萬點無損軌跡與快速 frame 切換 | 已實作；synthetic gate 與長 golden 通過 | 100,003 點拆成 49 個 chunk；完整 chunk 快取、每次 seek 最多重建 2,047 點尾端；250 次亂序 frame 切換不重建完整歷史；120 Hz 長 golden 播放可到尾端 |
| UI-17 | Contact label 穩定定位 | 已實作；長 golden 視覺驗收通過 | 1–10 號固定慣用方位；ID4 優先下方；40 px leader 距離；只在碰撞或越界時換位；Frame 1 與密集軌跡 Frame 2072→2073 無換邊跳動，Paint 與輸出共用 layout |

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

- [ ] 以最新 KingstVIS 長 golden 產出 Paint 1920×1080 dark / light 截圖。
- [ ] 驗證冷啟動後視窗真正使用 Windows working area，沒有右下偏移、超出螢幕或底部 transport 裁切。
- [ ] 產出 Paint 1672×720 與最小支援寬度 1180×720 截圖，確認工具列、Auto-pause、timeline、Inspector 無重疊或裁切。
- [ ] 產出 Output MP4、Heatmap、Data package 與 Settings 截圖；確認非 Paint 頁面沒有底部 transport。
- [ ] 逐張並排比對上表參考圖：控制項位置／尺寸誤差目標不超過 4 px，字級誤差不超過 1 px，顏色必須使用既有 design token。
- [ ] 檢查所有 ComboBox 的 closed、hover、keyboard focus、mouse focus 四種狀態，關閉後不可殘留強調框。
- [ ] 檢查 1、5、10 contacts 與 Finger、Glove、Palm label；label 不遮 contact，類型 icon、ID 色與軌跡一致。
- [ ] 檢查 Mark 旗標、Loop bracket、Break/All Break auto-pause panel 與 Unmark menu 的實際互動。
- [ ] Heatmap → MP4 → Data package → Heatmap 連續切換五輪，不得卡住或失去 content selector。

### 細小差異回歸清單

- [ ] Header：Save、Output、Settings 的 icon 光學尺寸、基線、間距與無框狀態一致；disabled、hover、pressed、focus 後都不可殘留灰底。
- [ ] Paint toolbar：Panel、Point view、Trail、Length 等 row 高度與文字垂直置中一致；選單關閉後不可保留亮框。
- [ ] Paint canvas：boxed label 維持 baseline 的雙行格式、leader 長度與點距；1／5／10 點均不得遮點或漂到遠端。
- [ ] Floating tools：Trace、Grid、Flip X、Flip Y、Swap XY、Fit、Settings 對齊同一基線，不使用多餘的大外框。
- [x] Trail evidence：Trace line 與 Points 都保留 retention 範圍內的完整 report 序列；切片僅是無損繪圖批次，不刪除線段或報點。Lines only、Points only、Both、Neither 均有測試，並以長 golden 實機驗收前三種可視狀態。
- [ ] Transport：Play、clock、speed、Loop、Mark、Auto-pause、marker 旗標、Loop bracket 與時間文字的 padding、層級和點擊熱區逐項比對參考圖。
- [ ] Inspector：Frame／time／contact／CRC／ASIL hierarchy 在 320、380、520 px 寬度都不裁切；selected contact 與畫布 highlight 同色。
- [ ] Output：共用 content selector、MP4 preview controls、右側設定 rail、Heatmap color bar 與輸出 action 使用一致 spacing；頁面不可出現 Paint transport。
- [ ] Settings：左側 active item 在 hover 後仍保持 accent；五個 section 的標題、說明、分隔線與 control row 對齊主頁 token。
- [ ] Light mode：畫布、grid、axis、label、trail、Heatmap 和 warning/alarm 對比均單獨驗收，不直接沿用 dark mode 判定。

## 效能 completion gate

- [ ] 最新 KingstVIS golden（4,160 physical / 2,080 logical）在 0.01×、0.1×、1×、10×、MAX 與 Loop 下各播放一輪。
- [ ] 播放中連續切換速度與 Loop，不可凍結、不可產生多個 playback loop、Play/Pause 狀態不可錯亂。
- [ ] UI thread 上不逐 frame 重建 Raw/Decoded list selection；Inspector detail 更新維持節流。
- [ ] 3 分鐘持續 Loop 後記憶體不持續線性成長，速度／Loop／Mark 操作回應時間目標小於 100 ms。
- [ ] 保留 CLI benchmark 結果與測試數字，並記錄機器、輸入 SHA-256、frame 數與 build commit。

## 目前驗證紀錄

- Avalonia UI tests：44 / 44 passed。
- Core / parser / rendering tests：160 / 160 passed。
- 長 golden：951,254 bytes，4,160 physical records，2,080 logical frames。
- 10,000 random seeks：約 15 ms；1,000 個 640×360 raster frames：約 89.6 FPS。這是 renderer benchmark，不等同桌面 Paint 的實際呈現 FPS。
