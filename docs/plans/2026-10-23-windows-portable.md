# Windows portable release v0.1.0

- [x] Publish win-x64 self-contained, untrimmed, with .NET 8.0.32.
- [x] Extract the SHA-256 verified official PortableGit 2.56.0.3 beside the app.
- [x] Include dependency notices, source inventory, quick start and build manifest.
- [x] ZIP, extract to a new Chinese/space-containing path and test those exact files.
- [x] Test bundled runtime/Git without system Git/.NET discovery; SQLite, libgit2,
      commit/hooks, snapshot restore, push/clone, AskPass and desktop startup.
- [ ] Build and run existing regression tests; review changes.
- [ ] Push source, tag the tested commit, publish ZIP/checksums/source assets.
- [ ] Download the published asset and verify its SHA-256.

Scope: Windows 10/11 x64. No installer, elevation, PATH registration or global
runtime installation. Application settings remain in LocalApplicationData/GitGui.
No claim of an entirely clean VM, remote provider authentication or code signing.

Baseline ZIP smoke passed 466 actual CLI invocations, native dependencies,
AskPass UTF-8 and desktop startup. Final committed-source ZIP must repeat this
verification before publication. The release verification attachment and GitHub
CI are the authoritative publication gate/results for the last three steps.

Rollback: withdraw the defective Release assets/mark the Release as prerelease;
retain immutable tags and checksums and publish a new patch version. Never replace
an existing version's ZIP silently. User repositories and settings are preserved.
