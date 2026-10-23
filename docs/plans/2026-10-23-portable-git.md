# Portable Git 支援

目標：未安裝 Git、PATH 沒有 Git 時，可使用已解壓縮的 Portable Git。

- [x] Core 設定契約與 SQLite 保存；指定路徑優先，程式旁 PortableGit/Git 次之，最後 PATH。
- [x] 集中建立 Git 子行程，涵蓋一般執行器與快照 refs prepare/commit；選定發行包的 helpers 加入子行程 PATH，不修改系統 PATH。
- [x] Git 設定視窗：選檔／選資料夾、自動偵測、測試與儲存；至少 Git 2.40，錯誤路徑不可悄悄退回其他 Git。
- [x] 測試：空白／中文路徑、缺檔／舊版、保存與重開、沒有 PATH Git 的真實 Portable Git commit/merge hook/快照還原；本機完整 126 項回歸通過，三平台由 CI 再驗證。
- [ ] 文件、審查、提交及直接推送 main，確認 CI。

不下載或內嵌 Git 到產品套件；使用者可從 Git for Windows 官方發行頁下載 PortableGit 並解壓縮。保持七專案相依方向，Core 不引用 Process。
