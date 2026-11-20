# M4 效能與重現

## 2026-11-20 自動 QoS 候選：64／68，未發布

來源 `ef23f8b7ab6c9baf2d0b0d82cafa2b5daae55132`、ZIP SHA-256 `8792c2682697ffba15769e8984a0c0ecc75e677f282e17ea0ed85114acc2c5c4`。主分支六格 CI `44554809401` 成功；但真正首次正常 VBS 啟動為 **2,367.7156ms**，超過 1,500ms。第 1 組三次完整可見 Diff surface 為 **353.3704、358.425、381.8168ms**，均超過 200ms。

第 1 組捲動的開始與結束觀察皆 `IsActive=false`，無法完成前景驗收；原始資料保留 37.4516ms 起始空檔及 183.6419ms 最大內部空檔，不能將背景結果當作有效前景成績。該組常駐用量沒有超過 300,000,000bytes，但記憶體取樣缺口 **361.8219ms** 超過原本 200ms 涵蓋要求。因此原驗收器為 **64／68、失敗**，不歸因於使用者操作，也不刪除該組。其餘四組通過，仍不抵銷失敗。

獨立同包 profiler 保留 297,645 筆完整紀錄，overflow=0。依原有產品階段與跨階段分類，189,912 筆涉及產品，含 40 筆跨階段紀錄，最大產品工作重疊 **14.3432ms**，符合 16ms。原始全階段 gate=false 的四筆紀錄亦保留，沒有新增排除規則。三入口 40ms 負向校準正確拒絕 queued 49.0538ms、send 44.547ms、native 45.5487ms。完整資料保存在本機 `artifacts/v091-automatic-formal-evidence`；沒有發布新 Release。

首次到達 `Main parsed probe arguments` 已耗時 1,480.5739ms，該區間包含進入 Main 前與 Parse 工作，不能直接全部稱為 CLR 載入。額外分段診斷的新二進位三次為 903.1735／723.0701／671.4046ms，未重現正式超時，不替換正式成績。相同既有二進位的 Exec／Run 交錯對照中，Run 未穩定改善並保留 2,546.557ms 離群值，因此沒有更換啟動器。腳本內部標記未觀察到 FileSystemObject 或命令組合的可見成本，尚未定位正式長等待的根因。自有程序的載入事件追蹤同樣屬診斷，不用事件間隔推定單一 DLL 或安全軟體的耗時及因果關係；沒有變更系統、安全性或防毒設定。

後續由使用者啟動管理員核心追蹤，GITGUI 仍以一般權限執行。三筆診斷為 794.7415／752.6738／800.2657ms，沒有重現首次超時；事件遺失0、六個目標程序均有I/O資料，87,609筆create/read起點均找到結束事件，沒有未配對或覆用覆蓋資料。啟動期間的已配對create/read最長0.6313ms；執行緒仍有少量未能分類的排程時間與建立後首段盲區，沒有補值為零或當作完整根因證明。這輪未觀察到超過1ms的DPC／ISR。

為讓下一次首次超時可直接分段，原生報告另保留既有OS查得的 `launchOrigin.ProcessStartedUtc`，以及Main入口的 `managedMainEntry` UTC／QPC。可以區分啟動器建立、原生程序建立、Main入口及既有參數解析checkpoint；它們不取代原本launcher的OS建立時間或surface完成端點，也不增加啟動期間檔案I/O。回歸測試先確認舊報告缺少欄位而失敗，再核對一般程序的真實建立時間及實際入口先於surface。這是增加定位資料，不宣稱已修正先前超時；新來源與實際散佈包仍須完整正式驗收。

## 2026-11-18 v0.9.0 重新驗收與 v0.9.1 修正

v0.9.0 正式 ZIP SHA-256 為 `912deb7e9a60865fd272d040a2c1fe15d6ba044e19f0ae9941f9b69a88f03e33`，二進位來源 `0a04fc6f7310699b1b1f21e0274d826af147d17d`。新的中文空白路徑解壓後，第一次呼叫正常 `GITGUI.vbs` 的 cold surface 時間為 **1,694.6485ms**，超過 1,500ms。該版本先前已在本機執行過，不能把這次新解壓誤稱為該二進位在本機的歷史首次執行。後續較快的重跑不能抵銷這筆失敗。

初輪 large 等候外部前景協調逾時，保留為未完成；沒有當成有效捲動成績。舊 fixture 缺少產生器身分清單，因此重新以原產生器建立 100,000 commits／50,000 files、10,000 行文字檔／100 處修改的實體 fixture，HEAD `b1cee5b2154b6b283da52f8eb9c138c53d6c7e97`、fingerprint `2804e73f3d1744f6bedaf7fc97a9f5eb`。沒有重建或偽造舊清單。

新 fixture 的五組捲動全部通過原本逐幀及端點關卡，surface 提交約 129.10–136.78fps；其餘絕對 native 畫面關卡亦通過。但配對的三入口獨立 profiler 記錄到產品 render **26.2507ms**，另有一筆 **16.1603ms**，故 v0.9.0 整體仍未達標。完整失敗紀錄分別在 `v090-m4-baseline-five`、`v090-m4-newfixture-five` 及 `v090-m4-newfixture-verdict.json`；不可只引用通過的捲動數值。

額外 layout／文字格式診斷將熱點定位至 `IncrementalTextView` 的 `TextLayer.SetVisualLines` 附加可見文字列；該重型診斷不作正式時間基準。v0.9.1 將每影格最多新增列數由八列降為四列。回歸測試先在八列版本確認失敗，再驗證完整可見區域、原文件文字、取消、換檔與過期影格處理。正式探針仍等待完整可見文字區域與實際 surface 提交，沒有用部分畫面縮短 Diff 完成時間。

第一個四列候選來源 `f5d5e66dda0c434ea9b3afcdb76ed4f8b6cdb2c3` 的真正首次啟動為 1,117.2516ms，十份 native 報告及五組捲動全部符合其絕對關卡；但配對 profiler 仍有 22.0386ms 與 17.6638ms 的產品 render，因此整體仍失敗。完整原始紀錄保留在 `v091-m4-native-five`、`v091-m4-desktop-verdict.json`，不能視為本版最終通過。

額外 EventPipe 追蹤在零遺失事件下，觀察到超標工作內的 `UiaRaiseStructureChangedEvent` 同步 COM 呼叫，來自文字繪圖層的子節點增刪；另有 Bounds 通知與字形繪製。診斷只用來定位，不扣除這些成本。先前遺失事件的重型追蹤亦保留，未作完整證明。參考對應 NuGet 來源的 [TextLayer](https://github.com/AvaloniaUI/AvaloniaEdit/blob/583f2f8279cf50830eb7fd79c73dec554a232abf/src/AvaloniaEdit/Rendering/TextLayer.cs) 與 [TextView](https://github.com/AvaloniaUI/AvaloniaEdit/blob/583f2f8279cf50830eb7fd79c73dec554a232abf/src/AvaloniaEdit/Rendering/TextView.cs)。

後續修正在文字檢視的 automation peer 中省略不可互動的裝飾繪圖層，避免每列建立不具文字語意的 None peer；保留可互動圖層及 inline 控制項。文件仍由編輯器 Value provider 提供完整 Unicode 文字，標準 ScrollViewer 的捲動 provider、唯讀與可編輯結果語意不變。新增測試確認裝飾節點不外露、互動圖層仍可存取、完整 1,000 行 Unicode 值與標準垂直捲動 provider 保留；不宣稱實體 Narrator 朗讀已驗證。

後續來源 `a0fcf2f4381f2340dd6a9b26bdc2d9181d8ab945` 的首次啟動為 1,245.3171ms，但第二組保留 25.7881ms 內部空檔，超過 25.025ms；第 4 組外部協調逾時，整輪保留為失敗／未完成，沒有產生通過的 summary。原始資料在 `v091-m4-final-native-five`。進一步保留文字檢視原本的非互動 Raw automation 分類，仍保有互動子控制項、完整文件 Value 及標準捲動；避免把裝飾檢視新增為互動／內容節點。相應分類測試先失敗再通過。後續五組使用既有自動驅動，仍逐筆檢查原始前景事件、空檔與所有其他門檻，先前失敗不刪除。

來源 `eef9da281550039bc0e7c52d5ca60463405e3dab` 的真正首次啟動為 1,177.9812ms，十份 native 報告及五組捲動通過；但獨立 profiler 的 363,750 筆紀錄中仍有一筆產品 render **16.3983ms**，因此整體失敗。原始紀錄及失敗判定保留在 `v091-m4-release-native-five` 與 `v091-m4-release-desktop-verdict.json`，不以四捨五入視為通過。

後續額外文字繪製／GC 診斷觀察到繪製期間的 Gen1 GC；這些重型診斷只用來定位，不作正式數值。外部 profiler 原先對每筆回呼重建相同方法名稱字串，再保留於 span 陣列；回歸測試重複同一回呼 100,000 次配置 5,600,000bytes。改用弱鍵方法名稱快取後，同一測試配置低於 4,096bytes，並核對閉包、泛型、動態方法及並行名稱完全相同。這是降低診斷工具自身開銷，不宣稱為產品程式加速。仍保留所有 span、原名稱、原始 QPC 進出位置、巢狀 inclusive 與跨階段計算；沒有扣除 GC 或推估的 profiler 成本，沒有更動驗收器或門檻。真正的 queued／UI Send／WndProc 40ms 負向校準均仍被拒絕，完整紀錄與 hook 身分隨證據提供。

來源 `bb00f6a47da8fd1fcd9d9b79e4d6e2b88f374681` 的真正首次啟動為 776.8626ms，五組捲動及三入口 profiler 通過，最大產品重疊工作 11.9932ms；但第 3 組的一次 status surface 為 **386.7671ms**，超過 300ms，整體仍失敗。引擎比較另保留圖形配置中位數 0.2583468ms，相對同 fixture 的 M3 基準 0.0694748ms 退步超過 10%。原始紀錄在 `v091-m4-certified-native-five`、`v091-m4-certified-desktop-verdict.json` 及 `v091-m4-certified-engine-comparison.json`。額外流程診斷中的 status 子程序約 148–157ms、runner 約 153–168ms，未重現該次超標；不能據此刪除或忽略失敗，也不推測為使用者操作造成。

後續圖形配置修正以索引逐一讀取父提交，只在多父提交時建立去重集合，保留原本邊線順序與 ordinal 去重語意。500 筆線性提交的診斷配置由 318,616bytes 降至 142,616bytes，約減少 55%；回歸測試先在舊程式確認超過新增的 200KB 配置預算，再驗證新程式及重複合併父提交的線條。這項額外回歸預算沒有取代或放寬任何 M4 關卡。工作樹診斷的原 protocol 2 比較通過，但不能代替最終提交的正式量測；新包仍須完整重驗十份 native 報告及獨立 profiler。

來源 `60c96d3aa19b58e5ada5cc2c567ad9b2c711a947` 的正式引擎圖形配置為 0.0771007ms，相對基準退步 **10.9765%**，仍超過 10%。該包尚未啟動，因引擎關卡失敗而未進入原生驗收；原始結果保留在 `v091-m4-optimized-engine-protocol2.json` 與比較報告，沒有四捨五入或放寬門檻。

進一步以有容量上限的陣列建立每列線段，移除逐列暫存清單；遇到空通道時，縮短至實際線段數，保持原本 `IReadOnlyList` 契約、順序與拓撲。容量依提交數及實際不同父提交數計算；多父提交先去重，避免大量重複 parent headers 放大配置。500 筆線性提交的診斷配置再降至 94,664bytes，相較原本 318,616bytes 約減少 70%；測試另核對合併通道留空時每列線段數，並以 100,000 個重複父提交驗證配置低於 16KB。探針新增 GitProcessRunner 完成通知的 QPC、階段、第一個參數（內建命令名稱）、runner 耗時與結束碼，不記錄其餘參數、路徑、環境或輸出；僅供後續區分 status 命令與發布等待成本，沒有更改既有完成端點、樣本數或驗收器。

來源 `8be6dc724f133d03159f0f092fac3b6a39fb7f00` 的正式引擎圖形配置中位數為 0.0659838ms，相對 M3 基準改善 5.0248%，四項比較通過。首次 cold 1,467.3586ms、全部 status 與 Diff 均通過，但第二組捲動保留 **70.7854ms** 內部空檔；配對 profiler 另有 **17.6489ms** 產品 render。因此整體仍失敗，原始資料保留在 `v091-m4-array-native-five` 與 `v091-m4-array-desktop-verdict.json`。空檔附近的工作執行緒計時器亦延遲，當時沒有 GC 增量；仍不足以確定 OS 排程根因，不能逕自歸因於使用者操作。

另一輪零遺失 EventPipe 診斷觀察到 23.3957ms render 與 11.6161ms Gen1 GC 暫停重疊；屬定位用診斷，不能取代正式 profiler。後續將外部 profiler 的百萬筆熱紀錄陣列改存純數值及字串編號，結束後才無損還原字串。測試確認熱紀錄不含 GC 參照、Unicode／大小寫／null／並行字串身分與全部時間及巢狀欄位保留。容量、原始 QPC 位置、完整紀錄、跨階段及 inclusive 判定均不變，也不扣除 GC 成本；真正 queued／UI Send／WndProc 40ms 負向校準仍全部被拒絕。這是減少診斷儲存負擔，並非宣稱產品或正式驗收已通過。

本版另新增每專案本機暫存排除清單；真實 Windows 桌面已操作加入已追蹤檔案、原生資料夾選擇器、保存、全部暫存與拒絕排除項暫存，並核對真實 Git index 僅含允許路徑、排除內容保留且未建立 `.gitignore`。功能測試不延伸為效能通過。

來源 `7e7a4c86a61e02c8c39895ec0a5d07ffece2b300` 的本機 Core53／Engine311／UI197、六格 CI、引擎四項 10% 比較及六組 BDN／30 個實測樣本均完成。圖形配置中位數 0.0442854ms，相對 M3 基準改善 36.2569%。但正式桌面判定僅 **54／68** 通過：首次冷啟動 **2,062.9989ms**、部分後續啟動、首批歷史最慢 **8,229.8154ms**、status 最慢 **1,200.1149ms**，以及兩組 **51.5636ms／26.7384ms** 捲動空檔仍超標。Diff 最慢 182.3813ms、最大常駐 226,136,064bytes，配對 profiler 保留 468,767 筆完整紀錄，195,964 筆涉及產品、40 筆跨階段，最大產品重疊 10.7394ms；這些通過不能抵銷其他失敗。原始資料與 `v091-stage-desktop-verdict.json` 保留，沒有發布該候選包。命令計時亦記錄到單次 config 約 5,679ms；尚不能逕自把全部延遲歸因於 UI 或使用者操作。

額外啟動追蹤保留零遺失 EventPipe 資料，僅作定位，不作正式冷啟動成績。後續修正讓尚無最近倉庫資料庫的啟動直接回傳空清單，在第一次實際保存時才建立 SQLite；已存在、損壞或無法讀取的資料庫仍沿用原錯誤處理，不偽裝為空清單。失敗開啟的連線也確實釋放，避免鎖住損壞檔案。尚未開啟倉庫時，分支表單預設收合；倉庫開啟後自動展開，手動收合不回寫倉庫狀態。新測試先驗證舊行為失敗，再驗證空讀不建立資料夾、首次保存可重開、損壞資料不變、連線釋放及分支表單切換。啟動／首屏完成端點與所有原門檻保持不變；修正是否達標仍須新套件正式驗收。

來源 `bf36cac31974e24f89d6064164531ea0cfe2aa00` 的 Core53／Engine313／UI198 共 564 項本機測試、六格 CI、引擎四項 10% 比較及六組 BDN／30 個實測樣本均完成。真正首次 cold surface 為 **857.7884ms**；十次啟動最慢 1,232.6427ms、首批歷史最慢 651.5364ms、全部 status 最慢 230.929ms、Diff 最慢 141.1834ms、最大常駐 224,522,240bytes，均符合原門檻。配對 profiler 保留 573,866 筆完整紀錄，175,705 筆涉及產品、40 筆跨階段，最大產品重疊 12.4232ms；同包三入口 40ms 負向校準仍全被拒絕。但第 1 組捲動出現 **52.5707ms** 內部空檔，其餘四組通過，因此正式判定為 **67／68、失敗**。原始資料在 `v091-idle-native-five`、`v091-idle-desktop-verdict.json` 與配對 profiler 證據，沒有發布該候選。空檔前後單次繪製約 3–4ms，工作執行緒計時器亦延遲，且未觀察到 GC 增量；這些資料不足以確認 OS、時鐘或其他排程根因，不能逕自歸因於使用者操作，也不能刪除該樣本。

額外的 180 秒背景時鐘對照，不啟動 Avalonia 或 GUI：STA、MTA 與原生非訊息等待的三個同程序時鐘，在同一時間都出現約 88–92ms 空檔。第二輪另加入獨立、完全不依賴 .NET 的原生程序；四次約 58–66ms 空檔在三個 .NET 時鐘及該獨立程序的 QPC 區間均重疊，全程沒有 GC。原始時間戳、診斷來源與雜湊保留在 `v091-idle-evidence/cross-process-clock-diagnostic.json` 及其列出的原始檔。這支持延遲不僅發生於 GUI，但背景對照不是正式前景量測，也不能證明與先前 52.5707ms 空檔有相同根因，因此沒有憑此更改 STA、降低門檻或撤銷正式失敗。更進一步的 WPR 核心排程追蹤遭系統拒絕（`0xc5585011`），未啟動追蹤，未變更安全性、電源或優先序設定；尚未定位到具體驅動或系統原因。

同包另外完成 1,831 次 Git 功能操作、apphost／WScript／單一執行個體桌面驗證；但原套件腳本的 AskPass 檢查失敗，不能將該次整體套件驗證記為通過。Windows 原生管線身分查詢顯示：測試預期啟動 PID 77128，最先連線的卻是 PID 58792；另一輪亦觀察到 PID 40576 搶先於實際 helper 44600。尚未判定這些其他程序的來源。修正測試器只接受實際啟動 helper 的 PID，其他連線斷開後，在原本總共 10 秒連線期限內繼續等待；原請求、UTF-8 回應、stdout、exit code 與其他 10 秒期限不變，沒有修改產品程式。新增回歸案例在 helper 啟動前，以測試程序先連線再關閉：舊測試器確實失敗，新測試器必須拒絕該連線，並完成真正 helper 的完整協定才通過。原失敗與修正後的測試證據均保留；這項測試器修正不改變捲動失敗判定。

2026-11-19 的非管理員背景對照另比較同程序、相同計時邏輯與一般優先序的兩條原生時鐘：自動 QoS 保留 82 次超過 25.025ms 的空檔，明確 HighQoS 保留 1 次，最大仍為 57.0731ms，不能稱為全部通過。CPU 統計改在獨立執行緒以 100ms 低頻取樣；逐影格讀取 CPU 統計的另一輪資料亦保留，因可能干擾量測而不作因果證明。原始資料、CPU 拓撲及來源雜湊保存在 `v091-scheduling-evidence`。

同一既有套件再以外部 hook 做前景 A/B/A/B 暖啟動診斷。自動排程兩輪最大 surface 空檔為 25.6576ms（失敗）／19.4243ms；HighQoS 兩輪為 16.7999ms／21.2595ms。原始時間戳顯示，自動排程第一輪的 25.3406ms 渲染時鐘空檔與超標重疊，但這輪前景渲染執行緒本已幾乎都在高效能核心，不能把根因簡化成效率核心。四輪皆保留，含已失敗的一輪；外部 hook 及策略介入使其不能作正式驗收。這些小樣本僅支持製作候選，不能證明修正已通過 M4。

該輪候選曾在 Windows 渲染工作執行緒提出 [HighQoS 排程提示](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadinformation)，API 不支援時維持原本時鐘；沒有更改系統電源計畫、全域計時解析度、CPU 親和性或執行緒優先序。新增測試先在原自動策略得到 ControlMask=0 而失敗，再確認工作執行緒的實際 OS 策略已設定、程序層級策略不變、優先序維持 Normal，並能正常停止。該候選後續正式結果記錄於下段；未通過者不得發布。此提示可能改變渲染工作的效能與耗電取捨，尚未量測電池續航。

首次候選 CI 的四組 Unix 測試通過，兩組 Windows Server 2022 測試因查詢 QoS 策略得到錯誤 87 而失敗，原始日誌保留。依 Microsoft 的資訊類別支援範圍，策略查詢測試限 build 22000 以上；較舊 Windows 仍執行既有時鐘啟停、重新訂閱與併發關閉測試，不宣稱已驗證其 HighQoS 策略。本機首次完整 UI 測試另有首次提交操作逾時（198／199），與並行引擎測試、建置同時發生；尚不能據此判定原因，失敗結果仍保留。

來源 `46e684e0bbafb114ced28fab4fb92c884aadb44d` 的完整本機測試為 Core53／Engine313／UI199，共565項，六格CI、原本四項10%比較及六組BDN／30個原始實測樣本均通過。首次新二進位正常VBS啟動為934.5024ms，十次啟動最大1,303.3129ms；首批500筆歷史最大940.8311ms、全部15次status最大272.5851ms、Diff最大158.3908ms、最大常駐216,043,520bytes。但第1組捲動有44.4472ms空檔，第5組有40.1225ms及65.7688ms空檔；配對profiler另有18.1055ms跨產品階段UI工作。因此為 **65／68、失敗**，沒有發布該候選；完整逐筆資料保留在 `v091-qos-evidence`。

後續管理員WPR搭配一般權限GITGUI的暖啟動診斷重現六個空檔，介於26.9046～80.6444ms，原始事件遺失數為0。其中三個空檔與多個CPU同時執行同一核心DPC重疊；以相符的Microsoft `ntkrnlmp.pdb`（GUID `ce884f70-f5a9-025d-53db-35d8be8bff79`、Age1）解析為 `KiSwapDirectoryTableBaseTarget`。36.0909ms及51.1651ms空檔的渲染執行緒分別有27、32筆CPU取樣標記在此DPC中，不能把其running或render wall time一律視為應用程式繪圖計算。

前一份輕量設定未收集呼叫堆疊，屬收集限制，不能憑單一核心名稱指定觸發者。改用已核對CSwitch／ReadyThread／SampledProfile堆疊設定的 `CPU.Verbose` 短追蹤後，觀察到910,196筆CPU取樣，其中798,498筆帶堆疊，事件遺失數為0。六份非DPC取樣的相同呼叫鏈為 `MiRebuildLargePages → MiRebuildLargePage → MiFindContiguousPagesEx → MiTradePage → MiStealPage → KeSwapDirectoryTableBase → KeGenericProcessorCallback → KiGenericCallDpcInitiatorWorker → KiInitiateGenericCallDpc → KiSwapDirectoryTableBaseTarget`，上層為System程序的 `ExpWorkerThread`。這是該輪觀察到的記憶體管理相關核心工作；該輪沒有重現超標，因此不能據此將所有舊失敗一律歸為同一原因。也沒有依據將個別出現在其他取樣中的第三方模組當作根因。

恢復Windows自動QoS，移除尚未在實際產品正式驗收中證實有效的HighQoS提示。原生時鐘回歸測試先在強制策略確認ControlMask=1而失敗，再確認自動策略ControlMask=0、程序策略及正常優先序保留、啟停與併發關閉正常。這項變更不宣稱已修正Windows核心工作造成的延遲；新包仍須重新驗收。新的正常輪次及診斷輪次不抵銷先前失敗，原始核心追蹤只保留在本機。

v0.9.1 的最終數值及整體判定以 Release 證據 ZIP 中的原始 cold／large 報告、summary、配對 dispatcher profiler、逐項 desktop verdict 及引擎比較報告為準，須核對相同受測 binary 的 SHA-256／MVID 與 fixture。原 cold 1,500ms、首屏 1,000ms、每次 status 300ms、Diff 200ms、300,000,000bytes、產品工作 16ms、持續捲動 60fps／25.025ms 空檔及引擎回歸 10% 全數維持。未全部通過不發布；未測試的新二進位不可沿用既有成績。

以下內容為 v0.7.0 及更早歷史紀錄，不是本版的量測值。

原規格第11.1節及10%退步門檻保持不變。v0.7.0已完成正式效能驗收；先列最終結果，後保留歷史候選版及失敗紀錄。

## 2026-10-24 最終正式驗收

二進位來源 `f923f11e18286b78b5b7358212bce5736032d5be`。原測試ZIP SHA-256為 `0144d2d47af465292ca77418d93e6b93f728d2eec7570f0dcda26c95ba9368df`。從中文空白路徑解壓，使用內附Portable Git及正常 `GITGUI.vbs` 啟動。新二進位第一次執行即保留，沒有預啟動、暖機或捨棄慢樣本；五組cold／large配對的十份原始報告全部進入判定。

| 指標與原門檻 | 最終觀察 | 判定 |
| --- | --- | --- |
| 冷啟動 ≤1,500ms | 首次1,319.8566ms | 通過 |
| 首500筆 ≤1,000ms | 五組最大812.2499ms | 通過 |
| 每次status ≤300ms | 15筆最大236.3389ms | 通過 |
| 一萬行文字檔Diff ≤200ms | 15筆最大131.2405ms，包含完整可見文字列及surface提交 | 通過 |
| 常駐 ≤300,000,000bytes | 桌面加存活WScript啟動器合計最大221,233,152bytes，含端點及100ms採樣 | 通過 |
| 持續捲動60fps | 五組各至少五秒，實際繪製／surface提交118.85–131.00fps，所有內部及兩端空檔通過25.025ms關卡 | 通過 |
| 產品UI工作 ≤16ms | 174,902筆，含40筆跨階段，最大13.9479ms | 通過配對範圍 |
| 主要引擎退步 ≤10% | 同機、同fixture、相同protocol2的四項M3比較均通過 | 通過 |

主機為Windows11 build26300、Intel i7-13620H，測試有效處理器數固定8，Runtime10.0.13。原生量測沒有變更系統電源方案、全域timer resolution或執行緒優先權。原生捲動以Activated／Deactivated事件及端點觀察核對前景狀態；畫面確認在計時前完成，計時中不擷取文字樹或截圖。

### 修正與證據邊界

Windows使用程式自有高解析度render clock及軟體呈現；SDK native bundle／composite ReadyToRun減少首次載入成本。正常 `GITGUI.vbs` 使用Windows既有WScript及rundll32，不散佈這兩個系統檔案。`GitGui.App.exe` 保留作備用方式，其啟動時間不能沿用上述1.5秒結果。

已停止閒置進度列的indeterminate動畫。細分量測將剩餘熱點定位到AvaloniaEdit一次附加整個可見區域的文字列視覺節點；Diff改為每影格最多新增八列，保留完整文件、複製、唯讀及左右比對。換檔或關閉會取消過期readiness。探針等待完整可見區域建立，核對文字及可見高度，再等待真正的render histogram與CompositionBatch.Rendered，沒有把部分畫面視為Diff完成。

獨立profiler保留462,924筆紀錄，無overflow及未閉合span。入口為DispatcherOperation.Execute、UI Action Send fastpath與WindowImpl.AppWndProc；巢狀span採inclusive，跨階段依實際重疊計算。通用Invoke<T>、其他COM或native入口不在instrumented範圍。render／surface提交不是GPU、DWM或實體螢幕光子完成，RAF與heartbeat不作替代證明。

同一hook的queued／Send／native canary各記錄約45ms已知阻塞，均被16ms門檻拒絕。原版PowerShell及編譯加速驗收器均逐筆檢查所有紀錄，68項判定完全相同；沒有抽樣、丟棄span或扣除假設開銷。另通過51個驗收情境、5,992個線性／索引對照及17個以真實通過基準生成的native負向案例。

### 引擎、相容性與套件驗證

BDN固定六個case：真實git.git及100k／50k合成倉庫，各測完整OpenAsync、status及選定Diff。每case一個launch、一個warmup、五個actual，invocation／unroll皆1，DontRemove且不變更電源方案；保留30筆原始actual與六份fixture metadata。git.git固定HEAD `7e34d8174dcda3435d1404c9a30bef46afe6c13e`，82,563個commit／4,857個檔案，partial clone讀取時禁止lazy fetch。

BDN完整OpenAsync包含首批圖之外的狀態等資料；真實倉庫選定的歷史Diff也不是合成倉庫的一萬行檔案，不能將這些完整操作時間直接替代上表畫面驗收。完整JSON、log及各case條件均附於證據ZIP。

與M3來源 `f1320bf47205f497be558902153acf4a7fd68ab5` 的配對比較：首500筆581.5716ms、status中位146.1484ms、Diff中位45.7279ms、layout500中位0.0820371ms；相對基準分別改善70.10%、94.84%、85.15%、68.75%，均符合退步不得超過10%的門檻。

[六格CI run40081899175](https://github.com/linhercherng/GITGUI/actions/runs/40081899175) 全部通過；其來源 `862cc11acd9acf9228d961553e71f7347e424cb7` 與二進位來源的產品程式碼相同。CI逐專案執行完整Core33／Engine288／UI170，保留停滯診斷，沒有減少案例或放寬斷言。先前兩次平行啟動而未完成的CI保留取消狀態，不計為成功。

實際ZIP通過2,224次Git操作、24筆原版M3快照Undo/Redo及原狀復原、原生／備用AskPass與桌面功能驗證。最終封裝只更新手冊與發行metadata；程式、Runtime、Portable Git、授權及截圖逐檔SHA保持相同。原測試ZIP及文件封裝的外層ZIP雜湊因此不同，分別列於verification及promotion報告，不重新定義第一次二進位執行。

完整資料見Release的 `M4-acceptance-evidence.zip`；本機原始資料位於 `artifacts/m4-native-sliced-five`、`artifacts/m4-sliced-bdn` 及 `artifacts/releases/v0.7.0-f923-evidence`。

## 歷史候選版與失敗紀錄

以下記錄當時的數據及設計選擇，不代表最終版本的設定或判定。


### 歷史候選包4e3741b：當時正式關卡未通過

來源 `4e3741bc96a8b9d6716fdeb1776b217263eb80a4` 的0.7.0本機候選ZIP，SHA-256為 `dfe2a8bc5eee26204c2834f89169ffbe519a6d03f54a5630355b5e344f38ea22`。從實際ZIP解壓，在中文空白路徑使用隨附Portable Git 2.56.0.windows.3完成五組原生量測；沒有先啟動暖機或丟棄首次樣本。正式判定 `pass=false`，尚未發布GitHub Release：

| 指標 | 本次觀察 | 判定 |
| --- | --- | --- |
| 冷啟動 ≤1,500ms | 首次7,734.22ms；其餘九次838.43–1,109.51ms | 未通過 |
| 首500筆 ≤1,000ms | 最大849.74ms | 本組通過 |
| status ≤300ms | 15筆最大279.37ms | 本組通過 |
| 10k行 Diff ≤200ms | 15筆最大135.51ms | 本組通過 |
| 常駐 ≤300MB | 捲動期間最大263,323,648bytes | 本組通過 |
| 持續捲動60fps | 嚴格cadence 0/5；保留27.2ms內部空檔、起點空檔及不足幀數 | 未通過 |
| 單次 UI 工作 ≤16ms | 本次配對profiler的2,899筆產品工作，含37筆跨階段，最大15.5945ms | 本次配對範圍通過 |

收合操作面板的指令文字更新，先前觀察到43.6639ms；本次14筆對應綁定更新的最大值為1.8327ms。這是具體熱點的觀察，不能用來宣稱首次啟動或整體效能已達標。不同來源、二進位、路徑的首次啟動數值不作為配對改善百分比。

完整判定與來源核對保留於 `artifacts/releases/v0.7.0-internal-final/desktop-verdict.json`，十份原始報告在 `artifacts/m4-v070-shipping-native-five`。報告包含相同七個產品DLL的MVID／hash、配對profiler與所有跨階段工作，沒有修改驗收器門檻。UI單次阻塞仍限於文件後述三個instrumented入口，不宣稱涵蓋所有native／COM路徑。

同一來源的 [六格CI](https://github.com/linhercherng/GITGUI/actions/runs/40048922703) 全部通過；Windows／Git2.56為Core33、Engine287、UI139，共459項。實際ZIP另通過隔離系統Git／.NET的2,224次真實Git指令、原版24筆快照目錄還原／重做與原狀復原、LFS、SSH簽章、UTF-8 AskPass、SQLite與libgit2驗證。桌面功能就緒為5,886.60ms，該測試只採30秒功能上限，不等於1,500ms效能通過。

免安裝功能驗證首次由另一建置目錄產生測試驅動程式，被共用DLL的逐位元雜湊檢查攔下，尚未執行Git功能案例。保留該次紀錄，改由建立ZIP的同一乾淨來源目錄重跑後通過，未放寬來源核對或600秒操作測試上限。手冊21章、16張標註圖、63個離線連結／錨點及圖片雜湊皆通過檢查；未新增GitHub示範倉庫。

相同二進位另做三組探針軟體呈現對照，實際提交平均61.39–61.98fps，但嚴格cadence仍為0/3；較高平均值不能抵銷長空檔。該模式在既有二進位已執行後才測試，首次軟體路徑1,850.49ms不代表新版本首次冷啟動。原始報告保留於 `artifacts/m4-v070-software-diagnostic-three`；沒有採用軟體呈現為產品預設，亦未以對照數值替換正式失敗樣本。

### 前兩個修改階段

本輪先完成三項可獨立回歸的修改：單欄 Diff 不再預先建立左右比對的兩個編輯器；最近專案讀寫與快照清單查詢移到背景；收合的操作面板不建立／更新文字框，並先裁切過大的指令輸出再通知綁定。展開操作面板會顯示完整保留範圍內的最新紀錄，複製功能維持原有行為。SQLite 的 Async ADO.NET 方法實際同步執行，依據為 [Microsoft 官方說明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)。

來源 `abafa43` 的五組原生量測保留首次啟動 6,665.62ms，仍未通過。來源 `6ccd5e2` 的後續五組結果如下；不同二進位的首次數值不作為配對改善百分比，也沒有刪除較慢樣本：

| 指標 | 逐筆結果 | 判定 |
| --- | --- | --- |
| 冷啟動 ≤1,500ms | 首次 3,107.82ms，其餘九次未超過門檻 | 未通過 |
| 首500筆 ≤1,000ms | 五組均未超過門檻 | 通過該組 |
| status ≤300ms | 一筆 307.69ms | 未通過 |
| 10k行 Diff ≤200ms | 一筆 225.17ms | 未通過 |
| 常駐 ≤300MB | 五組均未超過門檻 | 通過該組 |
| 持續捲動60fps | 嚴格 cadence 0/5；保留起點、內部間隔與不足幀數 | 未通過 |
| 單次 UI 工作 ≤16ms | 第一個超標 render 19.1584ms；操作面板更新另有43.6639ms | 未通過 |

完整配對輸入及七個 DLL／profiler 身分核對保留於 `artifacts/m4-resume-storage-verdict.json`；原始結果在 `artifacts/m4-resume-storage-native-five`。操作面板修改依據上述實際阻塞區段，不能由修改完成推論已通過效能門檻。該階段139項 UI 測試與7項資料庫／排程相關測試通過；後續最新實包量測見上節，仍未通過完整正式關卡。

探針另在所有產品工作結束後、產生報告之前排入一次真正的 Dispatcher 讓出，避免報告雜湊／JSON寫檔延長上一個產品 callback。所有原始 profiler spans、跨階段判定、正式計時端點與門檻均保留；這是診斷工作排程調整，不是使用者程式的效能改善，也不代表正式驗收通過。

完整壓縮單檔封裝也做過一次獨立診斷：首次表面提交 2,440.51ms，且因 `Assembly.Location` 為空，現有七 DLL 來源核對失敗。該樣本已保留並標為未完成，沒有納入正式成績或採用為發布格式。共用建置目錄另產生一次無報告的原生失敗與一次診斷重現；均保留，後續候選改由獨立乾淨來源建置。

## 固定主機與資料

主機為 MSI 筆電、Intel Core i7-13620H（10 實體核心／16 邏輯處理器）、51,229,798,400 bytes RAM、KINGSTON SNV2S1000G NVMe SSD、Windows 11 build 26300（API 回報 Windows 10.0.26300）、x64。.NET 工作處理器固定 8，電源計畫維持 Balanced；不更改系統安全設定、程序優先權或強制 GC。

合成實體 fixture 含 100,000 筆提交、50,000 個檔案、10,000 行文字檔及 100 處修改，fingerprint 為 `997fa82c370d493f9f31971cbf922c35`。Git 2.56.0.windows.3 啟用該 fixture 的 fsmonitor、fscache，先建立 FSMN／UNTR 快取；產品唯讀查詢仍使用 no-optional-locks。這些設定只作用於專用 fixture，不修改使用者倉庫。

另保留上游 git.git 的完整 master 祖先歷史，HEAD 為 `7e34d8174dcda3435d1404c9a30bef46afe6c13e`，包含 82,563 個可達提交及 4,857 個索引檔案。此為非 shallow 的 blob:none partial clone；必要 Diff 物件事先取得，量測子程序設定 `GIT_NO_LAZY_FETCH=1`，不得在計時期間網路補取。

基準來源為 M3 標籤 v0.4.0（`f1320bf47205f497be558902153acf4a7fd68ab5`），保留原 .NET 8.0.32，只套入相同量測驅動程式。M4 使用 .NET 10.0.13。比較必須匹配 protocol、fixture、主機及 Git；原始樣本與離群值全部保留。

## 目前驗收狀態

2026-10-24 的 candidate8（來源 `7eaa6d62fe73799b6cd1b90d5696083d7c3d0a40`）五組未注入追蹤工具的原生量測如下。新二進位第一次執行包含在原始樣本中；這是候選程式，尚非最終 ZIP。離線完整判定 `artifacts/m4-v060-candidate8-desktop-verdict.json` 為 `pass=false`。

| 原始目標 | 該組觀察 | 判定 |
| --- | --- | --- |
| 冷啟動 ≤1,500ms | 首次 6,432.46ms，下一次大型模式啟動 5,928.46ms；其餘八次約 880–960ms | 未通過，首次樣本不能當暖機丟棄 |
| 大倉庫前 500 筆首屏 ≤1,000ms | 五次最大 854.23ms | 該組符合 |
| status ≤300ms | 全部 15 次最大 212.24ms | 該組符合 |
| 10k 行 Diff 顯示 ≤200ms | 全部 15 次最大 146.35ms | 該組符合 |
| 常駐 ≤300,000,000 bytes | 捲動期間全部樣本最大低於 263,000,000 bytes | 該組符合 |
| 持續捲動 60fps | 五組只有一組通過嚴格 cadence；其餘起始空檔 28.99–33.03ms，內部最長空檔 26.21–33.53ms | 未通過 |
| Git 操作期間 UI 單次阻塞 ≤16ms | 配對外部 profiler 記錄 20.1075ms render 工作與 17.4541ms Loaded 佇列工作 | 未通過，不能由平均值抵銷 |

這組已使用依文字內容重用的有界文字快取、保留的不可變繪圖筆，以及同視窗一個實體像素的 composition marker。marker 確認所等待批次確有本視窗的繪製，不能單憑成功的空批次任務宣告顯示完成。這些修正消除了既有量測盲點，但未使全部原始效能關卡通過。

### 實際免安裝候選包 2

來源 `b34dc8a4fe6c8053cf854ace95a8180ec12d949f` 再將四個進階歷史篩選欄位改成使用者首次展開才建立，保留原搜尋命令、條件值、鍵盤與無障礙操作；完整 UI 測試 135 項通過。實際自含 Windows ZIP 已內附 Portable Git 2.56.0.windows.3，SHA-256 為 `108886fa101d29b4433f47d241587ea88ad5646052eb1c7f84fae2c0c59f621e`。這是本機驗證用候選包，沒有發布到 GitHub Release。

從該 ZIP 解壓到新的中文空白路徑，以包內 Git 執行五組原生量測，保留第一次啟動；再以相同二進位配對外部 profiler：

| 原始目標 | 候選包 2 的實測 | 判定 |
| --- | --- | --- |
| 冷啟動 ≤1,500ms | 首次 4,074.56ms；其餘九次 892.74–1,129.94ms | 未通過 |
| 首批 500 筆 ≤1,000ms | 五次 601.00–676.02ms | 通過該組 |
| status ≤300ms | 全部 15 次 176.80–246.54ms | 通過該組 |
| 10k 行 Diff ≤200ms | 全部 15 次 80.82–129.46ms | 通過該組 |
| 常駐 ≤300,000,000 bytes | 捲動期間最大 268,263,424 bytes | 通過該組 |
| 持續捲動 60fps | 嚴格 cadence 通過 1/5，仍有起始與內部長空檔 | 未通過 |
| UI 單次阻塞 ≤16ms | `open500.await-native-frame` 的 dispatcher 工作 18.9497ms | 未通過 |

完整輸入、逐筆上限與七個 DLL／profiler 身分配對判定保存在 `artifacts/m4-v060-candidate2-desktop-verdict.json`，總判定仍為 `false`。不以其他九次較快啟動或平均 FPS 抵銷失敗，也不從這兩組未配對的首次數值宣稱啟動尾端已改善多少。

同一 ZIP 另通過隔離系統 Git／.NET 的套件功能驗證：2,224 次真實 Git 指令、原版 24 筆快照目錄的還原／重做與原狀復原、SQLite／libgit2／LFS／SSH、UTF-8 AskPass、中文空白解壓路徑及正常桌面關閉。此功能測試只要求 30 秒內可互動；該次正常桌面 Readiness 為 4,657.67ms，不能被稱為通過 1,500ms 效能關卡。套件功能收據與總判定分開保存於 `artifacts/releases/v0.6.0-candidate2`，總判定仍為未通過、未發布。

首次新二進位啟動的既有 5–14 秒離群值亦保留。candidate7 的首次 EventPipe 診斷為 6,285.7ms，沒有事件遺失；大部分已觀察時間位於同步 assembly loader 區間，JIT 只占較小部分。CLR 首個事件之前約 1,569ms 為追蹤盲區。Loader 區間可能包含其他底層工作，現有證據不能判定 Windows、安全軟體、磁碟或 GPU 是根因；追蹤本身會改變耗時，不能拿來代替未注入工具的正式量測。

另以固定來源的獨立 0.6.2 診斷二進位做一次自有程序 native stack 取樣：245 個樣本全部保留，啟動前 166 個樣本中 116 個位於 `NtCreateSection+20`，與先前 managed loader 區間一致，支持繼續調查載入路徑。取樣有暫停、符號查核成本與 CreateProcess 盲區，且沒有擷取 syscall 參數；無法由此確認特定檔案、單次呼叫耗時、安全軟體或磁碟根因。該次 5,839.52ms 是診斷值，不是正式效能成績；也不是發布版本 0.6.2。

Read 路徑已加入設定批次查詢、保守失效快取、格式可用時的原生 refs／Diff／preview，以及獨立首批 history 發布；所有寫入仍走 CLI，重新取得完整狀態與安全前置檢查。SHA-256、reftable、特殊 attributes／encoding／Git 環境等不支援原生讀取的情況保留 CLI fallback。提交圖快取已成形的文字行，Diff 刷新保留相同文件與視窗幾何；這些功能測試結果不等於正式效能通過。

## 重現引擎與回歸比較

```powershell
$env:GITGUI_BENCH_GIT = 'C:/Tools/PortableGit/cmd/git.exe'
$env:DOTNET_PROCESSOR_COUNT = '8'
dotnet run --project tests/GitGui.Benchmarks -c Release -- --prepare C:/Bench/new-fixture 100000 50000
dotnet run --project tests/GitGui.Benchmarks -c Release -- --measure C:/Bench/new-fixture C:/Bench/new-report.json
./scripts/Compare-Performance.ps1 -Baseline C:/Bench/baseline.json -Candidate C:/Bench/new-report.json -OutputPath C:/Bench/comparison.json
./tests/Test-PerformanceComparison.ps1
```

Prepare 只接受新目錄，報告也禁止覆寫。先建置，在沒有其他 build、test 或下載的時段執行。protocol2 的手動比較器對首次 Open 計時；status／Diff 暖機一次後取五次中位數，Core 圖形佈局按其固定重複協定計算。此統計協定只用於引擎回歸比較，不用來替代原生 UI 的逐樣本上限。任何主要指標退步超過 10% 就失敗。來源 `6c714ddec508273a098df84a32da6468183d00e8` 的同主機配對比較已通過：

| 引擎指標（ms） | M3 | 候選 | 變化 |
| --- | ---: | ---: | ---: |
| 首批 500 筆發布 | 1,945.38 | 574.46 | −70.47% |
| status 中位數 | 2,834.31 | 132.55 | −95.32% |
| 10k 行 Diff 中位數 | 307.93 | 43.09 | −86.01% |
| Core layout 中位數 | 0.2625 | 0.06035 | −77.01% |

配對輸入 SHA 與逐項判定保存在 `artifacts/m4-v060-final-engine-comparison.json`；此時 UI 與原生驗收仍未通過。

BenchmarkDotNet 同時使用合成與實際倉庫：

```powershell
$env:GITGUI_BENCH_SYNTHETIC = 'C:/Bench/new-fixture'
$env:GITGUI_BENCH_REAL = 'C:/Bench/git.git'
$env:GITGUI_BENCH_SOURCE = 'exact-source-commit'
dotnet run --project tests/GitGui.Benchmarks -c Release -- --filter '*RepositoryReadBenchmarks*' --artifacts C:/Bench/new-bdn-results
```

預設 1 launch、1 warmup、5 actual iterations，每 iteration 只呼叫一次，保留所有離群值且不更改電源計畫。每次建立全新 service／native reader／暫存資料庫。`CompleteOpenFirst500` 計完整 Open task，與 progressive 首屏不同；另外測 status、合成工作檔 Diff 及實際 HEAD 的歷史 Diff。完整協定見 儲存庫的 `tests/GitGui.Benchmarks/README.md`。BDN 必須確認所有 case 與實際樣本成功；程序 exit 0 本身不足以證明成功。

來源 `b34dc8a` 的最終 BDN 六個 case 均完成，共 30 個 actual 樣本；實際 job 的 launch／warmup／iteration／invocation／unroll、保留離群值、包內 Git、兩個 fixture HEAD 及電源計畫皆已逐一核對。下表採未扣 overhead 的原始 WorkloadActual（ms）：

| Fixture | 引擎工作 | 平均 | 中位數 | 最大 |
| --- | --- | ---: | ---: | ---: |
| git.git | 完整 OpenAsync | 688.03 | 698.34 | 709.09 |
| git.git | status | 120.81 | 120.80 | 126.44 |
| git.git | HEAD 歷史 Diff | 447.29 | 448.38 | 461.54 |
| 100k／50k 合成 | 完整 OpenAsync | 447.70 | 451.15 | 456.24 |
| 100k／50k 合成 | status | 140.57 | 136.55 | 153.49 |
| 100k／50k 合成 | 工作檔 Diff | 41.86 | 41.91 | 43.00 |

git.git 的歷史 Diff 為 `Documentation/RelNotes/2.98.0.adoc`，與合成 10k 行工作檔及原生 UI 顯示計時不同；447ms 不標為 UI 的 200ms 關卡通過。原始五個值、完整 BDN JSON 與六份 fixture 收據保存在 `artifacts/m4-v060-bdn-final`。先前 `--cli` 產生不同預設迭代次數的中止嘗試另存，未納入統計；正式命令明確指定原本 1／1／5 次數並核對執行中的 job 描述。BDN 成功不能取代仍失敗的原生啟動、捲動與 16ms 關卡。

## 原生視窗驗證

```powershell
./scripts/Measure-DesktopPerformance.ps1 -AppPath C:/Apps/GITGUI/GitGui.App.exe `
  -Fixture C:/Bench/new-fixture -GitExecutable C:/Apps/GITGUI/PortableGit/cmd/git.exe `
  -OutputDirectory C:/Bench/new-native-reports -Samples 5
./tests/Test-DispatcherProfiler.ps1 -AppDirectory C:/Apps/GITGUI `
  -Repository C:/Bench/new-fixture -GitPath C:/Apps/GITGUI/PortableGit/cmd/git.exe -NoCanary
./tests/Test-DesktopPerformanceAcceptance.ps1
```

Probe 只接受自有產生器的 100k／50k fixture，以新的 TEMP 設定／資料庫顯示及關閉自有視窗，不讀取一般使用者設定。請勿把任意倉庫偽裝為 fixture；停止專用 fsmonitor 時只對該 fixture 執行 `git fsmonitor--daemon stop`。

正式時間終點是實際 render scope 加上對應 `CompositionBatch.Rendered` 完成、drawing session／surface update 返回。這仍不能證明 GPU 執行完成、Windows compositor 或實體顯示器呈現；RAF callback、全域 DWM 計數與 heartbeat 不可冒充該證據。

捲動固定至少五秒，名義 60Hz 兼容 60000/1001Hz；逐一檢查完整 raw frame，內部或端點空檔達 25.025ms 就失敗，不以恢復後的平均 FPS 抵銷。記憶體每 100ms 由 worker 取樣，保留前後邊界與全部最大值；任一值超標、取樣缺口過大、render error 或身分不一致皆失敗。

獨立外部 profiler 記錄 dispatcher Execute、UI Action Send 與主視窗 WndProc 的實際進出；nested span 為 inclusive，依原始 QPC 的產品階段重疊時間判定 16ms。不以 heartbeat queue delay 代替工作耗時，不扣除猜測的 profiler 負擔。泛型 Invoke、其他 native／COM callback 不在其覆蓋範圍。正式 gate 必須核對 PID、報告 SHA-256、七個產品 DLL 的 MVID／hash 與 profiler hook；未提供或無法核對時保持未驗證。

最終正式來源、六格 CI、同主機比較、實際 ZIP 的完整原生樣本、舊版快照復原與散佈雜湊都通過後，才會發布新版並在 Release 附上 `verification.json`／`SHA256SUMS.txt`。Headless 或固定主機測試不延伸為其他桌面環境、實體螢幕閱讀器或全新 VM 已通過。
