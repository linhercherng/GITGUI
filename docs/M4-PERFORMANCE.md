# M4 效能與重現

原始規格第11.1節的目標保持不變。v0.5.0為M4預覽版，完整效能關卡尚未通過。驗證使用同一台MSI筆電、Intel Core i7-13620H（10實體核心／16邏輯處理器）、51,229,798,400bytes RAM、KINGSTON SNV2S1000G NVMe SSD、Windows11 build26300（API回報Windows10.0.26300）、x64；.NET工作處理器固定8。實體fixture含100,000筆提交、50,000個檔案、10,000行文字檔及100處修改，Git2.56.0.windows.3的內建fsmonitor已啟動。

基準來源為M3標籤v0.4.0（`f1320bf47205f497be558902153acf4a7fd68ab5`），使用封存來源及原始.NET8.0.32；只套入相同量測驅動程式。M4使用.NET10.0.13。所有原始報告包含fixture fingerprint、HEAD、host、runtime、Git版本、二進位SHA-256、原始樣本與Git呼叫數，不能將不同電腦或不同fixture的結果用於10%比較。

## 重現指令

```powershell
# 使用完整PortableGit，設定只影響本shell。
$env:GITGUI_BENCH_GIT = 'C:/Tools/PortableGit/cmd/git.exe'
$env:DOTNET_PROCESSOR_COUNT = '8'
dotnet run --project tests/GitGui.Benchmarks -c Release -- --prepare C:/Bench/new-fixture 100000 50000
dotnet run --project tests/GitGui.Benchmarks -c Release -- --measure C:/Bench/new-fixture C:/Bench/new-report.json
./scripts/Compare-Performance.ps1 -Baseline C:/Bench/baseline.json -Candidate C:/Bench/new-report.json -OutputPath C:/Bench/comparison.json
./tests/Test-PerformanceComparison.ps1
```

Prepare只接受全新目錄，不刪除既有資料；報告也使用CreateNew語意。先建置，再在無其他build/test/下載的時段啟動已編譯的驅動程式。開圖取首次呼叫，狀態與Diff先暖機一次再取五次中位數；小於1ms的Core圖形佈局先暖機2000次，再每樣本1000次取平均，避免將JIT與計時器誤差當退步。

protocol2在Windows fixture的本機設定中啟用core.fscache，啟動fsmonitor後由一般Git status一次建立FSMN／UNTR index快取，再寫入測試修改。產品唯讀查詢仍使用no-optional-locks。比較器要求protocol、快取預備狀態、Git、fixture與host相同；不能將未預備的protocol1與protocol2混比。設定只作用於專用fixture，不修改使用者倉庫。

原生桌面可對已解壓縮的程式執行：

```powershell
./scripts/Measure-DesktopPerformance.ps1 -AppPath C:/Apps/GITGUI/GitGui.App.exe `
  -Fixture C:/Bench/new-fixture -GitExecutable C:/Apps/GITGUI/PortableGit/cmd/git.exe `
  -OutputDirectory C:/Bench/new-native-reports -Samples 5
```

此probe只接受自有產生器的100k／50k獨立fixture，使用新的TEMP設定／資料庫；不讀取一般使用者設定。它會顯示並關閉自己的測試視窗。不要把任意倉庫偽裝成fixture；停止專用fsmonitor時用該fixture的 `git fsmonitor--daemon stop`。

## 量測狀態

預發行protocol2的成對引擎報告（同fixture fingerprint `997fa82c370d493f9f31971cbf922c35`）如下。此表是功能來源完成、尚未提交時的診斷樣本；最終ZIP的二進位與原生報告另外記錄在同版Release的`verification.json`。

| 引擎指標 | M3 | M4 | 變化 |
| --- | --- | --- | --- |
| 初次讀取500筆 | 1945.38ms | 880.60ms | -54.73% |
| status中位數 | 2834.31ms | 175.78ms | -93.80%，10次Git啟動減為1次 |
| 10k行Diff讀取中位數 | 307.93ms | 157.96ms | -48.70% |
| Core500筆圖形佈局 | 0.2625ms | 0.1947ms | -25.82% |

主要引擎指標的10%比較通過。引擎報告只涵蓋讀取與Core佈局，不代表完整Diff呈現、桌面冷啟動、螢幕實際60fps或UI執行緒阻塞。

| 原始目標 | 預發行原生桌面觀察 | 驗收狀態 |
| --- | --- | --- |
| 冷啟動≤1.5s | 五次中位數894ms，最大1159ms | 該組程序冷啟動符合；不等於重開機／清空OS快取 |
| 大倉庫首屏≤1s | 一次826ms，四次約5.5–6.0s | 未通過，保留所有離群值 |
| status≤300ms | 五組中位數約185ms | 該組符合 |
| Diff顯示≤200ms | 五組中位數約151ms | 該組符合 |
| 常駐≤300MB | 該組最大277,475,328bytes | 該組符合，不強制GC |
| 捲動60fps | 原生RAF callback約60/s；其他實驗有未節流callback | callback不等於實體顯示器呈現，未稱為完整60fps驗收 |
| 單次UI阻塞≤16ms | 觀察到大於16ms的dispatcher延遲 | 未完整通過；延遲含OS排程與GC，不能等同每次callback CPU耗時 |

上述原生組為修復同步讀取取消之前的診斷資料。該問題已以8個阻塞回呼案例驗證後改為CancelAsync；最終ZIP另重新量測，不以舊數據冒充修正後證據。ordered heartbeat保留階段、GC與ThreadPool資料，同步property／layout span只記錄耗時與長度，未記錄文件內容。

修正後的0.5.0預提交五次重測：冷啟動中位883ms、首次新二進位離群9994ms；首屏中位5799ms（另兩次964／971ms）；status中位208ms；Diff中位199ms（部分組211／218ms）；常駐最大276,910,080bytes；dispatcher延遲最大124ms。RAF出現未節流callback，不能用其數字宣稱實體60fps。這些結果沒有抹去先前離群值，也沒有以中位數取代所有條件的嚴格上限；完整原生關卡仍未通過。

另以實際OS行程生命週期及專用fixture的Git Trace2診斷，觀察到約5秒延遲發生在Git的Trace2時鐘啟動之前；目前不能確定是wrapper、Windows載入或Git初始化階段。Software renderer、直接Git binary及限制同時子行程兩個的實驗未可靠消除延遲；後者亦拉長Diff，故沒有帶入產品。正常程式保持原來renderer與使用者選定Git路徑，也不放寬安全設定。暫時的行程／Trace2追蹤程式碼已移除，正式日誌不啟用這些追蹤。

最終來源、ZIP與所有原始樣本以同版Release的`verification.json`為準；固定主機以外的實體螢幕閱讀器、monitor呈現時間及其他桌面環境仍需另行驗收。M4正式版要在首屏、回應與實體呈現的驗證關卡完成後才能標記完成。

比較器驗證schema、fixture/host完整一致、數值有限且大於零；任一主要指標超過10%退步即拋出錯誤。CI執行比較器的正反案例；正式效能樣本只在固定主機執行，不能以GitHub共用runner的變動硬體冒充固定規格量測。
