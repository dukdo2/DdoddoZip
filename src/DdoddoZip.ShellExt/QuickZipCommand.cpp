// DdoddoZip.ShellExt — Explorer right-click item "○○.zip으로 압축하기" (F10).
//
// Registered per user under AllFilesystemObjects\shell\DdoddoZip.QuickZip with
// ExplorerCommandHandler = {BDC64B6B-...}, so it appears in the classic context menu
// ("더 많은 옵션 표시"). This DLL only builds the menu text and starts DdoddoZip.exe;
// all compression happens in that separate process so Explorer can never hang or crash
// because of it.

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/module.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <string>
#include <vector>

using namespace Microsoft::WRL;

namespace
{
    constexpr wchar_t kFallbackName[] = L"압축";

    std::wstring ModuleDirectory()
    {
        HMODULE module = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&ModuleDirectory), &module);
        std::wstring path(32768, L'\0');
        DWORD len = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
        path.resize(len);
        auto slash = path.find_last_of(L'\\');
        return slash == std::wstring::npos ? path : path.substr(0, slash);
    }

    std::wstring ExePath() { return ModuleDirectory() + L"\\DdoddoZip.exe"; }

    // Diagnostic trace: %TEMP%\DdoddoZip\shellext.log (only when built with DDZ_TRACE).
    void Trace([[maybe_unused]] const std::wstring& msg)
    {
#ifdef DDZ_TRACE
        wchar_t temp[MAX_PATH + 1] = {};
        if (!GetTempPathW(MAX_PATH, temp)) return;
        std::wstring dir = std::wstring(temp) + L"DdoddoZip";
        CreateDirectoryW(dir.c_str(), nullptr);
        HANDLE f = CreateFileW((dir + L"\\shellext.log").c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) return;
        SYSTEMTIME t; GetLocalTime(&t);
        wchar_t exe[MAX_PATH] = {}; GetModuleFileNameW(nullptr, exe, MAX_PATH);
        wchar_t head[64];
        swprintf_s(head, L"%02d:%02d:%02d pid=%lu ", t.wHour, t.wMinute, t.wSecond, GetCurrentProcessId());
        std::wstring line = std::wstring(head) + PathFindFileNameW(exe) + L" " + msg + L"\r\n";
        int n = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string utf8(n > 0 ? n - 1 : 0, '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, utf8.data(), n, nullptr, nullptr);
        DWORD w = 0; WriteFile(f, utf8.data(), static_cast<DWORD>(utf8.size()), &w, nullptr);
        CloseHandle(f);
#endif
    }

    // File-system paths of the selection. False when anything is not a real file or folder
    // (Control Panel items, libraries, ...), in which case the menu item is hidden.
    bool GetPaths(IShellItemArray* items, std::vector<std::wstring>& out)
    {
        if (!items) return false;
        DWORD count = 0;
        if (FAILED(items->GetCount(&count)) || count == 0) return false;
        for (DWORD i = 0; i < count; ++i)
        {
            ComPtr<IShellItem> item;
            if (FAILED(items->GetItemAt(i, &item))) return false;
            PWSTR p = nullptr;
            if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &p)) || !p) return false;
            out.emplace_back(p);
            CoTaskMemFree(p);
        }
        return true;
    }

    std::wstring TrimSlash(std::wstring s)
    {
        while (s.size() > 3 && s.back() == L'\\') s.pop_back();
        return s;
    }

    std::wstring LastSegment(const std::wstring& path)
    {
        auto p = TrimSlash(path);
        auto slash = p.find_last_of(L'\\');
        return slash == std::wstring::npos ? p : p.substr(slash + 1);
    }

    // Same rule as NamePolicy.QuickArchiveBaseName in DdoddoZip.Core.
    std::wstring SingleItemName(const std::wstring& path)
    {
        auto name = LastSegment(path);
        DWORD attr = GetFileAttributesW(path.c_str());
        if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY))
            return (name.empty() || name.find(L':') != std::wstring::npos) ? kFallbackName : name;

        auto dot = name.find_last_of(L'.');
        if (dot != std::wstring::npos && dot > 0) name = name.substr(0, dot);
        else if (dot == 0) name = name.substr(1);
        return name.empty() ? kFallbackName : name;
    }

    std::wstring BaseName(const std::vector<std::wstring>& paths)
    {
        if (paths.size() == 1) return SingleItemName(paths[0]);
        auto first = TrimSlash(paths[0]);
        auto slash = first.find_last_of(L'\\');
        if (slash == std::wstring::npos) return SingleItemName(paths[0]);
        auto parentName = LastSegment(first.substr(0, slash + 1));
        if (parentName.empty() || parentName.find(L':') != std::wstring::npos) return SingleItemName(paths[0]);
        return parentName;
    }

    std::string ToUtf8(const std::wstring& s)
    {
        if (s.empty()) return {};
        int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
        std::string out(n, '\0');
        WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
        return out;
    }

    // Writes the selection to %TEMP%\DdoddoZip\quickzip-*.txt (UTF-8, one path per line).
    // A file avoids the 32K command-line limit when thousands of files are selected.
    bool WriteListFile(const std::vector<std::wstring>& paths, std::wstring& listPath)
    {
        wchar_t temp[MAX_PATH + 1] = {};
        if (!GetTempPathW(MAX_PATH, temp)) return false;
        std::wstring dir = std::wstring(temp) + L"DdoddoZip";
        CreateDirectoryW(dir.c_str(), nullptr);
        listPath = dir + L"\\quickzip-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()) + L".txt";

        HANDLE f = CreateFileW(listPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
        if (f == INVALID_HANDLE_VALUE) return false;
        std::string data;
        for (const auto& p : paths) { data += ToUtf8(p); data += "\r\n"; }
        DWORD written = 0;
        BOOL ok = WriteFile(f, data.data(), static_cast<DWORD>(data.size()), &written, nullptr);
        CloseHandle(f);
        return ok && written == data.size();
    }
}

class __declspec(uuid("BDC64B6B-7E60-401C-A845-CAA4BD6AA71F")) QuickZipCommand final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* name) override
    {
        // "또또집:" prefix and the D accelerator keep the item distinct from other archivers'
        // identical "○○.zip으로 압축하기(&Z)" entries.
        std::vector<std::wstring> paths;
        std::wstring title = GetPaths(items, paths)
            ? L"또또집: " + BaseName(paths) + L".zip으로 압축(&D)"
            : std::wstring(L"또또집: ZIP으로 압축(&D)");
        Trace(L"GetTitle items=" + std::to_wstring(paths.size()) + L" -> " + title);
        return SHStrDupW(title.c_str(), name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        return SHStrDupW((ExePath() + L",0").c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        *guid = __uuidof(QuickZipCommand);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) override
    {
        std::vector<std::wstring> paths;
        *state = GetPaths(items, paths) ? ECS_ENABLED : ECS_HIDDEN;
        Trace(L"GetState slow=" + std::to_wstring(okToBeSlow) + L" items=" + std::to_wstring(paths.size()) +
              L" state=" + std::to_wstring(*state));
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        std::vector<std::wstring> paths;
        if (!GetPaths(items, paths)) return E_INVALIDARG;

        std::wstring listPath;
        if (!WriteListFile(paths, listPath)) return E_FAIL;

        std::wstring exe = ExePath();
        std::wstring cmd = L"\"" + exe + L"\" --quickzip --list \"" + listPath + L"\"";
        STARTUPINFOW si{ sizeof(si) };
        PROCESS_INFORMATION pi{};
        if (!CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
        {
            DeleteFileW(listPath.c_str());
            return HRESULT_FROM_WIN32(GetLastError());
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }
};

CoCreatableClass(QuickZipCommand);

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    HRESULT hr = Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv);
    Trace(L"DllGetClassObject hr=" + std::to_wstring(hr));
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(module);
    return TRUE;
}
