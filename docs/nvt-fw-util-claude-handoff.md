# NVT FW UTIL / Raw Data Analysis：Claude 交接文件

更新：2026-09-29。這份文件交接**需求與工作狀態**，不是宣稱新功能已完成的操作手冊。請把 [TODO_NVT_FW_UTIL.md](../TODO_NVT_FW_UTIL.md) 當作逐項待辦的主索引；本文件補足決策脈絡、設計邊界、風險與接手順序。若本文件與使用者的新指示衝突，以新指示為準，並同步修訂待辦。

## 1. 接手時先知道的事

- 正式 C#／Avalonia 專案：`nvt-event-buffer-replay`，GitHub：<https://github.com/Dennis40816/nvt-event-buffer-replay>。截至本文寫作，工作分支為 `0.1.2`，HEAD 為 `06a525d`，本地比 `origin/0.1.2` 超前四個**文件**提交；工作樹在建立本文前是乾淨的。這只是當時快照，接手時務必重新執行 `git status -sb` 與 `git log -5 --oneline`。
- 現有可用產品仍叫 **NVT Event Buffer Replay**。更名為 **NVT FW UTIL**、新增工具首頁與 **Raw Data Analysis**，均尚未實作。不要把既有 Event Buffer 的 Raw Explorer 誤認為新的感測矩陣 Raw Data Analysis。
- 現有 Raw Explorer 處理 LA／I²C／Event Buffer 傳輸紀錄；新 Raw Data Analysis 處理逐 frame 的觸控感測**數值矩陣**。另外，現有架構保留的「raw waveform」指 SDA/SCL 波形轉 I²C 解碼，與新工具也不是同一件事。
- 執行環境要求是 **C# only**；舊 Python 專案只是格式、decoder、golden 參考，不能作為執行期依賴。
- 本階段請先核對規格與樣本，再選最小垂直切片實作。**NF 正規化、CNC／共模消除、TPMux2、Raw Check 的精確算法都尚未足夠定義**，不可補想像公式並標成正確結果。

### 對應閱讀順序

1. [產品待辦](../TODO_NVT_FW_UTIL.md)：FWU-01～FWU-10、已確認與待確認事項。
2. [Roadmap](../ROADMAP.md) 的「Proposed next product slice — NVT FW UTIL」：產品方向。
3. [README](../README.md) 與 [現有產品規格](product-spec.md)：不能破壞的 Event Buffer 基線、建置／測試入口。
4. [來源介面](source-adapters.md)：既有 decoded-I²C adapter 的責任邊界；感測矩陣需要自己的資料模型。
5. 實作時再讀相關 `src/`、`tests/` 與 [效能要求](performance.md)，不要僅憑本文更動既有 decoder。

## 2. 產品構成：已確認 vs. 尚待設計審核

### 使用者已確認

1. 產品 shell 改名 **NVT FW UTIL**。
2. 目前的 Raw Explorer、Decoded Events、Paint、Output 是同一個 **Event Buffer Analysis** 工具內的頁面，不應在首頁拆成四個平行工具。
3. 新增另一個獨立工作區 **Raw Data Analysis**；其 Load、frame 瀏覽、矩陣檢視、分析都不應塞進 Event Buffer Paint。
4. **Recent captures 僅屬於 Event Buffer Analysis**，不要放成跨工具的首頁總列表。
5. 延續目前工程工具調性與足夠大的主畫面；使用者否定過「泛用卡片 dashboard」風格，希望重新設計 workstation 首頁。

### 尚未經使用者最後確認的 UX 提案

- 首頁是簡潔工具 launcher；保留 Paint／Output 全寬，不常駐很寬的工具側欄。
- 返回 Tools 時保留 Event Buffer session 並暫停播放；直接以 Event Buffer 檔案開啟時進 Event Buffer Analysis，無檔啟動時進 Tools。
- Raw Data Analysis 採中央矩陣、底部逐 frame 導航、右側當前 frame 的可選分析。

先做能點選、能切工具、能回到既有工作區且不掉 session 的低風險 shell，再做視覺核對；聊天中的互動示意圖只是概念稿，**不是核准的 pixel-perfect 基線**。深淺主題及窄視窗需驗證，但不可為了塞設定而壓縮矩陣主視圖。

## 3. Raw Data Analysis：輸入、畫面與數值責任

### 輸入格式參考

使用者指定以其私有 `Dennis40816/nds_helper` 專案的 `frame_extractor` 作格式參考，而不是移植 Python。其已知入口／形狀包括：

| 參考入口 | 已知形狀 | 仍要核對 |
| --- | --- | --- |
| `load_csv_diffdata` | 帶時間資訊的 `DiffData` CSV，可有獨立 `Button Data` | 實際欄位、時間單位、矩陣尺寸與異常列 |
| `load_plain_matrix_log` | 帶時間戳、沒有 `DiffData` 標記的數值矩陣 log | frame 邊界、標頭變體 |
| `load_single_matrix` | 一張矩形數值矩陣，無時間戳／段落標記 | 數值型別、維度、缺值 |

Python 的 `frame.xy(x,y)` 是零起算座標，亦有 `frame.rc[row,col]`、逐 frame view 與統計 helper。這些只足以辨認候選來源格式，**不等於**我們已收到可公開的 golden 或完整 C# import contract。請先取得可分享的代表性輸入與預期矩陣，再鎖定 MVP 支援格式；自動偵測不確定時要顯示候選與原因，不能靜默猜錯。私有 repo、真實 capture、韌體 BIN、機密 golden 不可直接提交到這個公開 repo；可提交合成 fixture、格式 schema、來源註記／SHA-256 與人工核對結論。

### 最小可用畫面

- Load 後逐 frame 顯示**原始匯入矩陣**，看得到 X／Y cell 座標與數值。上一張、下一張、直接 seek、frame 編號，以及有來源時間戳時顯示來源時間。
- 原始檔 bytes、frame 順序與匯入矩陣不可被分析或視覺變換偷偷修改；派生矩陣必須是另一層，並可回到原始值。
- 右側是**目前 frame** 的分析區。使用者可勾選要計算／顯示哪些指標。平均、最小、最大、標準差是首批候選，不是已定義完整公式；仍需確認全矩陣或 ROI、signedness、無效 cell、Button Data 是否參與、單位與精度。
- `TPMux2` 留進階欄位，但輸入、算法、適用條件、輸出意義及 golden 未提供前，只能顯示「尚未定義／不可用」，不能輸出推測數字。
- 大檔載入與索引應在 UI thread 外；只計算已勾選的目前 frame 指標，使用有界 frame cache。效能要以長 capture 實測，不可只宣稱可支援。

## 4. Before／After Diff、NF 與共模消除：本次討論的核心

使用者對資料路徑的原話重點是：「before 到 after 主要就是兩個步驟，一個是感應量正規化，一個是共模雜訊消除」，希望從任一狀態推導其他三個；也指出「如果是 Stop FW 的 Before Diff，會先經過 NF，但還不會共模消除」。共模消除可能有數種方式，需能快速互動調整，例如 Stair CNC、是否有 TPMux2、排列順序。這是新工具的**重點工作**，不是一個固定公式加選單即可完成。

### 四個邏輯狀態

| 狀態代號（文件用，非現有程式 enum） | NF 正規化 | 共模消除 | 目前證據 |
| --- | --- | --- | --- |
| S00 | 關 | 關 | 邏輯上存在；實際來源與公式待確認 |
| S10 | 開 | 關 | **Stop FW 的 Before Diff = 這一態，已由使用者確認** |
| S01 | 關 | 開 | 邏輯上存在；操作順序與能否從其他態得出待確認 |
| S11 | 開 | 開 | 邏輯上存在；對應哪些 After Diff／輸出待確認 |

此表是處理階段的分類，**不是**四種都可無損互相反推的證明。若 CNC 丟失共模分量、NF 飽和／截斷或資訊不足，某些反向路徑可能不唯一甚至不可逆。應將推導結果標成「精確可重現」「需額外參數／假設」「無法可靠推導」等明確狀態，向使用者說明，而不是產出貌似精確的矩陣。即使 NF 與 CNC 都為開，**先後順序**、各步方法及其版本也可能改變結果；四態名稱不能取代 pipeline recipe。

### 用戶預期的來源情境

- 實際輸入多半是 `Before Diff`、`After Diff`，或 Stop FW 擷取的**單張已經 NF 正規化但尚未共模消除的 Before Diff**。使用者說的「normalized Before」是否還指另一個獨立輸入標籤，需要確認；不得自創第五個處理態。
- 使用者先前要求依 NF table 在 Before／After Diff 間「算回」，但 NF table 的檔案格式、列／欄索引、公式、正負號、精度、捨入／clamp、版本綁定與 golden 都未定；目前僅能顯示輸入值作為事實。
- CNC 可能需 Stair CNC、TPMux2 與排列設定；還沒取得 FW 真實運算順序、參數與 golden。不得把兩者硬編成所有 IC 共通的規則。

### 操作者可調的重建流程

使用者明確要求重建不能僅有單一固定公式：

1. 建立可重用的常用運算功能框（**具體清單待定**）。
2. 可以拖曳改變步驟順序，立即比較不同 recipe 的派生結果；輸入矩陣仍不變。
3. 可新增自訂關鍵步驟，用 `{col}` 等 placeholder 表達；但 `{col}` 是欄索引、目前 cell、欄向量或其他意義**尚未確認**。不要先定語法再逼資料適應。
4. 保存並顯示步驟順序、方法、參數、NF table 版本／hash、來源 frame／檔案、輸出狀態與適用性，讓結果可以重現與追責。

工程上建議使用受驗證、決定性、限制能力的 expression language，而非任意 C#／Python 程式碼執行；這是**安全設計提案**，仍需與使用者確認功能需求。至少要有「順序不同，golden 輸出也不同」的驗證案例，再實作拖曳後重算。

### 派生流程的建議保護線（非已核准算法）

- 將原始 snapshot、狀態解讀、NF table、recipe、派生矩陣分開建模。
- 每個運算 block 公開輸入／輸出狀態、作用範圍（cell、column、frame、capture）、參數驗證與是否可逆；不支援的轉換明確停用。
- 用背景工作與取消／版本 token 處理快速調整；舊 recipe 的結果不能蓋掉新 recipe。大矩陣可逐階段快取，先驗證正確性再優化。
- 保存計算來源與差異檢視；所有比較使用相同 cell 軸向、尺寸及座標語意。

## 5. Raw Check：已提出，尚無算法授權

使用者另外希望有 **Raw Check** 分析，並明說「其實不簡單，但可以先記錄」。它已列為 `FWU-10`；不要把 Raw Check 等同 min/max、SNR、簡單閾值或現有 QA。待確認：接受哪一類 input／上述哪個狀態、檢查規則與門檻、是否跨 frame、輸出明細與嚴重度、可否調參、代表性正反例 golden，以及與 TPMux2 的關係。在規則前，UI 只留清楚的未開放入口或待辦，不顯示虛構通過／失敗。

## 6. 現有程式的接點與不可回退的基線

- `src/Nvt.Replay.Avalonia/App.axaml.cs` 啟動目前 `MainWindow`；若帶 capture 命令列參數，會在視窗打開時載入。改成工具首頁時需保留直開檔案行為，並處理無檔啟動。
- `src/Nvt.Replay.Avalonia/MainWindow.axaml` 目前容納 header、Event Buffer tabs、Paint、Output、Inspector 與 transport。`MainWindow.Shell.cs` 負責 tab／shell 行為。重構 shell 時應把 Event Buffer chrome 限定在其工作區，不要只塞一個「首頁 tab」又讓全域 Paint transport 露在首頁。
- `src/Nvt.Replay.Sources/` 是現有 I²C／LA adapter；`src/Nvt.Replay.Analysis/` 包含 replay workspace／playback；`src/Nvt.Replay.Rendering/` 是繪圖基礎。新的 raw-matrix import、frame snapshot、metric 與 pipeline 責任宜明確分層，不要用現有 I²C `SourceRecord` 硬塞矩陣。
- 現有 Event Buffer `0x82`–`0x85`／Desay `0x97` decoder、Raw Explorer、Paint、Output、review、ASIL、MP4 等功能已有行為契約及測試。新工具不能把這些功能改壞；具體產品基線見 [README](../README.md)。
- Repo 目前用 .NET 10 solution `Nvt.EventBufferReplay.sln`，含 Core、Sources、Formats、Analysis、Rendering、Cli、Avalonia 與測試專案。接手時先確認 SDK／`dotnet build`／現有 tests 再開重構；建置指令以 README 為準。

## 7. 請優先向使用者確認的問題

按會阻塞正確性／架構的程度排序，不必一次問完；可帶樣本做互動確認。

1. **真實檔案與標籤**：先支援哪幾個 `nds_helper` 格式？一個檔案只有單張矩陣還是多 frame？`Before Diff`、`After Diff`、Stop FW 的標頭長什麼樣；同一 frame 能否同時有多個階段的資料？可提供可公開 synthetic golden 嗎？
2. **四態映射／可逆性**：一般 `Before Diff`、`After Diff` 各對應 S00／S10／S01／S11 哪個？「normalized Before」是否只是 Stop FW Before Diff 的別名？若 CNC 消除掉資訊，使用者接受顯示「無法精確反算」或帶假設的估計嗎？
3. **NF table**：來源檔案、row／col 軸向、尺寸、各 cell／欄對應、數值範圍與 signedness；正向與逆向公式、運算次序、整數／浮點、捨入、飽和、IC／FW 版本；至少一個每步 expected matrix 的 golden。
4. **CNC／TPMux2**：Stair CNC 的精確運算和參數、TPMux2 的位置與定義、是否有多種 FW method、如何根據來源辨認；若不知道 FW 實際方法，UI 要如何表示候選與比較，而不是宣稱唯一正解？
5. **可編輯步驟**：首批 built-in blocks 是哪些、每步作用範圍、`{col}` 的精確含義、允許的運算符／函式與錯誤顯示、預設 recipe；是否需要儲存／分享 recipe。
6. **當前 frame 分析與 Raw Check**：統計是整矩陣、選區還是可切換？Button Data 是否另欄？Raw Check 真正要找的異常與 golden 是什麼？
7. **視覺流程**：首頁／Raw workspace 的 prototype 看過後再定案，尤其大矩陣可讀性、右側分析寬度、來源版本與當前狀態的顯示方式。

## 8. 建議分階段交付與驗收門檻

這是交接建議，尚非使用者授權的一口氣實作範圍。若接手者本輪只是討論，先回答問題／更新規格；若被要求開發，再按最小可驗證切片進行。

| 階段 | 可交付內容 | 完成判據 |
| --- | --- | --- |
| A：規格與樣本 | 來源格式清單、狀態映射、NF／CNC／TPMux2 證據矩陣、synthetic golden 計畫 | 每個事實有來源；不明確轉換標成 gate |
| B：工作站 shell | NVT FW UTIL launcher、Event Buffer Analysis 保持原功能、Recent capture 僅在該工具 | 啟動、直開 capture、切回工具、Paint／Output 與既有測試不退化 |
| C：Raw read-only slice | 先選一個 golden 格式的 C# importer、原始矩陣與逐 frame 瀏覽、基本來源定位 | 值、X/Y、frame／timestamp 與 golden 一致；不改 source |
| D：選配基本分析 | 右側 current-frame metrics 與啟閉 | 精度、無效值、ROI 等規則由使用者確認並有 golden |
| E：派生 pipeline | 四態顯示、NF／CNC 可編輯 recipe、可逆性／不可用顯示、provenance | 正反向只在有算法及 golden 時啟用；步驟改序有可驗證輸出 |
| F：進階 | TPMux2、Raw Check、較大資料集效能與輸出 | 各自有明確契約、golden、失敗訊息與效能實測 |

任何階段不可用的數值功能都須明示「定義／證據待補」，不能悄悄 fallback；視覺派生值不可冒充韌體實際輸出。對公開 repo，只納入合成資料或去敏感的契約與測試。

## 9. 給 Claude 的開始指令

1. 先重新確認分支、HEAD、工作樹、是否已有後續實作；本文中的 Git 狀態會過期。
2. 讀第 1 節列出的文件與 `nds_helper` 可存取的格式說明；若無權限，明確告知缺少來源，不要憑名稱猜格式或複製私有內容。
3. 把「使用者確認」「工程建議」「待 golden」三類分開紀錄，尤其維持 **Stop FW Before Diff = NF on / CNC off**；不要把一般 Before／After 擅自映射成同一規則。
4. 在動算法前，先請使用者提供第 7 節最前面的必要樣本與 NF／CNC 契約；可先做不依賴未知公式的 shell 或 read-only 匯入切片，但要有對應驗收。
5. 每一切片都驗證既有 Event Buffer 工作流程、來源不變性、golden 與 UI 可讀性；更新 `TODO_NVT_FW_UTIL.md` 的狀態並記錄仍需人工審核的 gate。
