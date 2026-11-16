// DdoddoZip.ShellExt — Explorer right-click items (F10, F11).
//
//   files / folders (not all archives):   또또집: ○○.zip으로 압축(D)
//                                         또또집: ○○.7z로 압축
//                                         또또집으로 압축하기...
//   archive files only:                   또또집: 여기에 풀기
//                                         또또집: ○○\ 에 풀기
//                                         또또집으로 풀기...
//
// Each item is an IExplorerCommand registered as a static verb with ExplorerCommandHandler
// (machine-wide), so it appears in the classic context menu ("더 많은 옵션 표시").
// This DLL only builds the menu text and starts DdoddoZip.exe; all real work happens in that
// separate process, so Explorer can never hang or crash because of it.

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
    enum class Kind { Zip, SevenZip, CompressDialog, ExtractHere, ExtractFolder, ExtractDialog };

    constexpr wchar_t kFallbackName[] = L"압축";
    constexpr size_t kMaxItemsToInspect = 200; // keep the menu instant for huge selections

    // Keep in sync with ArchiveTypes in DdoddoZip.App.
    constexpr const wchar_t* kArchiveExtensions[] = {
        L".zip", L".7z", L".rar", L".tar", L".gz", L".tgz", L".bz2", L".tbz", L".tbz2", L".xz", L".txz",
        L".zst", L".tzst", L".lzma", L".iso", L".cab", L".wim", L".lzh", L".lha", L".arj", L".cpio",
        L".rpm", L".deb", L".z", L".001",
    };

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
    // (Control Panel items, libraries, ...), in which case the menu items are hidden.
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

    bool IsDirectory(const std::wstring& path)
    {
        DWORD attr = GetFileAttributesW(path.c_str());
        return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY);
    }

    bool HasArchiveExtension(const std::wstring& path)
    {
        const wchar_t* ext = PathFindExtensionW(path.c_str());
        for (auto known : kArchiveExtensions)
            if (_wcsicmp(ext, known) == 0) return true;
        return false;
    }

    // True when every selected item is an archive file: then the extract items are shown
    // instead of the compress items (same behaviour as other archivers).
    bool AllArchives(const std::vector<std::wstring>& paths)
    {
        size_t n = paths.size() < kMaxItemsToInspect ? paths.size() : kMaxItemsToInspect;
        for (size_t i = 0; i < n; ++i)
            if (!HasArchiveExtension(paths[i]) || IsDirectory(paths[i])) return false;
        return n > 0;
    }

    // Same rule as NamePolicy.QuickArchiveBaseName in DdoddoZip.Core.
    std::wstring SingleItemName(const std::wstring& path)
    {
        auto name = LastSegment(path);
        if (IsDirectory(path))
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

    // Same rule as NamePolicy.ArchiveBaseName: "a.tar.gz" → "a", "사진.zip" → "사진".
    std::wstring ArchiveFolderName(const std::wstring& path)
    {
        auto name = LastSegment(path);
        for (auto multi : { L".tar.gz", L".tar.bz2", L".tar.xz", L".tar.zst", L".tar.lzma" })
        {
            size_t len = wcslen(multi);
            if (name.size() > len && _wcsicmp(name.c_str() + name.size() - len, multi) == 0)
                return name.substr(0, name.size() - len);
        }
        auto dot = name.find_last_of(L'.');
        return (dot != std::wstring::npos && dot > 0) ? name.substr(0, dot) : name;
    }

    std::wstring Title(Kind kind, const std::vector<std::wstring>& paths)
    {
        // The "또또집" prefix keeps these apart from other archivers' identically worded items.
        switch (kind)
        {
        case Kind::Zip:            return L"또또집: " + BaseName(paths) + L".zip으로 압축(&D)";
        case Kind::SevenZip:       return L"또또집: " + BaseName(paths) + L".7z로 압축";
        case Kind::CompressDialog: return L"또또집으로 압축하기...";
        case Kind::ExtractHere:    return L"또또집: 여기에 풀기";
        case Kind::ExtractFolder:
            return paths.size() == 1
                ? L"또또집: " + ArchiveFolderName(paths[0]) + L"\\ 에 풀기"
                : std::wstring(L"또또집: 각각 폴더에 풀기");
        case Kind::ExtractDialog:  return L"또또집으로 풀기...";
        }
        return L"또또집";
    }

    const wchar_t* Switch(Kind kind)
    {
        switch (kind)
        {
        case Kind::Zip:            return L"--quickzip";
        case Kind::SevenZip:       return L"--quick7z";
        case Kind::CompressDialog: return L"--compress";
        case Kind::ExtractHere:    return L"--extract-here";
        case Kind::ExtractFolder:  return L"--extract-folder";
        case Kind::ExtractDialog:  return L"--extract";
        }
        return L"";
    }

    bool IsExtractKind(Kind kind)
    {
        return kind == Kind::ExtractHere || kind == Kind::ExtractFolder || kind == Kind::ExtractDialog;
    }

    std::string ToUtf8(const std::wstring& s)
    {
        if (s.empty()) return {};
        int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
        std::string out(n, '\0');
        WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
        return out;
    }

    // Writes the selection to %TEMP%\DdoddoZip\shell-*.txt (UTF-8, one path per line).
    // A file avoids the 32K command-line limit when thousands of files are selected.
    bool WriteListFile(const std::vector<std::wstring>& paths, std::wstring& listPath)
    {
        wchar_t temp[MAX_PATH + 1] = {};
        if (!GetTempPathW(MAX_PATH, temp)) return false;
        std::wstring dir = std::wstring(temp) + L"DdoddoZip";
        CreateDirectoryW(dir.c_str(), nullptr);
        listPath = dir + L"\\shell-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()) + L".txt";

        HANDLE f = CreateFileW(listPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
        if (f == INVALID_HANDLE_VALUE) return false;
        std::string data;
        for (const auto& p : paths) { data += ToUtf8(p); data += "\r\n"; }
        DWORD written = 0;
        BOOL ok = WriteFile(f, data.data(), static_cast<DWORD>(data.size()), &written, nullptr);
        CloseHandle(f);
        return ok && written == data.size();
    }

    template <Kind K>
    class CommandBase : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
    {
    public:
        IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* name) override
        {
            std::vector<std::wstring> paths;
            if (!GetPaths(items, paths)) paths.clear();
            std::wstring title = paths.empty() ? std::wstring(L"또또집") : Title(K, paths);
            Trace(L"GetTitle -> " + title);
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
            *guid = GUID_NULL;
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
        {
            std::vector<std::wstring> paths;
            if (!GetPaths(items, paths))
            {
                *state = ECS_HIDDEN;
                return S_OK;
            }
            bool archives = AllArchives(paths);
            *state = (IsExtractKind(K) == archives) ? ECS_ENABLED : ECS_HIDDEN;
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            std::vector<std::wstring> paths;
            if (!GetPaths(items, paths)) return E_INVALIDARG;

            std::wstring listPath;
            if (!WriteListFile(paths, listPath)) return E_FAIL;

            std::wstring exe = ExePath();
            std::wstring cmd = L"\"" + exe + L"\" " + Switch(K) + L" --list \"" + listPath + L"\"";
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
}

// Class IDs: keep in sync with ShellIntegration in DdoddoZip.App.
class __declspec(uuid("BDC64B6B-7E60-401C-A845-CAA4BD6AA71F")) QuickZipCommand final : public CommandBase<Kind::Zip> {};
class __declspec(uuid("B62DA6EE-8453-4DCD-B67B-6D3E79731602")) Quick7zCommand final : public CommandBase<Kind::SevenZip> {};
class __declspec(uuid("A836D04B-F475-4923-BF16-BF5429FD0AB5")) CompressDialogCommand final : public CommandBase<Kind::CompressDialog> {};
class __declspec(uuid("10C5A761-3040-4168-9F84-8694004D0108")) ExtractHereCommand final : public CommandBase<Kind::ExtractHere> {};
class __declspec(uuid("873F9FB5-732E-499E-9B82-DF092B493C0E")) ExtractFolderCommand final : public CommandBase<Kind::ExtractFolder> {};
class __declspec(uuid("D95FD301-85B5-413B-BDB9-808CD227E75B")) ExtractDialogCommand final : public CommandBase<Kind::ExtractDialog> {};

CoCreatableClass(QuickZipCommand);
CoCreatableClass(Quick7zCommand);
CoCreatableClass(CompressDialogCommand);
CoCreatableClass(ExtractHereCommand);
CoCreatableClass(ExtractFolderCommand);
CoCreatableClass(ExtractDialogCommand);

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
