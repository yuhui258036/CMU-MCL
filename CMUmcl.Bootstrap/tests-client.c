#define UNICODE
#include <windows.h>
#include <wchar.h>
// Tiny owned fixture: records launch arguments/runtime without opening UI or contacting any service.
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE previous,PWSTR command,int show) {
    (void)instance;(void)previous;(void)command;(void)show;
    wchar_t path[1024],root[1024];GetModuleFileNameW(NULL,path,1024);
    wchar_t *slash=wcsrchr(path,L'\\');if(slash)*slash=0;
    wcscat(path,L"\\started.txt");GetEnvironmentVariableW(L"DOTNET_ROOT_X64",root,1024);
    HANDLE file=CreateFileW(path,GENERIC_WRITE,0,NULL,CREATE_ALWAYS,0,NULL);if(file==INVALID_HANDLE_VALUE)return 1;
    DWORD bytes;WriteFile(file,root,(DWORD)(wcslen(root)*2),&bytes,NULL);CloseHandle(file);return 0;
}
