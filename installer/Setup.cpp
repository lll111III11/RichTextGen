// 彩色文本生成器 安装程序（原生 C++ / 零依赖）
// ---------------------------------------------------------------------------
// 特性
//   · 纯 Win32 原生程序：不依赖 .NET Framework、不依赖 WebView2 运行时，
//     只链接系统自带 DLL（user32/gdi32/shell32/ole32/gdiplus/comctl32/advapi32）。
//   · 载荷（程序 exe/config + WebView2 三个组件）由 gen_embedded.ps1 转成字节数组
//     内嵌进本 exe，安装时逐个释放，文件名原样保留（含中文名与多段名）。
//   · 界面：经典浅色 Windows 安装程序样式（白色标题区 + 浅灰主体 + 标准控件）；
//     Win11 22H2+ 可选系统级毛玻璃（亚克力）。
//   · 静默安装：RichTextGen-Setup.exe --silent "D:\路径"
//   · 卸载：     RichTextGen-Setup.exe /uninstall   （或安装目录下 Uninstall.exe /uninstall）
// ---------------------------------------------------------------------------
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00

#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <objbase.h>
#include <shlwapi.h>
#include <gdiplus.h>
#include <tlhelp32.h>
#include <string>
#include <vector>
#include <cmath>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shlwapi.lib")

using namespace Gdiplus;

// 载荷数据（由 gen_embedded.ps1 生成）
#include "embedded.h"

namespace
{
    const wchar_t* const kAppName   = L"彩色文本生成器";
    const wchar_t* const kProduct   = L"Rich text & multifunctional tool";
    const wchar_t* const kPublisher = L"3576220975@qq.com";
    const wchar_t* const kRegKey    = L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\RichTextGen";
    const wchar_t* const kVersion   = L"5.8.0.4";
    const wchar_t* const kAppExe    = L"彩色文本生成器.exe";   // 卸载前需要结束的主程序进程名

    // ---- MER 地图编辑器（Alpha，可选组件）----
    const wchar_t* const kMerName     = L"MER 地图编辑器（Alpha）";
    const wchar_t* const kMerFolder   = L"MER-MapEditor(Alpha)";     // 装到 <本程序目录>\ 这个子目录
    const wchar_t* const kMerSubExe   = L"SCPSL-MER.exe";
    const wchar_t* const kMerRegKey   = L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SCPSL-MER";

    // ---- 控件 ID ----
    enum
    {
        IDC_EDIT_PATH = 101,
        IDC_BTN_BROWSE = 102,
        IDC_CHK_DESKTOP = 103,
        IDC_CHK_START = 104,
        IDC_CHK_RUN = 105,
        IDC_CHK_GLASS = 106,
        IDC_BTN_INSTALL = 107,
        IDC_BTN_CANCEL = 108,
        IDC_PROGRESS = 109,
        IDC_STATUS = 110,
        IDC_EDIT_LICENSE = 111,
        IDC_LBL_PATH = 112,
        IDC_CHK_MER = 113,
        IDC_BTN_MER = 114,
        IDC_ANIM = 1
    };

    // 窗口设计尺寸（96 DPI 逻辑像素）
    const int kWinW = 720;
    const int kWinH = 500;

    // DWM 扩展边框结构（dwmapi.h 里也有，这里手动声明以便动态加载 dwmapi）
    struct MARGINS { int cxLeftWidth; int cxRightWidth; int cyTopHeight; int cyBottomHeight; };

    ULONG_PTR g_gdiplusToken = 0;
    HINSTANCE g_hInst = NULL;

    // 动画状态
    bool g_glass = false;    // 毛玻璃是否生效
    bool g_installing = false;
    // DPI 缩放：窗口、控件与字号按屏幕 DPI 等比放大，避免高 DPI 下界面过小（96 DPI = 1.0）
    double g_scale = 1.0;
    int S(int v) { return (int)(v * g_scale + 0.5); }

    // 标题栏按钮矩形（客户区坐标）
    const RECT kBtnMin = { 630, 0, 672, 40 };
    const RECT kBtnClose = { 672, 0, 720, 40 };

    // 调色盘
    Color Palette(int i)
    {
        static const Color p[6] = {
            Color(0x0B, 0x2A, 0x5B), Color(0x00, 0x67, 0xC0),
            Color(0x2F, 0xA8, 0xFF), Color(0x0A, 0x2B, 0x55),
            Color(0x6C, 0xD4, 0xFF), Color(0x00, 0x3A, 0x8C)
        };
        return p[((i % 6) + 6) % 6];
    }

    Color Mix(Color a, Color b, double t)
    {
        if (t < 0) t = 0; if (t > 1) t = 1;
        return Color(
            (BYTE)(a.GetA() + (b.GetA() - a.GetA()) * t),
            (BYTE)(a.GetR() + (b.GetR() - a.GetR()) * t),
            (BYTE)(a.GetG() + (b.GetG() - a.GetG()) * t),
            (BYTE)(a.GetB() + (b.GetB() - a.GetB()) * t));
    }

    std::wstring GetDefaultDir()
    {
        wchar_t buf[MAX_PATH] = { 0 };
        if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, buf)))
            return std::wstring(buf) + L"\\Programs\\RichTextGen";
        return L"C:\\RichTextGen";
    }

    std::wstring SelfPath()
    {
        wchar_t buf[MAX_PATH] = { 0 };
        GetModuleFileNameW(NULL, buf, MAX_PATH);
        return buf;
    }

    std::wstring SelfDir()
    {
        std::wstring p = SelfPath();
        size_t pos = p.find_last_of(L"\\/");
        return (pos == std::wstring::npos) ? L"" : p.substr(0, pos);
    }

    // 系统版本号（判断是否支持 DWM 系统背景材质）
    int BuildNumber()
    {
        HKEY k = NULL;
        int n = 0;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", 0, KEY_READ, &k) == ERROR_SUCCESS)
        {
            wchar_t buf[16] = { 0 };
            DWORD sz = sizeof(buf);
            if (RegQueryValueExW(k, L"CurrentBuildNumber", NULL, NULL, (BYTE*)buf, &sz) == ERROR_SUCCESS)
                n = _wtoi(buf);
            RegCloseKey(k);
        }
        return n;
    }
    bool BackdropSupported() { return BuildNumber() >= 22621; }

    void RoundCorners(HWND hwnd)
    {
        typedef HRESULT(WINAPI* Fn)(HWND, DWORD, LPCVOID, DWORD);
        HMODULE m = GetModuleHandleW(L"dwmapi.dll");
        if (!m) return;
        Fn fn = (Fn)GetProcAddress(m, "DwmSetWindowAttribute");
        if (fn) { int v = 2; fn(hwnd, 33, &v, 4); }
    }

    // 亚克力（毛玻璃）开关；返回是否成功开启
    bool Acrylic(HWND hwnd, bool on)
    {
        typedef HRESULT(WINAPI* Fn)(HWND, DWORD, LPCVOID, DWORD);
        HMODULE m = GetModuleHandleW(L"dwmapi.dll");
        if (!m) return false;
        Fn fn = (Fn)GetProcAddress(m, "DwmSetWindowAttribute");
        if (!fn) return false;
        int v = on ? 3 : 0;   // DWMSBT_TRANSIENTWINDOW / DWMSBT_AUTO
        if (fn(hwnd, 38, &v, 4) != 0) return false;
        // 扩展边框到整个客户区，让亚克力透出
        typedef HRESULT(WINAPI* FnExt)(HWND, const MARGINS*);
        FnExt ext = (FnExt)GetProcAddress(m, "DwmExtendFrameIntoClientArea");
        if (ext) { MARGINS mg = { -1, -1, -1, -1 }; ext(hwnd, &mg); }
        return on;
    }

    // ==================================================================== 安装核心
    void SetStatusText(HWND hwnd, const wchar_t* text, int pct)
    {
        SetDlgItemTextW(hwnd, IDC_STATUS, text);
        SendDlgItemMessageW(hwnd, IDC_PROGRESS, PBM_SETPOS, (WPARAM)pct, 0);
    }

    void PumpMessages()
    {
        MSG msg;
        while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
    }

    // 是否为本产品自己的文件（用于升级时清理旧版本，绝不误删用户文件）
    bool IsProductFile(const wchar_t* name)
    {
        for (unsigned long i = 0; i < kPayloadCount; i++)
            if (_wcsicmp(name, kPayload[i].name) == 0) return true;
        if (_wcsicmp(name, L"Uninstall.exe") == 0) return true;
        // 兼容更早的命名（RichTextGen.exe），避免升级后留下同名旧程序
        if (_wcsicmp(name, L"RichTextGen.exe") == 0) return true;
        if (_wcsicmp(name, L"RichTextGen.exe.config") == 0) return true;
        return false;
    }

    // 只删除本产品白名单内的旧文件。注意：不能无差别清空安装目录，
    // 否则用户若把安装位置选到桌面/文档等目录会丢失自己的文件。
    void DeleteOldFiles(const std::wstring& dir)
    {
        std::wstring self = SelfPath();
        std::wstring pat = dir + L"\\*";
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW(pat.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return;
        do
        {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            if (!IsProductFile(fd.cFileName)) continue;
            std::wstring full = dir + L"\\" + fd.cFileName;
            if (_wcsicmp(full.c_str(), self.c_str()) == 0) continue;
            DeleteFileW(full.c_str());
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }

    std::wstring PickMainExe(const std::wstring& dir)
    {
        std::wstring pat = dir + L"\\*.exe";
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW(pat.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return dir + L"\\RichTextGen.exe";
        do
        {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            if (_wcsicmp(fd.cFileName, L"Uninstall.exe") == 0) continue;
            if (wcsstr(fd.cFileName, L"Setup") || wcsstr(fd.cFileName, L"setup")) continue;
            FindClose(h);
            return dir + L"\\" + fd.cFileName;
        } while (FindNextFileW(h, &fd));
        FindClose(h);
        return dir + L"\\RichTextGen.exe";
    }

    void CreateShortcut(const std::wstring& lnkPath, const std::wstring& target, const std::wstring& workdir)
    {
        IShellLinkW* link = NULL;
        if (FAILED(CoCreateInstance(CLSID_ShellLink, NULL, CLSCTX_INPROC_SERVER, IID_IShellLinkW, (void**)&link))) return;
        link->SetPath(target.c_str());
        link->SetWorkingDirectory(workdir.c_str());
        link->SetDescription(kProduct);
        IPersistFile* pf = NULL;
        if (SUCCEEDED(link->QueryInterface(IID_IPersistFile, (void**)&pf)))
        {
            pf->Save(lnkPath.c_str(), TRUE);
            pf->Release();
        }
        link->Release();
    }

    void WriteUninstallInfo(const std::wstring& dir, const std::wstring& exe)
    {
        HKEY k = NULL;
        if (RegCreateKeyExW(HKEY_CURRENT_USER, kRegKey, 0, NULL, 0, KEY_WRITE, NULL, &k, NULL) != ERROR_SUCCESS) return;
        std::wstring dn = std::wstring(kAppName) + L" · " + kProduct;
        std::wstring un = dir + L"\\Uninstall.exe";
        std::wstring ustr = L"\"" + un + L"\" /uninstall";
        DWORD one = 1;
        RegSetValueExW(k, L"DisplayName", 0, REG_SZ, (const BYTE*)dn.c_str(), (DWORD)((dn.size() + 1) * 2));
        RegSetValueExW(k, L"DisplayVersion", 0, REG_SZ, (const BYTE*)kVersion, (DWORD)((wcslen(kVersion) + 1) * 2));
        RegSetValueExW(k, L"Publisher", 0, REG_SZ, (const BYTE*)kPublisher, (DWORD)((wcslen(kPublisher) + 1) * 2));
        RegSetValueExW(k, L"InstallLocation", 0, REG_SZ, (const BYTE*)dir.c_str(), (DWORD)((dir.size() + 1) * 2));
        RegSetValueExW(k, L"DisplayIcon", 0, REG_SZ, (const BYTE*)exe.c_str(), (DWORD)((exe.size() + 1) * 2));
        RegSetValueExW(k, L"UninstallString", 0, REG_SZ, (const BYTE*)ustr.c_str(), (DWORD)((ustr.size() + 1) * 2));
        RegSetValueExW(k, L"NoModify", 0, REG_DWORD, (const BYTE*)&one, 4);
        RegSetValueExW(k, L"NoRepair", 0, REG_DWORD, (const BYTE*)&one, 4);
        RegCloseKey(k);
    }

    // 安装主体。progress 可空（静默模式）。任何一步失败都返回 false，避免「半成品却提示完成」。
    bool Install(const std::wstring& target, bool desktop, bool startMenu,
                 void (*progress)(void*, int, const wchar_t*), void* ctx)
    {
        if (!CreateDirectoryW(target.c_str(), NULL) && GetLastError() != ERROR_ALREADY_EXISTS)
            return false;
        DeleteOldFiles(target);

        // 释放内嵌载荷：逐字节写入，写入不完整即视为失败
        for (unsigned long i = 0; i < kPayloadCount; i++)
        {
            std::wstring out = target + L"\\" + kPayload[i].name;
            HANDLE h = CreateFileW(out.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (h == INVALID_HANDLE_VALUE) return false;
            DWORD written = 0;
            BOOL ok = WriteFile(h, kPayload[i].data, kPayload[i].size, &written, NULL);
            CloseHandle(h);
            if (!ok || written != (DWORD)kPayload[i].size) return false;

            int pct = (int)(20 + (i + 1) * 70 / kPayloadCount);
            if (pct > 90) pct = 90;
            if (progress) progress(ctx, pct, kPayload[i].name);
        }

        // 复制自身为卸载程序
        std::wstring self = SelfPath();
        std::wstring un = target + L"\\Uninstall.exe";
        if (_wcsicmp(self.c_str(), un.c_str()) != 0 && !CopyFileW(self.c_str(), un.c_str(), FALSE))
            return false;

        std::wstring exe = PickMainExe(target);

        wchar_t desk[MAX_PATH] = { 0 }, menu[MAX_PATH] = { 0 };
        SHGetFolderPathW(NULL, CSIDL_DESKTOPDIRECTORY, NULL, 0, desk);
        SHGetFolderPathW(NULL, CSIDL_PROGRAMS, NULL, 0, menu);
        if (desktop) CreateShortcut(std::wstring(desk) + L"\\" + kAppName + L".lnk", exe, target);
        if (startMenu) CreateShortcut(std::wstring(menu) + L"\\" + kAppName + L".lnk", exe, target);

        WriteUninstallInfo(target, exe);

        if (progress) progress(ctx, 100, L"完成");
        return true;
    }

    // 递归删除目录内容，保留 exclude（绝对路径，卸载器自身）
    void DeleteTreeExcept(const std::wstring& dir, const std::wstring& exclude)
    {
        std::wstring pat = dir + L"\\*";
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW(pat.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return;
        do
        {
            if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
            std::wstring full = dir + L"\\" + fd.cFileName;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                DeleteTreeExcept(full, exclude);
                RemoveDirectoryW(full.c_str());
            }
            else
            {
                if (_wcsicmp(full.c_str(), exclude.c_str()) == 0) continue;
                DeleteFileW(full.c_str());
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }

    /// 结束正在运行的主程序，避免卸载时 exe/dll 被占用导致删不干净
    void KillRunningApp()
    {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return;
        PROCESSENTRY32W pe;
        ZeroMemory(&pe, sizeof(pe));
        pe.dwSize = sizeof(pe);
        bool killed = false;
        if (Process32FirstW(snap, &pe))
        {
            do
            {
                if (_wcsicmp(pe.szExeFile, kAppExe) != 0) continue;
                HANDLE p = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pe.th32ProcessID);
                if (p)
                {
                    TerminateProcess(p, 0);
                    WaitForSingleObject(p, 3000);
                    CloseHandle(p);
                    killed = true;
                }
            } while (Process32NextW(snap, &pe));
        }
        CloseHandle(snap);
        if (killed) Sleep(600);   // 给系统一点时间释放文件句柄
    }

    void Uninstall()
    {
        std::wstring dir;
        HKEY k = NULL;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, kRegKey, 0, KEY_READ, &k) == ERROR_SUCCESS)
        {
            wchar_t buf[MAX_PATH] = { 0 };
            DWORD sz = sizeof(buf);
            if (RegQueryValueExW(k, L"InstallLocation", NULL, NULL, (BYTE*)buf, &sz) == ERROR_SUCCESS)
                dir = buf;
            RegCloseKey(k);
        }
        if (dir.empty() || GetFileAttributesW(dir.c_str()) == INVALID_FILE_ATTRIBUTES)
            dir = SelfDir();

        KillRunningApp();

        wchar_t desk[MAX_PATH] = { 0 }, menu[MAX_PATH] = { 0 };
        SHGetFolderPathW(NULL, CSIDL_DESKTOPDIRECTORY, NULL, 0, desk);
        SHGetFolderPathW(NULL, CSIDL_PROGRAMS, NULL, 0, menu);
        DeleteFileW((std::wstring(desk) + L"\\" + kAppName + L".lnk").c_str());
        DeleteFileW((std::wstring(menu) + L"\\" + kAppName + L".lnk").c_str());

        SHDeleteKeyW(HKEY_CURRENT_USER, kRegKey);

        std::wstring self = SelfPath();
        DeleteTreeExcept(dir, self);
        DeleteFileW(self.c_str());
        RemoveDirectoryW(dir.c_str());

        // 清理程序运行时落在用户目录的数据（WebView2 缓存、更新标记、错误日志）
        wchar_t appdata[MAX_PATH] = { 0 };
        if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_APPDATA, NULL, 0, appdata)))
        {
            std::wstring uda = std::wstring(appdata) + L"\\RichTextGen";
            DeleteTreeExcept(uda, L"");
            RemoveDirectoryW(uda.c_str());
        }

        // 若安装目录仍存在（自身被锁），交给 cmd 延迟删除
        if (GetFileAttributesW(dir.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            std::wstring q = L"\"";
            std::wstring cmd = L"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q " + q + dir + q;
            ShellExecuteW(NULL, L"open", L"cmd.exe", cmd.c_str(), NULL, SW_HIDE);
        }
    }

    // ==================================================================== 界面绘制
    // 经典浅色界面：浅灰主体 + 系统标准控件（传统 Windows 安装程序样式）
    void Paint(HWND hwnd, HDC hdc)
    {
        RECT rc; GetClientRect(hwnd, &rc);
        int pw = rc.right, ph = rc.bottom;
        if (pw <= 0 || ph <= 0) return;

        HDC mem = CreateCompatibleDC(hdc);
        HBITMAP bmp = CreateCompatibleBitmap(hdc, pw, ph);
        HGDIOBJ old = SelectObject(mem, bmp);

        Graphics g(mem);
        g.SetSmoothingMode(SmoothingModeAntiAlias);

        // 经典浅灰主体（毛玻璃开启时用半透明白，让亚克力透出）
        SolidBrush bg(g_glass ? Color(210, 244, 246, 249) : Color(243, 245, 248));
        g.FillRectangle(&bg, Rect(0, 0, pw, ph));

        BitBlt(hdc, 0, 0, pw, ph, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
    }

    HFONT MakeFont(int px, int weight)
    {
        return CreateFontW(-px, 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    }

    // ---- DPI 字体（与坐标一同在 LayoutControls 中按缩放重建）----
    HFONT g_fBody = NULL, g_fBold = NULL;
    void DisposeFonts()
    {
        if (g_fBody) { DeleteObject(g_fBody); g_fBody = NULL; }
        if (g_fBold) { DeleteObject(g_fBold); g_fBold = NULL; }
    }

    /// 按当前窗口客户区大小 + DPI 重排全部控件（WM_CREATE / WM_SIZE / WM_DPICHANGED 共用）。
    /// 可缩放：许可框吃掉多余高度、安装路径框吃掉多余宽度、按钮钉在右下角。
    /// 缩到设计尺寸(720x500)时坐标与原固定布局逐像素一致，只在更小/更大时才变化。
    void LayoutControls(HWND hwnd)
    {
        DisposeFonts();
        g_fBody = MakeFont(S(13), FW_NORMAL);
        g_fBold = MakeFont(S(15), FW_BOLD);

        // 关闭子控件主题渲染，强制经典浅色样式（深色系统主题下也保持白底黑字/浅灰控件）
        typedef HRESULT(WINAPI* FnTheme)(HWND, LPCWSTR, LPCWSTR);
        HMODULE ux = LoadLibraryW(L"uxtheme.dll");
        FnTheme fnTheme = ux ? (FnTheme)GetProcAddress(ux, "SetWindowTheme") : NULL;

        RECT rc = { 0, 0, 0, 0 };
        GetClientRect(hwnd, &rc);
        int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
        if (cw <= 0 || ch <= 0) { cw = S(kWinW); ch = S(kWinH); }

        // ---- 垂直：设计值 + 把多余/不足的高度按「许可框优先」分配 ----
        int licY  = S(40);
        int licH  = S(92);
        int gapSB = S(122);                       // 状态文字与按钮之间的间距（设计值）
        int extra = ch - S(kWinH);
        if (extra > 0)
        {
            int add = extra;                      // 拉高：许可框先吃掉，封顶后剩下的留给间距
            if (add > S(600)) add = S(600);
            licH += add;
            gapSB += extra - add;
        }
        else if (extra < 0)
        {
            int need = -extra;                    // 压矮：先压间距（最低 24），再压许可框（最低 70）
            int fromGap = gapSB - S(24); if (fromGap < 0) fromGap = 0;
            int useGap = need < fromGap ? need : fromGap;
            gapSB -= useGap; need -= useGap;
            if (need > 0) { licH -= need; if (licH < S(70)) licH = S(70); }
        }

        int pathY   = licY + licH + S(13);
        int chk1Y   = pathY + S(39);
        int chk2Y   = chk1Y + S(28);
        int progY   = chk2Y + S(40);
        int statusY = progY + S(22);
        int btnH    = S(36);
        int btnY    = statusY + S(20) + gapSB;
        int bottomPad = ch - (btnY + btnH);        // 按钮贴底（设计高度下 = 48）
        if (bottomPad < S(12)) { bottomPad = S(12); btnY = ch - bottomPad - btnH; }

        // ---- 水平：路径框随宽度伸缩，浏览按钮钉右 ----
        const int m = S(44);
        int lblW = S(60), browseW = S(82);
        int editX = m + lblW + S(12);
        int browseX = cw - m - browseW;
        int editW = browseX - S(12) - editX;
        if (editW < S(120)) editW = S(120);

        // 三个勾选：宽窗口保持原坐标，窄窗口等分压缩（保证都在可视区内）
        int k1 = m, w1 = S(170), k2 = S(254), w2 = S(190), k3 = S(494), w3 = S(170);
        if (k3 + w3 > cw - m)
        {
            int cw3 = (cw - m * 2 - S(24)) / 3; if (cw3 < S(96)) cw3 = S(96);
            k1 = m; w1 = cw3; k2 = k1 + cw3 + S(12); w2 = cw3; k3 = k2 + cw3 + S(12); w3 = cw3;
        }

        // 按钮钉右下角
        int cancelW = S(96), installW = S(126);
        int cancelX = cw - m - cancelW;
        int installX = cancelX - S(14) - installW;
        if (installX < m) installX = m;

        struct Item { int id; int x, y, w, h; bool bold; };
        const Item items[] = {
            { IDC_EDIT_LICENSE, m,        licY,           cw - m * 2, licH,    false },
            { IDC_LBL_PATH,     m,        pathY + S(3),   lblW,       S(20),   false },
            { IDC_EDIT_PATH,    editX,    pathY,          editW,      S(25),   false },
            { IDC_BTN_BROWSE,   browseX,  pathY,          browseW,    S(26),   false },
            { IDC_CHK_DESKTOP,  k1,       chk1Y,          w1,         S(22),   false },
            { IDC_CHK_START,    k2,       chk1Y,          w2,         S(22),   false },
            { IDC_CHK_RUN,      k3,       chk1Y,          w3,         S(22),   false },
            { IDC_CHK_GLASS,    k1,       chk2Y,          w1,         S(22),   false },
            { IDC_CHK_MER,      k2,       chk2Y,          w2,         S(22),   false },
            { IDC_BTN_MER,      k3,       chk2Y - S(2),    w3,         S(26),   false },
            { IDC_PROGRESS,     m,        progY,          cw - m * 2, S(14),   false },
            { IDC_STATUS,       m,        statusY,        cw - m * 2, S(20),   false },
            { IDC_BTN_INSTALL,  installX, btnY,           installW,   btnH,    true  },
            { IDC_BTN_CANCEL,   cancelX,  btnY,           cancelW,    btnH,    false }
        };
        for (int i = 0; i < (int)(sizeof(items) / sizeof(items[0])); i++)
        {
            HWND c = GetDlgItem(hwnd, items[i].id);
            if (!c) continue;
            SetWindowPos(c, NULL, items[i].x, items[i].y, items[i].w, items[i].h,
                         SWP_NOZORDER | SWP_NOACTIVATE);
            SendMessageW(c, WM_SETFONT, (WPARAM)(items[i].bold ? g_fBold : g_fBody), TRUE);
            if (fnTheme) fnTheme(c, L"", L"");   // 经典样式：白底黑字、浅灰按钮、绿进度条
        }
    }

    HWND g_hwnd = NULL;

    void ApplyGlass(bool on)
    {
        bool ok = Acrylic(g_hwnd, on);   // 关闭时也要调用，才能撤掉亚克力与扩展边框
        g_glass = on && ok;
        if (on && !g_glass)
        {
            Button_SetCheck(GetDlgItem(g_hwnd, IDC_CHK_GLASS), BST_UNCHECKED);
            SetDlgItemTextW(g_hwnd, IDC_STATUS, L"毛玻璃启用失败，已回退到浅色经典界面。");
        }
        else
        {
            SetDlgItemTextW(g_hwnd, IDC_STATUS, g_glass ? L"毛玻璃背景已开启（亚克力）。" : L"浅色经典界面（标准 Windows 安装程序样式）。");
        }
        InvalidateRect(g_hwnd, NULL, TRUE);
    }

    // ---- MER 地图编辑器（Alpha）：检测与协同安装 ----

    // 已安装则返回它的目录（读 HKCU 卸载信息 + 校验主程序在不在），否则返回空
    std::wstring MerInstalled()
    {
        HKEY k = NULL;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, kMerRegKey, 0, KEY_READ, &k) != ERROR_SUCCESS) return L"";
        wchar_t buf[MAX_PATH * 2] = { 0 };
        DWORD sz = sizeof(buf), type = 0;
        LONG r = RegQueryValueExW(k, L"InstallLocation", NULL, &type, (BYTE*)buf, &sz);
        RegCloseKey(k);
        if (r != ERROR_SUCCESS || buf[0] == 0) return L"";
        std::wstring dir = buf;
        if (GetFileAttributesW((dir + L"\\" + kMerSubExe).c_str()) == INVALID_FILE_ATTRIBUTES) return L"";
        return dir;
    }

    // 「检测 / 安装状态」按钮：先探测；没装就自动勾选，并说明会随本程序一起装到哪
    void OnMerEntry(HWND hwnd)
    {
        std::wstring dir = MerInstalled();
        if (!dir.empty())
        {
            SetDlgItemTextW(hwnd, IDC_STATUS,
                (std::wstring(L"检测到已安装 ") + kMerName + L"：\r\n" + dir + L"（无需重复安装）").c_str());
            return;
        }
        Button_SetCheck(GetDlgItem(hwnd, IDC_CHK_MER), BST_CHECKED);
        wchar_t buf[MAX_PATH * 2] = { 0 };
        GetDlgItemTextW(hwnd, IDC_EDIT_PATH, buf, MAX_PATH * 2);
        SetDlgItemTextW(hwnd, IDC_STATUS,
            (std::wstring(L"未检测到 ") + kMerName + L"。\r\n已自动勾选：点「立即安装」会把它一并装到 " +
             std::wstring(buf) + L"\\" + kMerFolder + L"\r\n注意：该组件目前处于 Alpha 版本（开发中），仅供测试。").c_str());
    }

    // 协同安装：把 RCDATA 里的 MER 安装器写到临时文件，静默装到 <baseDir>\MER-MapEditor(Alpha)
    int InstallMer(const std::wstring& baseDir)
    {
        HRSRC hr = FindResourceW(NULL, L"MER_PAYLOAD", RT_RCDATA);
        if (!hr) return -2;
        HGLOBAL hg = LoadResource(NULL, hr);
        if (!hg) return -2;
        const unsigned char* pdata = (const unsigned char*)LockResource(hg);
        DWORD psize = SizeofResource(NULL, hr);
        if (!pdata || psize == 0) return -2;

        std::wstring dir = baseDir + L"\\" + kMerFolder;
        CreateDirectoryW(dir.c_str(), NULL);

        wchar_t tmp[MAX_PATH] = { 0 };
        GetTempPathW(MAX_PATH, tmp);
        std::wstring exe = std::wstring(tmp) + L"MER-MapEditor-Alpha-Setup.exe";
        HANDLE hf = CreateFileW(exe.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (hf == INVALID_HANDLE_VALUE) return -3;
        DWORD written = 0;
        BOOL okw = WriteFile(hf, pdata, psize, &written, NULL);
        CloseHandle(hf);
        if (!okw || written != psize) { DeleteFileW(exe.c_str()); return -4; }

        std::wstring cmd = L"\"" + exe + L"\" /silent \"" + dir + L"\"";
        wchar_t cmdline[32768] = { 0 };
        wcsncpy_s(cmdline, cmd.c_str(), _TRUNCATE);

        STARTUPINFOW si = { 0 }; si.cb = sizeof(si);
        PROCESS_INFORMATION pi = { 0 };
        if (!CreateProcessW(NULL, cmdline, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
        {
            DeleteFileW(exe.c_str());
            return -5;
        }
        WaitForSingleObject(pi.hProcess, 180000);
        DWORD rc = 1;
        GetExitCodeProcess(pi.hProcess, &rc);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        DeleteFileW(exe.c_str());
        return (int)rc;
    }

    void DoInstall(HWND hwnd)
    {
        if (g_installing) return;
        wchar_t buf[MAX_PATH * 2] = { 0 };
        GetDlgItemTextW(hwnd, IDC_EDIT_PATH, buf, MAX_PATH * 2);
        std::wstring dir = buf;
        // 去首尾空白
        size_t a = dir.find_first_not_of(L" \t");
        size_t b = dir.find_last_not_of(L" \t");
        if (a == std::wstring::npos) { MessageBoxW(hwnd, L"请选择安装位置。", L"提示", MB_OK); return; }
        dir = dir.substr(a, b - a + 1);

        bool desktop = Button_GetCheck(GetDlgItem(hwnd, IDC_CHK_DESKTOP)) == BST_CHECKED;
        bool startMenu = Button_GetCheck(GetDlgItem(hwnd, IDC_CHK_START)) == BST_CHECKED;
        bool run = Button_GetCheck(GetDlgItem(hwnd, IDC_CHK_RUN)) == BST_CHECKED;
        bool withMer = Button_GetCheck(GetDlgItem(hwnd, IDC_CHK_MER)) == BST_CHECKED;

        g_installing = true;
        EnableWindow(GetDlgItem(hwnd, IDC_BTN_INSTALL), FALSE);
        EnableWindow(GetDlgItem(hwnd, IDC_BTN_CANCEL), FALSE);

        struct Ctx { HWND hwnd; } ctx = { hwnd };
        bool ok = Install(dir, desktop, startMenu,
            [](void* c, int pct, const wchar_t* msg) {
                Ctx* x = (Ctx*)c;
                SetStatusText(x->hwnd, (std::wstring(L"正在释放 ") + msg).c_str(), pct);
                PumpMessages();
            }, &ctx);

        if (ok)
        {
            // ---- 可选组件：MER 地图编辑器（Alpha）----
            std::wstring merLine;
            if (withMer)
            {
                std::wstring already = MerInstalled();
                if (!already.empty())
                {
                    merLine = std::wstring(L"\r\n\r\n") + kMerName + L"：已安装（" + already + L"），未重复安装";
                }
                else
                {
                    SetStatusText(hwnd, L"正在协同安装 MER 地图编辑器（Alpha）…", 96);
                    PumpMessages();
                    int rc = InstallMer(dir);
                    if (rc == 0) merLine = std::wstring(L"\r\n\r\n") + kMerName + L"：已装到 " + dir + L"\\" + kMerFolder;
                    else merLine = std::wstring(L"\r\n\r\n") + kMerName + L"：安装未成功（错误码 " + std::to_wstring(rc) + L"），可单独运行安装程序重试";
                }
            }

            std::wstring exe = PickMainExe(dir);
            SetStatusText(hwnd, (std::wstring(L"安装完成：") + dir).c_str(), 100);
            if (run) ShellExecuteW(hwnd, L"open", exe.c_str(), NULL, dir.c_str(), SW_SHOWNORMAL);
            MessageBoxW(hwnd,
                (std::wstring(L"安装完成！\r\n\r\n位置：") + dir +
                 (desktop ? L"\r\n桌面快捷方式已创建" : L"") + merLine +
                 L"\r\n\r\n卸载：开始菜单搜索“彩色文本生成器”或运行安装目录下的 Uninstall.exe").c_str(),
                L"完成", MB_OK | MB_ICONINFORMATION);
            PostMessageW(hwnd, WM_CLOSE, 0, 0);
        }
        else
        {
            g_installing = false;
            EnableWindow(GetDlgItem(hwnd, IDC_BTN_INSTALL), TRUE);
            EnableWindow(GetDlgItem(hwnd, IDC_BTN_CANCEL), TRUE);
            MessageBoxW(hwnd, L"安装失败，请检查目录权限后重试。", L"错误", MB_OK | MB_ICONERROR);
        }
    }

    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        switch (msg)
        {
        case WM_CREATE:
        {
            g_hwnd = hwnd;
            RoundCorners(hwnd);

            // 控件先以 0 尺寸创建，位置与字号统一交给 LayoutControls（按 DPI 缩放）
            HWND lic = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_READONLY | WS_VSCROLL | ES_AUTOVSCROLL,
                0, 0, 0, 0, hwnd, (HMENU)IDC_EDIT_LICENSE, g_hInst, NULL);
            SetWindowTextW(lic, L"1. 本程序按“原样”提供，用于编辑与生成 Unity/TextMeshPro 富文本（类 Win11 记事本的富文本记事本）。\r\n"
                L"2. 请勿用于违反游戏服务条款的用途；因使用本程序产生的后果由使用者自负。\r\n"
                L"3. 程序会联网检查更新（GitHub），并在你点击“更新”时下载新版本。\r\n"
                L"4. 本程序不收集你的任何个人信息。\r\n\r\n继续安装即表示你同意以上条款。");

            CreateWindowExW(0, L"STATIC", L"安装位置", WS_CHILD | WS_VISIBLE,
                0, 0, 0, 0, hwnd, (HMENU)IDC_LBL_PATH, g_hInst, NULL);

            CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", GetDefaultDir().c_str(),
                WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                0, 0, 0, 0, hwnd, (HMENU)IDC_EDIT_PATH, g_hInst, NULL);

            CreateWindowExW(0, L"BUTTON", L"浏览…", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                0, 0, 0, 0, hwnd, (HMENU)IDC_BTN_BROWSE, g_hInst, NULL);

            HWND chkDesk = CreateWindowExW(0, L"BUTTON", L"创建桌面快捷方式",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0, 0, 0, 0, hwnd, (HMENU)IDC_CHK_DESKTOP, g_hInst, NULL);
            Button_SetCheck(chkDesk, BST_CHECKED);

            HWND chkStart = CreateWindowExW(0, L"BUTTON", L"在开始菜单创建快捷方式",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0, 0, 0, 0, hwnd, (HMENU)IDC_CHK_START, g_hInst, NULL);
            Button_SetCheck(chkStart, BST_CHECKED);

            HWND chkRun = CreateWindowExW(0, L"BUTTON", L"安装完成后启动程序",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0, 0, 0, 0, hwnd, (HMENU)IDC_CHK_RUN, g_hInst, NULL);
            Button_SetCheck(chkRun, BST_CHECKED);

            HWND chkGlass = CreateWindowExW(0, L"BUTTON", L"毛玻璃背景（Win11 22H2）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0, 0, 0, 0, hwnd, (HMENU)IDC_CHK_GLASS, g_hInst, NULL);

            // 可选组件：MER 地图编辑器（Alpha）
            HWND chkMer = CreateWindowExW(0, L"BUTTON", L"同时安装 MER 地图编辑器（Alpha）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0, 0, 0, 0, hwnd, (HMENU)IDC_CHK_MER, g_hInst, NULL);

            HWND btnMer = CreateWindowExW(0, L"BUTTON", L"检测 / 安装状态",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                0, 0, 0, 0, hwnd, (HMENU)IDC_BTN_MER, g_hInst, NULL);
            if (!BackdropSupported()) EnableWindow(chkGlass, FALSE);

            HWND prog = CreateWindowExW(0, PROGRESS_CLASSW, L"", WS_CHILD | WS_VISIBLE,
                0, 0, 0, 0, hwnd, (HMENU)IDC_PROGRESS, g_hInst, NULL);
            SendMessageW(prog, PBM_SETRANGE32, 0, 100);

            CreateWindowExW(0, L"STATIC",
                (BackdropSupported() ? L"准备就绪，点击“立即安装”开始。" : L"当前系统不支持系统级毛玻璃（需 Win11 22H2+），已使用浅色经典界面。"),
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                0, 0, 0, 0, hwnd, (HMENU)IDC_STATUS, g_hInst, NULL);

            CreateWindowExW(0, L"BUTTON", L"立即安装", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON | BS_DEFPUSHBUTTON,
                0, 0, 0, 0, hwnd, (HMENU)IDC_BTN_INSTALL, g_hInst, NULL);

            CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                0, 0, 0, 0, hwnd, (HMENU)IDC_BTN_CANCEL, g_hInst, NULL);

            LayoutControls(hwnd);

            return 0;
        }

        case WM_GETMINMAXINFO:
        {
            // 最小尺寸：保证缩到最小时许可框、勾选与按钮都不重叠
            MINMAXINFO* mmi = (MINMAXINFO*)lp;
            RECT r = { 0, 0, S(620), S(400) };
            DWORD style = (DWORD)GetWindowLongPtrW(hwnd, GWL_STYLE);
            DWORD ex = (DWORD)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            AdjustWindowRectEx(&r, style, FALSE, ex);
            mmi->ptMinTrackSize.x = r.right - r.left;
            mmi->ptMinTrackSize.y = r.bottom - r.top;

            // 最大化时不要盖住任务栏
            HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = { sizeof(MONITORINFO) };
            if (GetMonitorInfoW(mon, &mi))
            {
                mmi->ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                mmi->ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                mmi->ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                mmi->ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
            }
            return 0;
        }

        case WM_SIZE:
            // 鼠标拖拽改变窗口大小：控件按新的客户区重排（最小化时不重排）
            if (wp != SIZE_MINIMIZED)
            {
                LayoutControls(hwnd);
                InvalidateRect(hwnd, NULL, TRUE);
            }
            return 0;

        case WM_DPICHANGED:
        {
            // 拖到不同 DPI 的显示器：按新 DPI 重算缩放并重排
            g_scale = (double)HIWORD(wp) / 96.0;
            RECT* want = (RECT*)lp;
            SetWindowPos(hwnd, NULL, want->left, want->top,
                         want->right - want->left, want->bottom - want->top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
            LayoutControls(hwnd);
            InvalidateRect(hwnd, NULL, TRUE);
            return 0;
        }

        case WM_COMMAND:
            switch (LOWORD(wp))
            {
            case IDC_BTN_BROWSE:
            {
                BROWSEINFOW bi = { 0 };
                bi.hwndOwner = hwnd;
                bi.lpszTitle = L"选择安装位置";
                bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
                PIDLIST_ABSOLUTE pidl = SHBrowseForFolderW(&bi);
                if (pidl)
                {
                    wchar_t path[MAX_PATH] = { 0 };
                    if (SHGetPathFromIDListW(pidl, path)) SetDlgItemTextW(hwnd, IDC_EDIT_PATH, path);
                    CoTaskMemFree(pidl);
                }
                return 0;
            }
            case IDC_CHK_GLASS:
                ApplyGlass(Button_GetCheck(GetDlgItem(hwnd, IDC_CHK_GLASS)) == BST_CHECKED);
                return 0;
            case IDC_BTN_MER:
                OnMerEntry(hwnd);
                return 0;
            case IDC_CHK_MER:
                if (Button_GetCheck(GetDlgItem(hwnd, IDC_CHK_MER)) == BST_CHECKED)
                    SetDlgItemTextW(hwnd, IDC_STATUS, (std::wstring(L"将一并安装 ") + kMerName + L"（Alpha，开发中，仅供测试）").c_str());
                else
                    SetDlgItemTextW(hwnd, IDC_STATUS, L"已取消一并安装 MER 地图编辑器。");
                return 0;
            case IDC_BTN_INSTALL:
                DoInstall(hwnd);
                return 0;
            case IDC_BTN_CANCEL:
                PostMessageW(hwnd, WM_CLOSE, 0, 0);
                return 0;
            }
            break;

        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            Paint(hwnd, hdc);
            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_PRINT:
        case WM_PRINTCLIENT:
            // 支持 PrintWindow / 缩略图：把界面画到调用方提供的 DC。
            // 必须返回非 0，否则 PrintWindow 会把结果当作失败（返回 FALSE）。
            Paint(hwnd, (HDC)wp);
            return 1;

        case WM_CTLCOLOREDIT:
        {
            // 强制文本框浅色（深色系统主题下也保持白底黑字）
            HDC dc = (HDC)wp;
            SetTextColor(dc, RGB(30, 30, 30));
            SetBkColor(dc, RGB(255, 255, 255));
            return (LRESULT)GetStockObject(WHITE_BRUSH);
        }

        case WM_CTLCOLORSTATIC:
        {
            // 静态标签 / 复选框文字 / 只读 EDIT：浅色实心底（深色主题下不露黑）
            HDC dc = (HDC)wp;
            if (lp)
            {
                wchar_t cls[32] = { 0 };
                GetClassNameW((HWND)lp, cls, 32);
                if (_wcsicmp(cls, L"Edit") == 0)
                {
                    SetTextColor(dc, RGB(30, 30, 30));
                    SetBkColor(dc, RGB(255, 255, 255));
                    return (LRESULT)GetStockObject(WHITE_BRUSH);
                }
            }
            SetTextColor(dc, RGB(40, 42, 46));
            SetBkColor(dc, RGB(243, 245, 248));
            static HBRUSH sbg = CreateSolidBrush(RGB(243, 245, 248));
            return (LRESULT)sbg;
        }

        case WM_CTLCOLORBTN:
        {
            // 按钮 / 复选框：浅色背景 + 深色文字
            HDC dc = (HDC)wp;
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB(30, 30, 30));
            return (LRESULT)GetStockObject(WHITE_BRUSH);
        }

        case WM_ERASEBKGND:
            return 1;

        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    void EnableDpiAwareness()
    {
        // 系统级 DPI 感知：窗口与所有子控件按 96 DPI 逻辑坐标布局，
        // 由系统在 125%/150% 屏幕上统一放大（经典 Win32 安装程序做法，控件不会错位）。
        typedef BOOL(WINAPI* Fn10)(void);
        Fn10 old = (Fn10)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDPIAware");
        if (old) old();
    }

    /// 取主显示器 DPI（优先 GetDpiForSystem，回退 GetDeviceCaps）
    int PrimaryDpi()
    {
        typedef UINT(WINAPI* Fn)(void);
        Fn fn = (Fn)GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForSystem");
        if (fn)
        {
            UINT d = fn();
            if (d >= 96) return (int)d;
        }
        HDC hdc = GetDC(NULL);
        int dpi = hdc ? GetDeviceCaps(hdc, LOGPIXELSX) : 96;
        if (hdc) ReleaseDC(NULL, hdc);
        return dpi >= 96 ? dpi : 96;
    }

    /// 取窗口所在显示器的实际 DPI（GetDpiForSystem 在多屏/系统 DPI 不一致时不可靠）
    UINT WindowDpi(HWND hwnd)
    {
        typedef UINT(WINAPI* Fn)(HWND);
        Fn fn = (Fn)GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow");
        if (fn)
        {
            UINT d = fn(hwnd);
            if (d >= 96) return d;
        }
        HDC hdc = GetDC(hwnd);
        int dpi = hdc ? GetDeviceCaps(hdc, LOGPIXELSX) : 96;
        if (hdc) ReleaseDC(hwnd, hdc);
        return dpi >= 96 ? (UINT)dpi : 96;
    }

    int RunGui()
    {
        INITCOMMONCONTROLSEX icc = { sizeof(icc), ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES };
        InitCommonControlsEx(&icc);

        // 标准窗口 + 系统级 DPI 缩放：控件统一按 96 DPI 逻辑坐标布局，
        // 在 125%/150% 屏幕上由系统整体放大（经典 Win32 安装程序做法，不会错位溢出）
        g_scale = 1.0;

        // 窗口类：标准边框（原生标题栏 + 系统按钮），Win11 经典风格
    WNDCLASSEXW wc = { 0 };
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = WndProc;
        wc.hInstance = g_hInst;
        wc.hCursor = LoadCursor(NULL, IDC_ARROW);
        wc.hbrBackground = NULL;
        wc.lpszClassName = L"RichTextGenSetupWnd";
        wc.style = CS_HREDRAW | CS_VREDRAW;
        RegisterClassExW(&wc);

        std::wstring title = std::wstring(kAppName) + L" 安装向导  v" + kVersion;
        // 可缩放窗口：WS_THICKFRAME 让鼠标可以拖拽边框/四角改变大小，WS_MAXIMIZEBOX 允许最大化
        DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME;
        g_hwnd = CreateWindowExW(WS_EX_APPWINDOW, wc.lpszClassName, title.c_str(),
            style | WS_CLIPCHILDREN,
            CW_USEDEFAULT, CW_USEDEFAULT, 736, 560,
            NULL, NULL, g_hInst, NULL);
        if (!g_hwnd) return 1;

        g_scale = 1.0;

        // 客户区严格按设计尺寸 720x500 创建，使首次显示与原固定布局逐像素一致
        RECT wr = { 0, 0, 720, 500 };
        AdjustWindowRectEx(&wr, style, FALSE, WS_EX_APPWINDOW);
        int winW = wr.right - wr.left, winH = wr.bottom - wr.top;
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        SetWindowPos(g_hwnd, NULL, (sw - winW) / 2, (sh - winH) / 2, winW, winH, SWP_NOZORDER);
        ShowWindow(g_hwnd, SW_SHOW);
        UpdateWindow(g_hwnd);

        MSG msg;
        while (GetMessageW(&msg, NULL, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
        return (int)msg.wParam;
    }
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR cmdline, int)
{
    g_hInst = hInst;
    EnableDpiAwareness();
    CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);

    GdiplusStartupInput gsi;
    GdiplusStartup(&g_gdiplusToken, &gsi, NULL);

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(cmdline, &argc);
    bool silent = false, uninstall = false;
    std::wstring silentDir;
    // 注意：wWinMain 的 cmdline 不含 exe 名，故 argv[0] 已是第一个参数，从 0 开始
    for (int i = 0; i < argc; i++)
    {
        std::wstring a = argv[i];
        if (a == L"--silent" || a == L"-s") { silent = true; if (i + 1 < argc) silentDir = argv[i + 1]; }
        if (a == L"/uninstall" || a == L"--uninstall") uninstall = true;
    }
    if (argv) LocalFree(argv);

    int code = 0;
    if (uninstall)
    {
        Uninstall();
    }
    else if (silent)
    {
        std::wstring target = silentDir.empty() ? GetDefaultDir() : silentDir;
        bool ok = Install(target, true, false, NULL, NULL);
        code = ok ? 0 : 1;
    }
    else
    {
        code = RunGui();
    }

    GdiplusShutdown(g_gdiplusToken);
    CoUninitialize();
    return code;
}
