#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#undef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <winhttp.h>
#include <wintrust.h>
#include <softpub.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <strsafe.h>
#include <stdio.h>
#include <wchar.h>
#include "runtime-release.h"

#define UPDATE_UI (WM_APP + 1)
#define FINISHED (WM_APP + 2)
#define MAX_DOWNLOAD (128ULL * 1024 * 1024)
#define ID_START 101
#define ID_CANCEL 102
#define ID_WEB 103
static HWND mainWindow, titleLabel, detailLabel, statusLabel, progressBar, primary, cancelButton;
static HFONT regularFont, headingFont, smallFont;
static HBRUSH whiteBrush;
static CRITICAL_SECTION statusLock;
static WCHAR statusText[1024], appDirectory[MAX_PATH], runtimeRoot[MAX_PATH];
static volatile LONG canceled, installing;
static int percent, busy, ready, preview, demoFailure;
static DWORD installExit;

static BOOL FileExists(const WCHAR *path) {
    DWORD a = GetFileAttributesW(path);
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}
static void Join(WCHAR *out, size_t count, const WCHAR *base, const WCHAR *suffix) {
    StringCchPrintfW(out, count, L"%s\\%s", base, suffix);
}
static void Status(const WCHAR *message, int value) {
    EnterCriticalSection(&statusLock);
    StringCchCopyW(statusText, ARRAYSIZE(statusText), message); percent = value;
    LeaveCriticalSection(&statusLock);
    if (mainWindow) PostMessageW(mainWindow, UPDATE_UI, 0, 0);
}
static void WinError(const WCHAR *operation, DWORD code) {
    WCHAR text[1024];
    StringCchPrintfW(text, ARRAYSIZE(text), L"%s（错误 %lu）。请重试，或使用微软官方下载页面。", operation, code);
    Status(text, 0);
}
static BOOL CompatibleVersion(const WCHAR *name) {
    unsigned major, minor, patch; int consumed = 0;
    return swscanf(name, L"%u.%u.%u%n", &major, &minor, &patch, &consumed) == 3 &&
        name[consumed] == 0 && major == 10 && minor == 0;
}
static BOOL HasFramework(const WCHAR *root, const WCHAR *framework, const WCHAR *dll) {
    WCHAR folder[MAX_PATH], glob[MAX_PATH], file[MAX_PATH]; WIN32_FIND_DATAW data;
    StringCchPrintfW(folder, ARRAYSIZE(folder), L"%s\\shared\\%s", root, framework);
    Join(glob, ARRAYSIZE(glob), folder, L"*");
    HANDLE find = FindFirstFileW(glob, &data); if (find == INVALID_HANDLE_VALUE) return FALSE;
    BOOL found = FALSE;
    do {
        if (!(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || !CompatibleVersion(data.cFileName)) continue;
        StringCchPrintfW(file, ARRAYSIZE(file), L"%s\\%s\\%s", folder, data.cFileName, dll);
        if (FileExists(file)) { found = TRUE; break; }
    } while (FindNextFileW(find, &data));
    FindClose(find); return found;
}
static BOOL IsX64Executable(const WCHAR *path) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return FALSE;
    IMAGE_DOS_HEADER dos; DWORD read; DWORD signature = 0; IMAGE_FILE_HEADER header;
    BOOL ok = ReadFile(file, &dos, sizeof(dos), &read, NULL) && read == sizeof(dos) && dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew > 0 && dos.e_lfanew < 1024 * 1024;
    if (ok) {
        SetFilePointer(file, dos.e_lfanew, NULL, FILE_BEGIN);
        ok = ReadFile(file, &signature, sizeof(signature), &read, NULL) && signature == IMAGE_NT_SIGNATURE &&
            ReadFile(file, &header, sizeof(header), &read, NULL) && read == sizeof(header) && header.Machine == IMAGE_FILE_MACHINE_AMD64;
    }
    CloseHandle(file); return ok;
}
typedef struct { size_t size; const WCHAR *host_path; const WCHAR *dotnet_root; } HostParameters;
typedef int (__cdecl *HostInitialize)(const WCHAR *, const HostParameters *, void **);
typedef int (__cdecl *HostClose)(void *);
typedef void (__cdecl *HostErrorWriter)(const WCHAR *);
typedef HostErrorWriter (__cdecl *HostSetErrorWriter)(HostErrorWriter);
static void __cdecl IgnoreHostError(const WCHAR *message) { (void)message; }
static BOOL ResolveFrameworks(const WCHAR *root) {
    WCHAR glob[MAX_PATH], library[MAX_PATH] = L"", config[MAX_PATH]; WIN32_FIND_DATAW data; unsigned best = 0; BOOL found = FALSE;
    StringCchPrintfW(glob, ARRAYSIZE(glob), L"%s\\host\\fxr\\*", root);
    HANDLE find = FindFirstFileW(glob,&data); if(find==INVALID_HANDLE_VALUE)return FALSE;
    do {
        if (!CompatibleVersion(data.cFileName)) continue;
        unsigned a,b,patch;swscanf(data.cFileName,L"%u.%u.%u",&a,&b,&patch);
        if(!found || patch>best){best=patch;found=TRUE;StringCchPrintfW(library,ARRAYSIZE(library),L"%s\\host\\fxr\\%s\\hostfxr.dll",root,data.cFileName);}
    }while(FindNextFileW(find,&data));FindClose(find);
    if(!found)return FALSE;
    Join(config,ARRAYSIZE(config),appDirectory,L"CMUmcl.runtimeconfig.json");if(!FileExists(config))return FALSE;
    HMODULE module=LoadLibraryExW(library,NULL,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);if(!module)return FALSE;
    HostInitialize initialize=(HostInitialize)GetProcAddress(module,"hostfxr_initialize_for_runtime_config");
    HostClose close=(HostClose)GetProcAddress(module,"hostfxr_close");
    HostSetErrorWriter setError=(HostSetErrorWriter)GetProcAddress(module,"hostfxr_set_error_writer");
    HostErrorWriter previous=setError?setError(IgnoreHostError):NULL;
    HostParameters parameters={sizeof(parameters),NULL,root};void *context=NULL;
    BOOL ok=initialize && close && initialize(config,&parameters,&context)>=0 && context;
    if(context && close)close(context);if(setError)setError(previous);FreeLibrary(module);return ok;
}
static BOOL TryRoot(const WCHAR *root) {
    if (!root || !root[0]) return FALSE;
    WCHAR exe[MAX_PATH]; Join(exe, ARRAYSIZE(exe), root, L"dotnet.exe");
    if (!IsX64Executable(exe) || !HasFramework(root, L"Microsoft.NETCore.App", L"coreclr.dll") ||
        !HasFramework(root, L"Microsoft.WindowsDesktop.App", L"PresentationFramework.dll") || !ResolveFrameworks(root)) return FALSE;
    StringCchCopyW(runtimeRoot, ARRAYSIZE(runtimeRoot), root); return TRUE;
}
static BOOL FindRuntime(void) {
    WCHAR path[MAX_PATH], base[MAX_PATH]; runtimeRoot[0] = 0;
    // App-local, explicit environment, official machine registration, then standard install location.
    Join(path, ARRAYSIZE(path), appDirectory, L"runtime"); if (TryRoot(path)) return TRUE;
    DWORD n = GetEnvironmentVariableW(L"DOTNET_ROOT_X64", path, ARRAYSIZE(path));
    if (n && n < ARRAYSIZE(path) && TryRoot(path)) return TRUE;
    n = GetEnvironmentVariableW(L"DOTNET_ROOT", path, ARRAYSIZE(path));
    if (n && n < ARRAYSIZE(path) && TryRoot(path)) return TRUE;
    for (int i = 0; i < 2; i++) {
        DWORD bytes = sizeof(path);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64", L"InstallLocation",
                RRF_RT_REG_SZ | (i == 0 ? RRF_SUBKEY_WOW6464KEY : RRF_SUBKEY_WOW6432KEY), NULL, path, &bytes) == ERROR_SUCCESS && TryRoot(path)) return TRUE;
    }
    n = GetEnvironmentVariableW(L"ProgramW6432", base, ARRAYSIZE(base));
    if (!n) n = GetEnvironmentVariableW(L"ProgramFiles", base, ARRAYSIZE(base));
    if (n && n < ARRAYSIZE(base)) { Join(path, ARRAYSIZE(path), base, L"dotnet"); if (TryRoot(path)) return TRUE; }
    return FALSE;
}
static BOOL VerifyHash(const WCHAR *path, const WCHAR *expected) {
    BCRYPT_ALG_HANDLE algorithm = NULL; BCRYPT_HASH_HANDLE hash = NULL;
    HANDLE file = INVALID_HANDLE_VALUE; BYTE buffer[65536], digest[64]; WCHAR hex[129]; BOOL ok = FALSE; DWORD read;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA512_ALGORITHM, NULL, 0) < 0 ||
        BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0) goto done;
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE) goto done;
    for (;;) {
        if (!ReadFile(file, buffer, sizeof(buffer), &read, NULL)) goto done;
        if (!read) break;
        if (InterlockedCompareExchange(&canceled, 0, 0) || BCryptHashData(hash, buffer, read, 0) < 0) goto done;
    }
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    for (int i = 0; i < 64; i++) StringCchPrintfW(hex + i * 2, 3, L"%02x", digest[i]);
    ok = _wcsicmp(hex, expected) == 0;
done:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}
static BOOL VerifyMicrosoftSignature(const WCHAR *path) {
    WINTRUST_FILE_INFO info = {0}; info.cbStruct = sizeof(info); info.pcwszFilePath = path;
    WINTRUST_DATA trust = {0}; trust.cbStruct = sizeof(trust); trust.dwUIChoice = WTD_UI_NONE;
    trust.fdwRevocationChecks = WTD_REVOKE_NONE; trust.dwUnionChoice = WTD_CHOICE_FILE;
    trust.pFile = &info; trust.dwStateAction = WTD_STATEACTION_VERIFY;
    GUID policy = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    BOOL ok = WinVerifyTrust(NULL, &policy, &trust) == ERROR_SUCCESS;
    if (ok) {
        CRYPT_PROVIDER_DATA *provider = WTHelperProvDataFromStateData(trust.hWVTStateData);
        CRYPT_PROVIDER_SGNR *signer = provider ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : NULL;
        WCHAR name[256] = L"";
        if (!signer || !signer->csCertChain) ok = FALSE;
        else {
            CertGetNameStringW(signer->pasCertChain[0].pCert, CERT_NAME_ATTR_TYPE, 0, szOID_ORGANIZATION_NAME, name, ARRAYSIZE(name));
            ok = wcscmp(name, L"Microsoft Corporation") == 0;
        }
    }
    trust.dwStateAction = WTD_STATEACTION_CLOSE; WinVerifyTrust(NULL, &policy, &trust); return ok;
}
static BOOL Download(const WCHAR *path) {
    HINTERNET session = NULL, connection = NULL, request = NULL; HANDLE file = INVALID_HANDLE_VALUE;
    BOOL ok = FALSE; DWORD error = 0, read, written, httpStatus, length = sizeof(httpStatus), redirects = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    BYTE buffer[65536]; ULONGLONG total = 0, expected = 0; WCHAR host[256], urlPath[1024], contentLength[64];
    URL_COMPONENTS url = {0}; url.dwStructSize = sizeof(url); url.lpszHostName = host; url.dwHostNameLength = ARRAYSIZE(host);
    url.lpszUrlPath = urlPath; url.dwUrlPathLength = ARRAYSIZE(urlPath);
    if (!WinHttpCrackUrl(RUNTIME_URL, 0, 0, &url) || url.nScheme != INTERNET_SCHEME_HTTPS) goto done;
    session = WinHttpOpen(L"CMUmcl-Bootstrap/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) goto done;
    WinHttpSetTimeouts(session, 15000, 15000, 30000, 15000);
    connection = WinHttpConnect(session, host, url.nPort, 0); if (!connection) goto done;
    request = WinHttpOpenRequest(connection, L"GET", urlPath, NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (!request || !WinHttpSetOption(request, WINHTTP_OPTION_REDIRECT_POLICY, &redirects, sizeof(redirects))) goto done;
    if (!WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) || !WinHttpReceiveResponse(request, NULL)) goto done;
    if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, NULL, &httpStatus, &length, NULL) || httpStatus != 200) {
        Status(L"下载失败，请检查网络连接后重试，也可以选择手动下载。", 0); goto cleanup;
    }
    length = sizeof(contentLength);
    if (WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_LENGTH, NULL, contentLength, &length, NULL)) expected = _wcstoui64(contentLength, NULL, 10);
    if (expected > MAX_DOWNLOAD) { Status(L"安装包大小异常，已停止下载。", 0); goto cleanup; }
    file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL); if (file == INVALID_HANDLE_VALUE) goto done;
    for (;;) {
        if (InterlockedCompareExchange(&canceled, 0, 0)) goto cleanup;
        if (!WinHttpReadData(request, buffer, sizeof(buffer), &read)) goto done;
        if (!read) break;
        total += read;
        if (total > MAX_DOWNLOAD) { Status(L"安装包超过允许大小，已停止下载。", 0); goto cleanup; }
        if (!WriteFile(file, buffer, read, &written, NULL) || read != written) goto done;
        WCHAR message[256];
        if (expected) StringCchPrintfW(message, ARRAYSIZE(message), L"正在下载运行组件… %.1f / %.1f MB", total / 1048576.0, expected / 1048576.0);
        else StringCchPrintfW(message, ARRAYSIZE(message), L"正在下载运行组件… %.1f MB", total / 1048576.0);
        Status(message, expected ? (int)(total * 100 / expected) : 0);
    }
    ok = total > 0 && (!expected || total == expected);
    if (!ok) Status(L"下载失败，请检查网络连接后重试，也可以选择手动下载。", 0);
    goto cleanup;
done:
    error = GetLastError(); (void)error; Status(L"下载失败，请检查网络连接后重试，也可以选择手动下载。", 0);
cleanup:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (request) WinHttpCloseHandle(request); if (connection) WinHttpCloseHandle(connection); if (session) WinHttpCloseHandle(session);
    return ok;
}
static BOOL LaunchClient(void) {
    WCHAR executable[MAX_PATH], arguments[4096] = L"", config[MAX_PATH], folder[MAX_PATH];
    StringCchCopyW(folder, ARRAYSIZE(folder), appDirectory);
    if (preview) {
        Join(config, ARRAYSIZE(config), appDirectory, L"preview.ini");
        GetPrivateProfileStringW(L"Preview", L"Executable", L"", executable, ARRAYSIZE(executable), config);
        GetPrivateProfileStringW(L"Preview", L"Arguments", L"", arguments, ARRAYSIZE(arguments), config);
        GetPrivateProfileStringW(L"Preview", L"Runtime", L"", runtimeRoot, ARRAYSIZE(runtimeRoot), config);
        GetPrivateProfileStringW(L"Preview", L"WorkingDirectory", appDirectory, folder, ARRAYSIZE(folder), config);
    } else Join(executable, ARRAYSIZE(executable), appDirectory, L"CMUmcl.exe");
    if (!FileExists(executable)) { Status(L"未找到客户端，请将启动引导器与完整客户端放在一起。", 0); return FALSE; }
    // Process-local only. This also reaches the update helper started by the client.
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", runtimeRoot); SetEnvironmentVariableW(L"DOTNET_ROOT", runtimeRoot);
    WCHAR command[8192]; StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" %s", executable, arguments);
    STARTUPINFOW startup = {0}; startup.cb = sizeof(startup); PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, folder, &startup, &process)) { WinError(L"启动客户端失败", GetLastError()); return FALSE; }
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return TRUE;
}
static DWORD RunInstall(const WCHAR *path) {
    SHELLEXECUTEINFOW start = {0}; start.cbSize = sizeof(start); start.fMask = SEE_MASK_NOCLOSEPROCESS;
    start.hwnd = mainWindow; start.lpVerb = L"runas"; start.lpFile = path;
    start.lpParameters = L"/install /passive /norestart"; start.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&start)) return GetLastError();
    if (!start.hProcess) return ERROR_INVALID_HANDLE;
    WaitForSingleObject(start.hProcess, INFINITE); DWORD code = 1;
    GetExitCodeProcess(start.hProcess, &code); CloseHandle(start.hProcess); return code;
}
static DWORD WINAPI Worker(void *unused) {
    WCHAR temp[MAX_PATH], directory[MAX_PATH], installer[MAX_PATH]; BOOL success = FALSE; HANDLE lock = INVALID_HANDLE_VALUE;
    (void)unused;
    if (preview) {
        for (int i = 0; i <= 100; i += 2) {
            if (InterlockedCompareExchange(&canceled, 0, 0) == 1) goto finish;
            if (i == 80) {
                if (InterlockedCompareExchange(&canceled, -1, 0) != 0) goto finish;
                InterlockedExchange(&installing, 1);
            }
            Status(i < 60 ? L"正在下载运行组件…" : i < 80 ? L"正在检查安装包…" : L"请允许 Windows 安装提示，并等待安装完成。", i);
            Sleep(90);
            if (demoFailure && i == 40) { demoFailure = 0; Status(L"下载失败，请检查网络连接后重试，也可以选择手动下载。", 0); goto finish; }
        }
        InterlockedExchange(&installing, 0); InterlockedExchange(&canceled, 0);
        success = TRUE; goto finish;
    }
    if (!GetTempPathW(ARRAYSIZE(temp), temp) || !GetTempFileNameW(temp, L"cmu", 0, directory)) { WinError(L"无法创建下载目录", GetLastError()); goto finish; }
    DeleteFileW(directory);
    if (!CreateDirectoryW(directory, NULL)) { WinError(L"无法创建下载目录", GetLastError()); goto finish; }
    Join(installer, ARRAYSIZE(installer), directory, L"windowsdesktop-runtime.exe");
    Status(L"正在下载运行组件…", 0);
    if (!Download(installer)) goto cleanup;
    // Deny writes/deletion from verification until the installer has exited.
    lock = CreateFileW(installer, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (lock == INVALID_HANDLE_VALUE) { WinError(L"无法读取安装包", GetLastError()); goto cleanup; }
    Status(L"正在检查安装包…", 100);
    if (!VerifyHash(installer, RUNTIME_SHA512) || !VerifyMicrosoftSignature(installer)) { Status(L"安装包验证失败，请重新下载。", 0); goto cleanup; }
    // Atomically commit to installation: a simultaneous Cancel must win or lose deterministically.
    if (InterlockedCompareExchange(&canceled, -1, 0) != 0) goto cleanup;
    InterlockedExchange(&installing, 1);
    Status(L"请允许 Windows 安装提示，并等待安装完成。", 100);
    installExit = RunInstall(installer);
    InterlockedExchange(&installing, 0);
    InterlockedExchange(&canceled, 0);
    if (installExit != ERROR_SUCCESS && installExit != ERROR_SUCCESS_REBOOT_REQUIRED) {
        if (installExit == ERROR_CANCELLED || installExit == ERROR_INSTALL_USEREXIT) Status(L"已取消安装。安装运行组件后才能使用启动器。", 0);
        else WinError(L"运行库安装未完成", installExit);
        goto cleanup;
    }
    success = FindRuntime();
    if (!success) Status(installExit == ERROR_SUCCESS_REBOOT_REQUIRED ? L"安装完成，请重启电脑后重新打开启动器。" : L"安装结束，但尚未检测到所需运行库。请重试或手动安装 Desktop Runtime 10 x64。", 0);
cleanup:
    if (lock != INVALID_HANDLE_VALUE) CloseHandle(lock);
    DeleteFileW(installer); RemoveDirectoryW(directory);
finish:
    PostMessageW(mainWindow, FINISHED, success, 0); return 0;
}
static HWND Control(const WCHAR *type, const WCHAR *text, DWORD style, int x, int y, int w, int h, int id) {
    HWND control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h, mainWindow, (HMENU)(INT_PTR)id, GetModuleHandleW(NULL), NULL);
    SendMessageW(control, WM_SETFONT, (WPARAM)regularFont, TRUE); return control;
}
static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wp, LPARAM lp) {
    switch (message) {
    case WM_CREATE:
        mainWindow = window;
        regularFont = CreateFontW(-17,0,0,0,FW_NORMAL,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
        headingFont = CreateFontW(-25,0,0,0,FW_SEMIBOLD,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
        smallFont = CreateFontW(-14,0,0,0,FW_NORMAL,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
        titleLabel = Control(L"STATIC", L"准备运行环境", 0,32,26,540,38,0);
        SendMessageW(titleLabel, WM_SETFONT, (WPARAM)headingFont, TRUE);
        detailLabel = Control(L"STATIC", L"启动器需要 Microsoft .NET Desktop Runtime 10 (x64) ，将从微软官方下载并安装。", 0,32,84,552,60,0);
        progressBar = Control(PROGRESS_CLASSW, L"", PBS_SMOOTH,32,162,552,8,0);
        SendMessageW(progressBar, PBM_SETRANGE32,0,100);
        statusLabel = Control(L"STATIC", preview ? L"预览：不会下载或安装组件。\n未检测到兼容运行库。安装时 Windows 可能请求管理员确认。" : L"未检测到兼容运行库。安装时 Windows 可能请求管理员确认。", 0,32,188,552,58,0);
        HWND website = Control(L"BUTTON", L"手动下载", BS_PUSHBUTTON | WS_TABSTOP,32,266,146,38,ID_WEB);
        SendMessageW(website, WM_SETFONT, (WPARAM)smallFont, TRUE);
        cancelButton = Control(L"BUTTON", L"退出", BS_PUSHBUTTON | WS_TABSTOP,316,266,100,38,ID_CANCEL);
        primary = Control(L"BUTTON", L"下载并安装", BS_DEFPUSHBUTTON | WS_TABSTOP,430,266,154,38,ID_START);
        return 0;
    case UPDATE_UI:
        EnterCriticalSection(&statusLock);
        SetWindowTextW(statusLabel, statusText); SendMessageW(progressBar, PBM_SETPOS, percent, 0);
        LeaveCriticalSection(&statusLock);
        EnableWindow(cancelButton, !InterlockedCompareExchange(&installing,0,0)); return 0;
    case FINISHED:
        busy = 0; EnableWindow(primary, TRUE); EnableWindow(cancelButton, TRUE); SetWindowTextW(cancelButton,L"退出");
        if (wp) {
            ready = 1;
            SetWindowTextW(primary, L"打开启动器");
            Status(L"安装完成，正在打开启动器…",100);
            SendMessageW(window, UPDATE_UI, 0, 0);
            if (LaunchClient()) DestroyWindow(window);
        } else {
            SetWindowTextW(primary,L"重试");
            if (InterlockedCompareExchange(&canceled,0,0)==1) Status(L"已取消安装。安装运行组件后才能使用启动器。",0);
        }
        return 0;
    case WM_COMMAND:
        if (LOWORD(wp) == ID_WEB) { ShellExecuteW(window,L"open",L"https://dotnet.microsoft.com/download/dotnet/10.0",NULL,NULL,SW_SHOWNORMAL); return 0; }
        if (LOWORD(wp) == ID_START && !busy) {
            if (ready) { if (LaunchClient()) DestroyWindow(window); return 0; }
            if (!preview && FindRuntime()) { if (LaunchClient()) DestroyWindow(window); return 0; }
            busy = 1; InterlockedExchange(&canceled,0); EnableWindow(primary,FALSE); SetWindowTextW(cancelButton,L"取消");
            HANDLE thread = CreateThread(NULL,0,Worker,NULL,0,NULL);
            if (thread) CloseHandle(thread); else { busy=0; EnableWindow(primary,TRUE); WinError(L"无法开始下载",GetLastError()); }
            return 0;
        }
        if (LOWORD(wp) == ID_CANCEL) SendMessageW(window,WM_CLOSE,0,0);
        return 0;
    case WM_CLOSE:
        if (busy) {
            LONG previous = InterlockedCompareExchange(&canceled,1,0);
            if (previous >= 0) { Status(L"正在取消下载，请稍候…",0); EnableWindow(cancelButton,FALSE); }
            return 0;
        }
        DestroyWindow(window); return 0;
    case WM_CTLCOLORSTATIC:
        SetBkMode((HDC)wp,TRANSPARENT); SetTextColor((HDC)wp,RGB(45,45,50)); return (LRESULT)whiteBrush;
    case WM_DESTROY:
        DeleteObject(regularFont); DeleteObject(headingFont); DeleteObject(smallFont); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window,message,wp,lp);
}
static int SelfTest(void) {
    if (!CompatibleVersion(L"10.0.0") || !CompatibleVersion(L"10.0.14") || CompatibleVersion(L"10.0.14-preview.1") || CompatibleVersion(L"11.0.0") || CompatibleVersion(L"10.1.0") || CompatibleVersion(L"garbage")) return 1;
    WCHAR executable[MAX_PATH]; GetModuleFileNameW(NULL,executable,ARRAYSIZE(executable));
    if (!IsX64Executable(executable) || VerifyHash(executable,RUNTIME_SHA512) || VerifyMicrosoftSignature(executable)) return 2;
    wprintf(L"PASS version filtering, architecture check, hash rejection, unsigned-file rejection\n"); return 0;
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command, int show) {
    (void)previous; (void)command; (void)show;
    InitializeCriticalSection(&statusLock);
    GetModuleFileNameW(NULL,appDirectory,ARRAYSIZE(appDirectory)); WCHAR *slash=wcsrchr(appDirectory,L'\\'); if(slash)*slash=0;
    int argc; LPWSTR *argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    for(int i=1;i<argc;i++) {
        if(!wcscmp(argv[i],L"--preview"))preview=1;
        else if(!wcscmp(argv[i],L"--preview-failure")){preview=1;demoFailure=1;}
        else if(!wcscmp(argv[i],L"--self-test"))return SelfTest();
        else if(!wcscmp(argv[i],L"--probe")){BOOL found=FindRuntime();wprintf(L"desktop10_x64=%ls\nroot=%ls\n",found?L"ready":L"missing",runtimeRoot);return found?0:3;}
        else if(!wcscmp(argv[i],L"--verify-file") && i+1<argc){BOOL ok=VerifyHash(argv[++i],RUNTIME_SHA512)&&VerifyMicrosoftSignature(argv[i]);wprintf(L"verified=%d\n",ok);return ok?0:4;}
        else if(!wcscmp(argv[i],L"--download-check") && i+1<argc){const WCHAR *path=argv[++i];BOOL ok=Download(path)&&VerifyHash(path,RUNTIME_SHA512)&&VerifyMicrosoftSignature(path);wprintf(L"download_verified=%d\n",ok);return ok?0:5;}
    }
    LocalFree(argv);
    WCHAR mutexName[128]; DWORD pathHash=2166136261u;for(const WCHAR *p=appDirectory;*p;p++)pathHash=(pathHash^(DWORD)*p)*16777619u;
    StringCchPrintfW(mutexName,ARRAYSIZE(mutexName),L"Local\\CMUmclBootstrap_%08lx_%d",pathHash,preview);
    HANDLE mutex=CreateMutexW(NULL,FALSE,mutexName);
    if(GetLastError()==ERROR_ALREADY_EXISTS){MessageBoxW(NULL,L"启动引导器已打开，请切换到已有窗口。",L"CMUmcl",MB_OK);return 0;}
    if (!preview) {
        WCHAR client[MAX_PATH],config[MAX_PATH];Join(client,ARRAYSIZE(client),appDirectory,L"CMUmcl.exe");Join(config,ARRAYSIZE(config),appDirectory,L"CMUmcl.runtimeconfig.json");
        if(!FileExists(client)||!FileExists(config)){
            MessageBoxW(NULL,L"客户端文件不完整。请先完整解压启动器，再运行 CMUmcl.Start.exe。",L"CMUmcl",MB_OK|MB_ICONERROR);
            if(mutex)CloseHandle(mutex);return 6;
        }
    }
    if(!preview && FindRuntime() && LaunchClient()){if(mutex)CloseHandle(mutex);return 0;}
    INITCOMMONCONTROLSEX controls={sizeof(controls),ICC_PROGRESS_CLASS|ICC_STANDARD_CLASSES};InitCommonControlsEx(&controls);
    whiteBrush=CreateSolidBrush(RGB(255,255,255));
    WNDCLASSW cls={0};cls.lpfnWndProc=WindowProc;cls.hInstance=instance;cls.lpszClassName=L"CMUmclDependencyBootstrap";
    cls.hCursor=LoadCursorW(NULL,IDC_ARROW);cls.hbrBackground=whiteBrush;cls.hIcon=LoadIconW(instance,MAKEINTRESOURCEW(1));RegisterClassW(&cls);
    RECT bounds={0,0,616,336};AdjustWindowRectEx(&bounds,WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX,FALSE,0);
    int width=bounds.right-bounds.left,height=bounds.bottom-bounds.top;
    HWND window=CreateWindowExW(0,cls.lpszClassName,L"CMUmcl",
        WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX,(GetSystemMetrics(SM_CXSCREEN)-width)/2,(GetSystemMetrics(SM_CYSCREEN)-height)/2,width,height,NULL,NULL,instance,NULL);
    if(!window)return 1;ShowWindow(window,SW_SHOWNORMAL);UpdateWindow(window);
    MSG msg;while(GetMessageW(&msg,NULL,0,0)>0){if(!IsDialogMessageW(window,&msg)){TranslateMessage(&msg);DispatchMessageW(&msg);}}
    if(mutex)CloseHandle(mutex);DeleteObject(whiteBrush);DeleteCriticalSection(&statusLock);return 0;
}
