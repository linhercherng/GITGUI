#include <windows.h>
#include <shellapi.h>
#include <cstdint>
#include <string>
#include <vector>
#include "BundleOffset.h"

using BundleMain=int(__cdecl*)(int,const wchar_t**,const wchar_t*,const wchar_t*,const wchar_t*,int64_t);
extern "C" __declspec(dllexport) void CALLBACK RunW(HWND,HINSTANCE,LPWSTR,int) {
    HMODULE module=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&RunW),&module)) ExitProcess(2);
    wchar_t file[32768];
    const auto length=GetModuleFileNameW(module,file,32768);
    if(!length || length>=32768) ExitProcess(2);
    const std::wstring own(file,length);
    const auto slash=own.find_last_of(L"\\/");
    if(slash==std::wstring::npos) ExitProcess(2);
    const auto root=own.substr(0,slash);
    const auto bundle=root+L"\\GitGui.App.exe";
    const auto app=root+L"\\GitGui.App.dll";
    const auto library=root+L"\\hostfxr.dll";
    const auto fxr=LoadLibraryExW(library.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if(!fxr) ExitProcess(3);
    const auto run=reinterpret_cast<BundleMain>(GetProcAddress(fxr,"hostfxr_main_bundle_startupinfo"));
    if(!run) ExitProcess(4);
    int count=0;
    const auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    if(!args || count<2) ExitProcess(5);
    std::vector<const wchar_t*> managed;
    managed.push_back(bundle.c_str());
    for(int i=2;i<count;i++) managed.push_back(args[i]);
    SetEnvironmentVariableW(L"GITGUI_NATIVE_HOST_PATH",own.c_str());
    const auto result=run(static_cast<int>(managed.size()),managed.data(),bundle.c_str(),root.c_str(),app.c_str(),BUNDLE_HEADER_OFFSET);
    LocalFree(args);
    ExitProcess(static_cast<UINT>(result));
}
