# Portable Git

GITGUI 支援 Git 2.40 以上的已安裝版本與完整 Portable Git；不需要修改系統 PATH。

## 設定

1. 從 [Git for Windows 官方發行頁](https://github.com/git-for-windows/git/releases/latest) 下載 PortableGit，解壓縮至自選位置。
2. 在 GITGUI 右上角開啟「Git 設定」。可選整個 Portable Git 資料夾，或選其 `cmd/git.exe`。
3. 按「測試版本與路徑」確認實際執行檔與版本，再「儲存並套用」。不必重新啟動。
4. 路徑保存在原有 SQLite 資料庫的 application_settings；下次啟動沿用。無效路徑、執行失敗或版本太舊都不覆蓋原設定。

如果希望整份應用程式搬移後繼續自動找到 Git，可用以下目錄配置並選「自動偵測」後儲存：

```text
GITGUI/
  GitGui.App.exe
  ...其他應用程式檔案...
  PortableGit/
    cmd/git.exe
    usr/
    ucrt64/       # 舊版可能是 mingw64/
    ...其餘 Portable Git 檔案...
```

同樣支援 `Git/`、`tools/PortableGit/`、`tools/git/`。這些路徑相對於正在執行的應用程式目錄；dotnet run 時指建置輸出目錄。也可直接選取其他磁碟上的 Git 資料夾，路徑可包含空白及中文。

優先順序為明確保存的路徑、程式旁的 Portable Git、PATH。明確路徑遺失時顯示錯誤，不會偷偷切換到不同版本的 Git；請重新選取，或選「自動偵測」儲存。PATH 的空白與相對路徑不參與自動尋找，避免從目前倉庫誤執行程式。

## 執行語意

一般讀取、所有寫入／網路、快照 refs prepare/commit 都透過同一個 GitRuntime 建立行程。Windows 子行程 PATH 會優先加入選定發行包的 cmd/bin、ucrt64（或 mingw64）與 usr/bin，並選用同一包的 git-core；系統 PATH 不變。既有 Git Credential Manager、AskPass 與使用者 Git 設定保留正常語意。

請保留完整發行包。只有一份 git.exe 不足以提供 hooks 所需 shell、SSH、傳輸與其他 helpers。[Windows x64 免安裝版](WINDOWS-RELEASE.md) 已內附完整 PortableGit 與 .NET 執行環境，解壓後直接開啟 GitGui.App.exe。從原始碼執行時仍需準備 SDK 與 Git。

## 驗證

本輪在 Windows 使用官方 PortableGit 2.56.0.3，解壓到含空白／中文的目錄，移除子行程所繼承 PATH 的 Git 項目後，實測 init、stage、commit、shell hook、amend、分支、快照 HEAD/index/worktree/ref 還原、本地 bare remote push/clone。命令紀錄逐項檢查選用的完整 Git 路徑，包含 ref 交易。

封存檔來源為 [官方 v2.56.0.windows.3](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.3)，SHA-256：

```text
4fc27b779829a36df77911e3f4dea09c66c790dd6c5f7dfa3b6a5479f1fe354f
```

Windows CI 透過 `tests/PreparePortableGit.ps1` 下載、核對雜湊後解壓；測試不把整包 Git 加入儲存庫。Linux/macOS 使用明確指定的原生 Git 路徑驗證同一執行流程。Auto discovery 搬移、SQLite 重開、失效路徑不退回系統 Git、版本門檻與設定視窗驗證皆有回歸測試。

```powershell
./tests/PreparePortableGit.ps1
dotnet test GitGui.sln -c Release
```

設定視窗截圖以 Headless 與固定測試路徑渲染；真正 Portable Git 的行為由引擎整合測試驗證。SSH 遠端服務的所有憑證／代理組合仍需依個別環境驗收。
