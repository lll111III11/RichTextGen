// 彩色文本生成器 安装程序（原生 C++ / 零依赖）
// ---------------------------------------------------------------------------
// 特性
//   · 纯 Win32 原生程序：不依赖 .NET Framework、不依赖 WebView2 运行时，
//     只链接系统自带 DLL（user32/gdi32/shell32/ole32/gdiplus/comctl32/advapi32）。
//   · 载荷（程序 exe/config + WebView2 三个组件）由 gen_embedded.ps1 转成字节数组
//     内嵌进本 exe，安装时逐个释放，文件名原样保留（含中文名与多段名）。
//   · 界面：GDI+ 45° 渐变沿左上→右下流动 + 一条黑色斜杠斜着扫过；
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
    const wchar_t* const kVersion   = L"5.6.0.0";

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
    double g_phase = 0.0;    // 渐变色相位 0..1
    double g_sweep = 0.0;    // 黑色斜杠相位 0..1
    bool g_glass = false;    // 毛玻璃是否生效
    bool g_installing = false;

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
            if (_wcsicmp(fd.cFileName, L"Uninstall.exe") == 0) continue;
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

    // 安装主体。progress 可空（静默模式）。
    bool Install(const std::wstring& target, bool desktop, bool startMenu,
                 void (*progress)(void*, int, const wchar_t*), void* ctx)
    {
        CreateDirectoryW(target.c_str(), NULL);
        DeleteOldFiles(target);

        // 释放内嵌载荷
        for (unsigned long i = 0; i < kPayloadCount; i++)
        {
            std::wstring out = target + L"\\" + kPayload[i].name;
            HANDLE h = CreateFileW(out.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (h == INVALID_HANDLE_VALUE) continue;
            DWORD written = 0;
            WriteFile(h, kPayload[i].data, kPayload[i].size, &written, NULL);
            CloseHandle(h);
            int pct = (int)(20 + (i + 1) * 70 / kPayloadCount);
            if (pct > 90) pct = 90;
            if (progress) progress(ctx, pct, kPayload[i].name);
        }

        // 复制自身为卸载程序
        std::wstring self = SelfPath();
        std::wstring un = target + L"\\Uninstall.exe";
        if (_wcsicmp(self.c_str(), un.c_str()) != 0) CopyFileW(self.c_str(), un.c_str(), FALSE);

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

        // 若仍存在（自身被锁），交给 cmd 延迟删除
        if (GetFileAttributesW(dir.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            std::wstring q = L"\"";
            std::wstring cmd = L"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q " + q + dir + q;
            ShellExecuteW(NULL, L"open", L"cmd.exe", cmd.c_str(), NULL, SW_HIDE);
        }
    }

    // ==================================================================== 界面绘制
    void PaintGradient(Graphics& g, int w, int h, int alpha)
    {
        const int n = 6;
        double sp = g_phase * n;
        int k = (int)std::floor(sp);
        double f = sp - k;
        Color stops[n + 1];
        float pos[n + 1];
        for (int i = 0; i <= n; i++)
        {
            Color c = Mix(Palette(i + k), Palette(i + k + 1), f);
            stops[i] = (alpha < 255) ? Color((BYTE)alpha, c.GetR(), c.GetG(), c.GetB()) : c;
            pos[i] = (float)i / n;
        }
        RectF r(-2.0f, -2.0f, (float)(w + 4), (float)(h + 4));
        // 构造时首尾色不同（GDI+ 不允许两色相同），实际色带由 SetInterpolationColors 决定
        LinearGradientBrush br(r, stops[0], stops[1], 45.0f);
        br.SetInterpolationColors(stops, pos, n + 1);
        g.FillRectangle(&br, Rect(0, 0, w, h));
    }

    void PaintSweep(Graphics& g, int w, int h)
    {
        int diag = (int)std::sqrt((double)w * w + (double)h * h);
        GraphicsState st = g.Save();
        g.TranslateTransform(w / 2.0f, h / 2.0f);
        g.RotateTransform(45.0f);
        int cx = (int)(-diag * 0.5 + g_sweep * diag * 1.0);
        Rect band(cx - 62, -diag, 124, diag * 2);
        Color c0(0, 0, 0, 0), c1(150, 0, 0, 0);
        LinearGradientBrush bg(band, c0, c1, 0.0f);
        Color cols[4] = { Color(0, 0, 0, 0), Color(150, 0, 0, 0), Color(150, 0, 0, 0), Color(0, 0, 0, 0) };
        float p[4] = { 0.0f, 0.3f, 0.7f, 1.0f };
        bg.SetInterpolationColors(cols, p, 4);
        g.FillRectangle(&bg, band);
        g.Restore(st);
    }

    void PaintLogo(Graphics& g)
    {
        Rect r(18, 15, 20, 20);
        GraphicsPath path;
        int d = 10;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.GetRight() - d, r.Y, d, d, 270, 90);
        path.AddArc(r.GetRight() - d, r.GetBottom() - d, d, d, 0, 90);
        path.AddArc(r.X, r.GetBottom() - d, d, d, 90, 90);
        path.CloseFigure();
        RectF rf(18.0f, 15.0f, 20.0f, 20.0f);
        LinearGradientBrush b(rf, Color(0x6C, 0xD4, 0xFF), Color(0x00, 0x67, 0xC0), 45.0f);
        g.FillPath(&b, &path);
    }

    void PaintChrome(Graphics& g, int w, int h, bool glass)
    {
        PaintLogo(g);

        // 卡片：圆角矩形
        RectF card(24.0f, 62.0f, 672.0f, 414.0f);
        GraphicsPath cp;
        int cd = 24;
        cp.AddArc((int)card.X, (int)card.Y, cd, cd, 180, 90);
        cp.AddArc((int)(card.X + card.Width - cd), (int)card.Y, cd, cd, 270, 90);
        cp.AddArc((int)(card.X + card.Width - cd), (int)(card.Y + card.Height - cd), cd, cd, 0, 90);
        cp.AddArc((int)card.X, (int)(card.Y + card.Height - cd), cd, cd, 90, 90);
        cp.CloseFigure();
        SolidBrush cardBrush(glass ? Color(236, 252, 252, 252) : Color(248, 250, 252));
        g.FillPath(&cardBrush, &cp);

        // 标题栏文字
        FontFamily ff(L"Microsoft YaHei UI");
        Font title(&ff, 12.5f, FontStyleBold, UnitPoint);
        SolidBrush white(Color(255, 255, 255, 255));
        PointF pt1(46.0f, 9.0f);
        g.DrawString(kAppName, -1, &title, pt1, &white);
        Font sub(&ff, 8.5f, FontStyleRegular, UnitPoint);
        SolidBrush subBrush(Color(206, 226, 250));
        PointF pt2(46.0f, 32.0f);
        std::wstring verText = std::wstring(L"v") + kVersion + L" · HTML 界面版（WebView2）";
        g.DrawString(verText.c_str(), -1, &sub, pt2, &subBrush);

        // 标题栏按钮
        Font wbtn(&ff, 10.0f, FontStyleRegular, UnitPoint);
        SolidBrush wb(Color(232, 240, 252));
        RectF minR(630.0f, 0.0f, 42.0f, 40.0f);
        StringFormat sf;
        sf.SetAlignment(StringAlignmentCenter);
        sf.SetLineAlignment(StringAlignmentCenter);
        g.DrawString(L"—", -1, &wbtn, minR, &sf, &wb);
        RectF closeR(672.0f, 0.0f, 48.0f, 40.0f);
        g.DrawString(L"✕", -1, &wbtn, closeR, &sf, &wb);
    }

    void Paint(HWND hwnd, HDC hdc)
    {
        RECT rc; GetClientRect(hwnd, &rc);
        int w = rc.right, h = rc.bottom;
        if (w <= 0 || h <= 0) return;

        HDC mem = CreateCompatibleDC(hdc);
        HBITMAP bmp = CreateCompatibleBitmap(hdc, w, h);
        HGDIOBJ old = SelectObject(mem, bmp);

        Graphics g(mem);
        g.SetSmoothingMode(SmoothingModeAntiAlias);

        int alpha = g_glass ? 128 : 255;
        PaintGradient(g, w, h, alpha);
        if (!g_glass) PaintSweep(g, w, h);
        PaintChrome(g, w, h, g_glass);

        BitBlt(hdc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
    }

    HFONT MakeFont(int px, int weight)
    {
        return CreateFontW(-px, 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    }

    HWND g_hwnd = NULL;

    void ApplyGlass(bool on)
    {
        bool ok = Acrylic(g_hwnd, on);   // 关闭时也要调用，才能撤掉亚克力与扩展边框
        g_glass = on && ok;
        if (on && !g_glass)
        {
            Button_SetCheck(GetDlgItem(g_hwnd, IDC_CHK_GLASS), BST_UNCHECKED);
            SetDlgItemTextW(g_hwnd, IDC_STATUS, L"毛玻璃启用失败，已回退到渐变流动背景。");
        }
        else
        {
            SetDlgItemTextW(g_hwnd, IDC_STATUS, g_glass ? L"毛玻璃背景已开启（亚克力）。" : L"渐变流动背景（黑色斜杠扫描）。");
        }
        if (g_glass) KillTimer(g_hwnd, IDC_ANIM); else SetTimer(g_hwnd, IDC_ANIM, 33, NULL);
        InvalidateRect(g_hwnd, NULL, TRUE);
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

        g_installing = true;
        EnableWindow(GetDlgItem(hwnd, IDC_BTN_INSTALL), FALSE);
        EnableWindow(GetDlgItem(hwnd, IDC_BTN_CANCEL), FALSE);
        KillTimer(hwnd, IDC_ANIM);

        struct Ctx { HWND hwnd; } ctx = { hwnd };
        bool ok = Install(dir, desktop, startMenu,
            [](void* c, int pct, const wchar_t* msg) {
                Ctx* x = (Ctx*)c;
                SetStatusText(x->hwnd, (std::wstring(L"正在释放 ") + msg).c_str(), pct);
                PumpMessages();
            }, &ctx);

        if (ok)
        {
            std::wstring exe = PickMainExe(dir);
            SetStatusText(hwnd, (std::wstring(L"安装完成：") + dir).c_str(), 100);
            if (run) ShellExecuteW(hwnd, L"open", exe.c_str(), NULL, dir.c_str(), SW_SHOWNORMAL);
            MessageBoxW(hwnd,
                (std::wstring(L"安装完成！\r\n\r\n位置：") + dir +
                 (desktop ? L"\r\n桌面快捷方式已创建" : L"") +
                 L"\r\n\r\n卸载：开始菜单搜索“彩色文本生成器”或运行安装目录下的 Uninstall.exe").c_str(),
                L"完成", MB_OK | MB_ICONINFORMATION);
            PostMessageW(hwnd, WM_CLOSE, 0, 0);
        }
        else
        {
            g_installing = false;
            EnableWindow(GetDlgItem(hwnd, IDC_BTN_INSTALL), TRUE);
            EnableWindow(GetDlgItem(hwnd, IDC_BTN_CANCEL), TRUE);
            if (!g_glass) SetTimer(hwnd, IDC_ANIM, 33, NULL);
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

            HFONT fBody = MakeFont(13, FW_NORMAL);
            HFONT fBold = MakeFont(15, FW_BOLD);

            // 许可（只读多行）
            HWND lic = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_READONLY | WS_VSCROLL | ES_AUTOVSCROLL,
                44, 102, 632, 92, hwnd, (HMENU)IDC_EDIT_LICENSE, g_hInst, NULL);
            SetWindowTextW(lic, L"1. 本程序按“原样”提供，用于生成 Unity/TextMeshPro 富文本并发送到游戏。\r\n"
                L"2. 请勿用于违反游戏服务条款的用途；因使用本程序产生的后果由使用者自负。\r\n"
                L"3. 程序会联网检查更新（GitHub），并在你点击“更新”时下载新版本。\r\n"
                L"4. 本程序不收集你的任何个人信息。\r\n\r\n继续安装即表示你同意以上条款。");
            SendMessageW(lic, WM_SETFONT, (WPARAM)fBody, TRUE);

            // 路径
            CreateWindowExW(0, L"STATIC", L"安装位置", WS_CHILD | WS_VISIBLE,
                44, 210, 60, 20, hwnd, NULL, g_hInst, NULL);
            HWND edit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", GetDefaultDir().c_str(),
                WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                116, 207, 466, 25, hwnd, (HMENU)IDC_EDIT_PATH, g_hInst, NULL);
            SendMessageW(edit, WM_SETFONT, (WPARAM)fBody, TRUE);

            HWND browse = CreateWindowExW(0, L"BUTTON", L"浏览…", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                594, 206, 82, 26, hwnd, (HMENU)IDC_BTN_BROWSE, g_hInst, NULL);
            SendMessageW(browse, WM_SETFONT, (WPARAM)fBody, TRUE);

            HWND chkDesk = CreateWindowExW(0, L"BUTTON", L"创建桌面快捷方式", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                44, 246, 170, 22, hwnd, (HMENU)IDC_CHK_DESKTOP, g_hInst, NULL);
            SendMessageW(chkDesk, WM_SETFONT, (WPARAM)fBody, TRUE);
            Button_SetCheck(chkDesk, BST_CHECKED);

            HWND chkStart = CreateWindowExW(0, L"BUTTON", L"在开始菜单创建快捷方式", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                254, 246, 190, 22, hwnd, (HMENU)IDC_CHK_START, g_hInst, NULL);
            SendMessageW(chkStart, WM_SETFONT, (WPARAM)fBody, TRUE);
            Button_SetCheck(chkStart, BST_CHECKED);

            HWND chkRun = CreateWindowExW(0, L"BUTTON", L"安装完成后启动程序", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                494, 246, 170, 22, hwnd, (HMENU)IDC_CHK_RUN, g_hInst, NULL);
            SendMessageW(chkRun, WM_SETFONT, (WPARAM)fBody, TRUE);
            Button_SetCheck(chkRun, BST_CHECKED);

            HWND chkGlass = CreateWindowExW(0, L"BUTTON", L"毛玻璃背景（Win11 22H2，关闭则显示渐变流动）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                44, 274, 380, 22, hwnd, (HMENU)IDC_CHK_GLASS, g_hInst, NULL);
            SendMessageW(chkGlass, WM_SETFONT, (WPARAM)fBody, TRUE);
            if (!BackdropSupported()) EnableWindow(chkGlass, FALSE);

            // 进度条 + 状态
            HWND prog = CreateWindowExW(0, PROGRESS_CLASSW, L"", WS_CHILD | WS_VISIBLE,
                44, 314, 632, 14, hwnd, (HMENU)IDC_PROGRESS, g_hInst, NULL);
            SendMessageW(prog, PBM_SETRANGE32, 0, 100);

            HWND st = CreateWindowExW(0, L"STATIC",
                (BackdropSupported() ? L"准备就绪，点击“立即安装”开始。" : L"当前系统不支持系统级毛玻璃（需 Win11 22H2+），已使用渐变流动背景。"),
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                44, 336, 632, 20, hwnd, (HMENU)IDC_STATUS, g_hInst, NULL);
            SendMessageW(st, WM_SETFONT, (WPARAM)fBody, TRUE);

            HWND inst = CreateWindowExW(0, L"BUTTON", L"立即安装", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                452, 414, 126, 36, hwnd, (HMENU)IDC_BTN_INSTALL, g_hInst, NULL);
            SendMessageW(inst, WM_SETFONT, (WPARAM)fBold, TRUE);

            HWND cancel = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                586, 414, 96, 36, hwnd, (HMENU)IDC_BTN_CANCEL, g_hInst, NULL);
            SendMessageW(cancel, WM_SETFONT, (WPARAM)fBody, TRUE);

            SetTimer(hwnd, IDC_ANIM, 33, NULL);
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
            case IDC_BTN_INSTALL:
                DoInstall(hwnd);
                return 0;
            case IDC_BTN_CANCEL:
                PostMessageW(hwnd, WM_CLOSE, 0, 0);
                return 0;
            }
            break;

        case WM_TIMER:
            if (wp == IDC_ANIM)
            {
                g_phase += 0.0045; if (g_phase >= 1) g_phase -= 1;
                g_sweep += 0.0075; if (g_sweep >= 1) g_sweep -= 1;
                InvalidateRect(hwnd, NULL, TRUE);
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
            // 支持 PrintWindow / 缩略图：把界面画到调用方提供的 DC
            Paint(hwnd, (HDC)wp);
            return 0;

        case WM_ERASEBKGND:
            return 1;

        case WM_LBUTTONDOWN:
        {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (y < 40 && !(x >= kBtnMin.left && x < kBtnClose.right))
            {
                ReleaseCapture();
                SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                return 0;
            }
            break;
        }

        case WM_LBUTTONUP:
        {
            int x = GET_X_LPARAM(lp), y = GET_Y_LPARAM(lp);
            if (y < 40)
            {
                if (x >= kBtnMin.left && x < kBtnMin.right) { ShowWindow(hwnd, SW_MINIMIZE); return 0; }
                if (x >= kBtnClose.left && x < kBtnClose.right) { PostMessageW(hwnd, WM_CLOSE, 0, 0); return 0; }
            }
            break;
        }

        case WM_CTLCOLORSTATIC:
        {
            HDC hdc = (HDC)wp;
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, RGB(93, 93, 93));
            return (LRESULT)GetStockObject(NULL_BRUSH);
        }

        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    void EnableDpiAwareness()
    {
        typedef BOOL(WINAPI* Fn)(HANDLE);
        Fn fn = (Fn)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext");
        if (fn) fn((HANDLE)(INT_PTR)-4);   // PER_MONITOR_AWARE_V2
    }

    int RunGui()
    {
        INITCOMMONCONTROLSEX icc = { sizeof(icc), ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES };
        InitCommonControlsEx(&icc);

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
        g_hwnd = CreateWindowExW(WS_EX_APPWINDOW, wc.lpszClassName, title.c_str(),
            WS_POPUP | WS_CLIPCHILDREN, CW_USEDEFAULT, CW_USEDEFAULT, kWinW, kWinH,
            NULL, NULL, g_hInst, NULL);
        if (!g_hwnd) return 1;

        // 居中
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        SetWindowPos(g_hwnd, NULL, (sw - kWinW) / 2, (sh - kWinH) / 2, kWinW, kWinH, SWP_NOZORDER);
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
