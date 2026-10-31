// In-process component tests for the bootstrap's own controls/state machine.
// Preview mode never downloads, elevates, or invokes an installer.
#define wWinMain BootstrapEntry
#include "bootstrap.c"
#undef wWinMain
static int checks;
#define CHECK(expression, label) do { if(!(expression)){fprintf(stderr,"FAIL: %s\n",label);return 1;}checks++;printf("PASS: %s\n",label);fflush(stdout); }while(0)
static BOOL ContainsStatus(const WCHAR *part) {
    WCHAR text[1024];GetWindowTextW(statusLabel,text,ARRAYSIZE(text));return wcsstr(text,part)!=NULL;
}
static BOOL PumpUntilIdle(DWORD timeout) {
    ULONGLONG start=GetTickCount64();MSG message;
    do {
        while(PeekMessageW(&message,NULL,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
        if(!busy)return TRUE;Sleep(10);
    }while(GetTickCount64()-start<timeout);
    return FALSE;
}
int wmain(int argc,WCHAR **argv) {
    CHECK(argc==4,"fixture arguments supplied");
    InitializeCriticalSection(&statusLock);
    GetModuleFileNameW(NULL,appDirectory,ARRAYSIZE(appDirectory));*wcsrchr(appDirectory,L'\\')=0;
    CHECK(SelfTest()==0,"native version/hash/signature self-tests");
    CHECK(TryRoot(argv[1]),"actual Microsoft host resolves both required frameworks");
    CHECK(!TryRoot(L"Z:\\nonexistent-runtime"),"missing runtime rejected");
    CHECK(VerifyHash(argv[2],RUNTIME_SHA512),"official installer SHA-512 accepted");
    CHECK(VerifyMicrosoftSignature(argv[2]),"official Microsoft organization signature accepted");
    CHECK(!VerifyHash(argv[3],RUNTIME_SHA512),"tampered installer hash rejected");
    CHECK(!VerifyMicrosoftSignature(argv[3]),"tampered installer signature rejected");
    preview=1;
    INITCOMMONCONTROLSEX controls={sizeof(controls),ICC_PROGRESS_CLASS|ICC_STANDARD_CLASSES};InitCommonControlsEx(&controls);
    whiteBrush=CreateSolidBrush(RGB(255,255,255));WNDCLASSW cls={0};cls.hInstance=GetModuleHandleW(NULL);cls.lpfnWndProc=WindowProc;cls.lpszClassName=L"BootstrapComponentTests";cls.hbrBackground=whiteBrush;RegisterClassW(&cls);
    mainWindow=CreateWindowExW(0,cls.lpszClassName,L"Bootstrap component tests",WS_OVERLAPPED,0,0,640,420,NULL,NULL,cls.hInstance,NULL);
    CHECK(mainWindow!=NULL && IsWindow(primary),"native view and controls created without CLR");
    CHECK(ContainsStatus(L"不会下载或安装"),"preview clearly states no installation");
    SendMessageW(mainWindow,WM_COMMAND,ID_CANCEL,0);
    CHECK(!IsWindow(mainWindow),"Exit before download closes bootstrap immediately");
    mainWindow=CreateWindowExW(0,cls.lpszClassName,L"Bootstrap component tests",WS_OVERLAPPED,0,0,640,376,NULL,NULL,cls.hInstance,NULL);
    SendMessageW(mainWindow,WM_COMMAND,ID_START,0);
    CHECK(busy && !IsWindowEnabled(primary),"starting disables duplicate requests");
    WCHAR buttonText[32];GetWindowTextW(cancelButton,buttonText,ARRAYSIZE(buttonText));
    CHECK(!wcscmp(buttonText,L"取消"),"Exit button becomes Cancel while downloading");
    SendMessageW(mainWindow,WM_CLOSE,0,0);
    CHECK(PumpUntilIdle(5000) && !ready && ContainsStatus(L"已取消"),"cancel ends simulation without installing");
    demoFailure=1;SendMessageW(mainWindow,WM_COMMAND,ID_START,0);
    CHECK(PumpUntilIdle(8000) && !ready && ContainsStatus(L"下载失败"),"failure leaves retryable state");
    GetWindowTextW(cancelButton,buttonText,ARRAYSIZE(buttonText));
    CHECK(!wcscmp(buttonText,L"退出"),"failed or canceled download restores Exit");
    // Boundary race: installation claim cannot be overwritten by a late Cancel.
    busy=1;InterlockedExchange(&canceled,-1);SendMessageW(mainWindow,WM_CLOSE,0,0);
    CHECK(canceled==-1 && IsWindow(mainWindow),"late cancel cannot interrupt committed installation");
    busy=0;SendMessageW(mainWindow,WM_COMMAND,ID_START,0);
    ULONGLONG start=GetTickCount64();MSG event;
    while(!InterlockedCompareExchange(&installing,0,0) && GetTickCount64()-start<8000) {
        while(PeekMessageW(&event,NULL,0,0,PM_REMOVE)){TranslateMessage(&event);DispatchMessageW(&event);}
        Sleep(10);
    }
    while(PeekMessageW(&event,NULL,0,0,PM_REMOVE)){TranslateMessage(&event);DispatchMessageW(&event);}
    CHECK(installing && !IsWindowEnabled(cancelButton),"Cancel is disabled once simulated installation begins");
    CHECK(PumpUntilIdle(8000) && ready && !IsWindow(mainWindow),"completion automatically starts isolated client and closes bootstrap");
    DeleteObject(whiteBrush);DeleteCriticalSection(&statusLock);
    printf("PASS %d bootstrap component checks\n",checks);return 0;
}
