# M4 正式版驗收

## 2026-11-21 發布條件調整：功能修正版，M4 未通過

依使用者最新指示「功能正常，些許延遲可以忽略」，v0.9.1 改以完整功能測試、同來源六格 CI 與最終 ZIP 驗證通過作為發布條件，明確保留效能限制；不宣稱 M4 正式效能驗收通過，也不修改原驗收器或任何數值門檻。以下歷史「全部通過才發布」描述的是先前的 M4 發布條件，不能解讀為本版已達成。

來源 `00768c3a5f552641ac992967b0398c1692412c88` 的單一 EXE 候選已完成同來源六格 CI、原始 M3 的 10% 比較，以及六組基準／30 筆原始樣本。正式桌面量測記錄首次冷啟動 **28,636.9516ms**，其餘已完成啟動也約 26～29 秒，超過 1,500ms。第一組捲動通過，第二組失敗；第三個冷啟動樣本因前景條件失敗而中止，未產生完整十份樣本或 68 項通過結論。原始失敗及中止資料完整保留，不以暖啟動抵銷，原因仍待確認。

本版發布附件應分開呈現功能驗證與未完成／未通過的 M4 紀錄。後續改善仍須使用原始門檻重新完整驗收。

## v0.9.1 驗收來源

來源 `b063941984775fa9a6aa56502a98f1e30250bd1e` 的中止交握仍留下首次啟動 1,673.6561ms，超過 1,500ms。完整正式樣本未完成，該候選不得發布。後續將預設啟動改為單一原生 `GITGUI.exe`，保留所有原數值門檻並加入等強度的 EXE 身分、單程序記憶體及反偽造檢查；新的啟動路徑必須重新完整驗收，不能沿用舊版 VBS 成績，詳見 [效能紀錄](M4-PERFORMANCE.md)。

2026-11-20，來源 `28eeaba7b79b0f6e3094df5a2be8038c37195801` 的六格 CI 通過，正式桌面判定為 **67／68、失敗**。新套件真正首次正常 VBS 啟動為 1,329.6985ms，十次啟動、五組讀取、記憶體與獨立 profiler 的 14.2874ms 產品工作均通過。唯一失敗是第 1 組捲動的起點至首幀 27.1794ms，超過原本 25.025ms；五組前景事件均有效，最大內部空檔為 16.5043～18.0686ms，不能以其餘樣本抵銷起點失敗。首幀附近另記錄到 9.057ms 的 Gen0／Gen1 GC 暫停，成本仍完整計入。後續診斷與虛擬列事件留存修正見 [效能紀錄](M4-PERFORMANCE.md)，尚未證明新修正通過正式門檻，未發布 v0.9.1。

2026-11-20，來源 `ef23f8b7ab6c9baf2d0b0d82cafa2b5daae55132` 的主分支六格 CI 通過，但正式桌面判定為 **64／68、失敗**。真正首次冷啟動 2,367.7156ms 超過 1,500ms；第 1 組三次 Diff 為 353.3704～381.8168ms，超過每次 200ms。該組捲動兩個端點均未取得前景，不能當作有效前景成績；記憶體取樣最大缺口 361.8219ms，也超過原本 200ms 涵蓋要求。其餘四組捲動及配對 profiler 的 14.3432ms 產品工作通過，仍不能抵銷這四項失敗。原因尚未確認，不歸因於使用者操作，未發布 v0.9.1。

前一候選 `bf36cac31974e24f89d6064164531ea0cfe2aa00` 的 **67／68、失敗**及其第 1 組 52.5707ms 空檔仍完整保留。跨程序背景時鐘對照亦觀察到重疊長空檔，診斷不能取代正式前景驗收。

下一輪使用 `Measure-DesktopPerformance.ps1 -ExternalDriver`，逐份依 `driver-request.json` 核對實際程序及視窗，取得前景並在計時前完成觀察後，才寫入該份 `continuePath`。交握發生在既有啟動時間記錄之後；不可重設啟動時間、預熱、略過失敗樣本或在產品操作計時中擷取畫面。每份原始前景事件、全部時間與取樣涵蓋仍由原驗收器檢查。

v0.9.0 本次重新量測保留冷啟動 1,694.6485ms 與產品 UI render 26.2507ms 的失敗，沒有以後續較快樣本抵銷。各 v0.9.1 候選的 render、status 與圖形配置超標亦完整保留。後續修正縮小文字列附加批次，並移除文字檢視中的非互動裝飾 automation 節點，保留完整 Value 與標準捲動 provider；另減少提交圖配置的暫存物件。原規格與所有門檻不變。正式結果須以該 Release 證據附件的完整十份原生報告、配對外部 profiler、逐樣本判定與程式身分核對為準，詳見 [本次效能紀錄](M4-PERFORMANCE.md)。只有全部通過才發布，不能把歷史 v0.7.0 的結果延伸為新版本通過。

## v0.7.0 歷史正式驗收

M4 沿用 [ROADMAP](ROADMAP.md) 與原始 [規格](specification.md) 第11–13節。v0.7.0正式效能驗收已通過，原始規格及所有門檻保持不變。實際二進位來源為 `f923f11e18286b78b5b7358212bce5736032d5be`；後續驗收器及CI調整沒有修改產品程式碼。

Windows／macOS／Linux × Git2.40／2.56的 [六格CI](https://github.com/linhercherng/GITGUI/actions/runs/40081899175) 全部通過，包含Core33、Engine288、UI170項測試及Windows ZIP驗證。實際ZIP另完成2,224次Git操作、24筆原版M3快照Undo/Redo與原狀復原、AskPass及桌面啟閉。

首次新二進位啟動1,319.8566ms，五組大型倉庫操作及捲動全部通過。獨立profiler保留462,924筆完整紀錄，其中174,902筆涉及產品操作、40筆跨階段，最大產品重疊13.9479ms。原版與加速後驗收器的68項判定相同；51個驗收情境、5,992個時間索引對照及17個實際native負向案例均通過。BDN六個case／30個actual及M3的10%比較亦完成，詳見 [效能報告](M4-PERFORMANCE.md)。

預設啟動路徑為 `GITGUI.vbs`。手冊更新採已測試映像的文件封裝，所有程式、Runtime、Portable Git及截圖逐檔SHA保持相同。`build-info.json`保留二進位來源，`release-info.json`記錄文件／驗收來源；Release的 `verification.json`、`SHA256SUMS.txt` 與證據ZIP提供完整核對。

## 歷史驗收紀錄

以下保留當時的失敗與限制，不代表v0.7.0最終判定。

2026-10-24續作的編輯器延遲建立、資料庫背景排程與收合操作面板改善，及仍未達標的原生數據，見 [本輪效能結果](M4-PERFORMANCE.md)。測試程式已驗證資料庫鎖定不阻塞呼叫端、切換Diff保留文件與語系、收合期間保留紀錄並於展開更新；這些功能性質不能取代正式效能驗收。

最新實際免安裝候選包來源 `4e3741b` 的 [六格CI](https://github.com/linhercherng/GITGUI/actions/runs/40048922703) 與Windows459項測試、2,224次Git操作及原版24筆快照功能驗證均通過。原生五組的首屏、status、Diff、記憶體及本次配對16ms工作範圍達標；首次啟動7,734.22ms與捲動cadence 0/5仍未過關，正式判定保持失敗，因此沒有發布0.7.0 Release。

前輪候選包來源為 `b34dc8a4fe6c8053cf854ace95a8180ec12d949f`，已完成五組原生量測與配對 dispatcher profiler，正式判定仍為失敗：首次啟動4,074.56ms、捲動cadence僅1/5通過、單次UI工作18.9497ms；首批歷史、status、Diff及記憶體該組符合目標。該來源的真實git.git與合成倉庫BDN六個case／30個actual樣本全部完成，但不取代最新來源的原生關卡。詳見 [逐項結果](M4-PERFORMANCE.md)。

該來源的 [CI run39993166764](https://github.com/linhercherng/GITGUI/actions/runs/39993166764) 為五格成功、一格macOS／Git2.56的UI測試同步失敗；Core33、Engine284均通過。失敗已在隔離測試受控重現：衝突編輯測試未等待Activated啟動的實際刷新，儲存因忙碌被正確拒絕。修正只等待正確的工作完成，不放寬儲存與復原斷言。修正來源 `d5b5e08a63873ab00336554ac967c4c0cfc56d46` 的 [六格 CI run39995975192](https://github.com/linhercherng/GITGUI/actions/runs/39995975192) 已全部成功，包含Windows套件驗證；其相較候選包來源只有測試同步與文件變更，產品原始碼相同。本文件後續僅同步此驗證結果，不將CI成功延伸成原生效能通過。

| 範圍 | 實作 | 驗證方式 |
| --- | --- | --- |
| 大型倉庫 | 首批500筆、續載1000筆、依 refs 世代的有界提交快取、原生游標、狀態獨立刷新 | 100k提交／50k檔案實體fixture、SHA-1/SHA-256分頁與外部改refs、取消後重試、寫入重新檢查 |
| UI 回應 | 批次清單通知、監看事件先合併再送UI、非同步Diff投影、延遲載入大型Diff | 50k事件/檔案壓力、過期選取與關閉切換測試；原生量測見效能文件 |
| 鍵盤與偏好 | 共用指令描述、自訂／解除／重設快捷鍵、視窗作用域、JSON原子保存 | 真正鍵盤stage/commit、衝突拒絕、交換快捷鍵後重啟、文字輸入焦點保護 |
| 雙語 | 繁中／英文資源、語系持久保存、開啟中視窗即時更新 | 資源鍵完整性、指令面板／設定／工具／終端機／確認文字與live切換 |
| 無障礙 | 提交圖虛擬列Selection/Scroll automation peers、檔案完整名稱、Diff唯讀Value provider、可見焦點 | Headless automation與Tab/ShiftTab；不將其稱為實體Narrator驗收 |
| 文字與路徑 | 依encoding標頭／嚴格UTF-8／Big5解碼；顯示偵測與EOL設定；不支援路徑顯示escaped bytes並禁止模糊寫入 | 真實Big5 commit/reword、混合訊息批次、Linux無效位元組檔名、Windows UTF-16別名保護 |
| 寫入編碼 | 遵守已設定的i18n.commitEncoding，未設定為UTF-8；未知／無法表示的文字在快照或寫入前拒絕 | emoji對Big5、空／未知編碼、rebase續作時編碼改變；不偷偷更改使用者Git設定 |
| 錯誤處理 | 保守分類、白話說明與建議動作、原始輸出仍可展開 | 認證／鎖／hook／網路／lease等案例；retry沿用原安全確認，鎖定說明不盲刪檔案 |
| 診斷 | 結構化參數/cwd/耗時/exit/錯誤分類，14日滾動保留，憑證遮罩 | attached/split/quoted秘密參數、URL/header、取消、ILogger與observer故障；不持久化stdout/stdin/AskPass |
| 執行環境 | .NET10 LTS、自含10.0.13、ReadyToRun、無trimming | 鎖定依賴、實際ZIP共享DLL逐一hash核對，隔離已安裝.NET/Git後啟動 |
| 散佈與升級 | Windows x64 ZIP含完整PortableGit2.56.0.3、離線手冊、保留舊設定與快照索引 | 解壓到中文空白路徑；AskPass UTF-8、桌面啟動關閉、資料庫／偏好重新開啟；不覆寫舊套件 |
| 授權 | 主程式MIT，第三方依原授權 | 根目錄LICENSE、NuGet版本/hash/license inventory、PortableGit原始來源包與notice |

## 執行環境與相容性決策

SDK固定10.0.402，Runtime固定10.0.13，依 [Microsoft官方發行metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json) 選定LTS。SDK下載SHA-512：

```text
ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd
```

上游 [支援OS表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) 截至2026-10-23列出Windows11、指定Windows10 Enterprise/IoT/LTSC版本、macOS15+與Ubuntu22.04+。原規格的macOS13已離開上游支援週期；本次macOS CI使用15 Intel，不宣稱驗證13，也不把Windows11本機測試延伸為所有Windows10版本驗收。Windows套件為x64；ARM決策仍見 [M3 ARM](M3-ARM-DECISION.md)。

Git固定2.40.0與2.56.0，各於Windows2022、Ubuntu22.04、macOS15 Intel執行；舊Git不具備的reftable案例明確skip。非UTF-8實體檔名字節測試僅能在Linux建立相應fixture，不能將Windows無法建立的案例當成跨平台通過。

## 驗證邊界

效能的每一項原始目標、基準比較與原生桌面量測邊界見 [M4效能](M4-PERFORMANCE.md)。Headless測試不能證明實體螢幕閱讀器讀音、顯示器實際幀率或所有桌面環境；Windows免安裝驗證也不是全新VM驗收。Git的使用者設定、credential helper、hooks與外部工具仍依個人環境執行。

逐行文字終端機沒有新增PTY/VT；託管平台PR/MR整合與可選遙測仍不在本次範圍。主程式不啟用遙測。
