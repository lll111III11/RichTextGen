// ---------------------------------------------------------------------------
//  RichTextGen-FullUninstall / 彩色文本生成器 彻底卸载工具
//
//  用途：本程序安装在 C:\ 或任何位置，且"卸载"会刻意保留代码签名证书；
//        只有当你想把一切痕迹清干净时，才从发布页下载本工具并运行。
//
//  它会做四件事（需要管理员权限，否则无法删除其它目录与受信任存储）：
//    1. 枚举所有固定磁盘，全目录搜索名为"彩色文本生成器.exe"的程序
//       —— 不管它属于哪个版本、装在哪个盘、是不是便携版
//    2. 删除找到的安装目录：若该目录只含本程序组件，整目录删除；
//       若混有其它文件（例如被解压到桌面或下载目录），只清理本程序组件，避免误删
//    3. 删除注册表卸载项、桌面/开始菜单快捷方式、%APPDATA%\RichTextGen 数据目录
//    4. 把安装目录里的代码签名证书从本机 Root / TrustedPublisher / TrustedPeople
//       三个受信任存储中移除
//
//  用法：RichTextGen-FullUninstall.exe                交互式（列出清单后需输入 Y 确认）
//        RichTextGen-FullUninstall.exe --dry-run      只扫描并列出将要删除的内容，不做任何改动
//        RichTextGen-FullUninstall.exe --yes          跳过确认（无人值守）
//
//  注：keep this file ASCII-safe in identifiers; Chinese literals are UTF-8
//      and the project is compiled with /utf-8.
// ---------------------------------------------------------------------------
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wincrypt.h>
#include <tlhelp32.h>
#include <string>
#include <vector>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "crypt32.lib")
// 彻底卸载需要管理员权限：删除其它目录、写 HKLM、清理本机受信任存储。
// 提权级别写在 fulluninstall.manifest 里，由 build_fulluninstall.bat 用 mt.exe 嵌入
// （pragma 传 /MANIFESTUAC 会被链接器 LNK4229 忽略，等于从不提权）。

namespace
{
    const wchar_t* const kAppExe   = L"彩色文本生成器.exe";
    const wchar_t* const kAppName  = L"彩色文本生成器";
    const wchar_t* const kCertFile = L"rtg-codesign.cer";
    const wchar_t* const kDataDir  = L"RichTextGen";   // %APPDATA%\RichTextGen
    const wchar_t* const kRegKey   =
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\RichTextGen";

    HANDLE g_out = INVALID_HANDLE_VALUE;

    // ---- 控制台输出 ----
    // 直接写控制台时用 WriteConsoleW（中文不受代码页影响）；
    // 输出被重定向到文件/管道时改为写 UTF-8 字节，便于日志留存。
    void Out(const std::wstring& s)
    {
        if (g_out == INVALID_HANDLE_VALUE) return;
        DWORD w = 0;
        DWORD mode = 0;
        if (GetConsoleMode(g_out, &mode))
        {
            WriteConsoleW(g_out, s.c_str(), (DWORD)s.size(), &w, NULL);
            return;
        }
        int need = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0, NULL, NULL);
        if (need <= 0) return;
        std::string u8((size_t)need, '\0');
        WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &u8[0], need, NULL, NULL);
        WriteFile(g_out, u8.c_str(), (DWORD)u8.size(), &w, NULL);
    }
    void OutLine(const std::wstring& s) { Out(s + L"\r\n"); }

    std::wstring ReadLine()
    {
        HANDLE h = GetStdHandle(STD_INPUT_HANDLE);
        if (h == INVALID_HANDLE_VALUE) return L"";

        std::wstring s;
        DWORD mode = 0;
        if (GetConsoleMode(h, &mode))
        {
            wchar_t buf[512] = { 0 };
            DWORD n = 0;
            if (!ReadConsoleW(h, buf, 511, &n, NULL)) return L"";
            s = buf;
        }
        else
        {
            // 输入被重定向（例如 echo Y | tool）：按 UTF-8 读字节
            char raw[512] = { 0 };
            DWORD n = 0;
            if (!ReadFile(h, raw, sizeof(raw) - 1, &n, NULL) || n == 0) return L"";
            raw[n] = 0;
            int need = MultiByteToWideChar(CP_UTF8, 0, raw, (int)n, NULL, 0);
            if (need <= 0) return L"";
            std::vector<wchar_t> buf((size_t)need + 1, 0);
            MultiByteToWideChar(CP_UTF8, 0, raw, (int)n, &buf[0], need);
            s = &buf[0];
        }
        while (!s.empty() && (s[s.size() - 1] == L'\r' || s[s.size() - 1] == L'\n'))
            s.erase(s.size() - 1);
        return s;
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

    bool EqualsI(const std::wstring& a, const wchar_t* b) { return _wcsicmp(a.c_str(), b) == 0; }

    /// 本程序的部署组件（小写比较）。只有这些文件才算"我们的"，其它一律不动。
    bool IsKnownComponentFile(const std::wstring& name)
    {
        static const wchar_t* kFiles[] = {
            L"彩色文本生成器.exe",
            L"彩色文本生成器.exe.config",
            L"uninstall.exe",
            L"microsoft.web.webview2.core.dll",
            L"microsoft.web.webview2.winforms.dll",
            L"microsoft.web.webview2.wpf.dll",
            L"webview2loader.dll",
            L"rtg-codesign.cer",
        };
        for (size_t i = 0; i < sizeof(kFiles) / sizeof(kFiles[0]); i++)
            if (_wcsicmp(name.c_str(), kFiles[i]) == 0) return true;
        return false;
    }

    bool IsKnownComponentDir(const std::wstring& name) { return _wcsicmp(name.c_str(), L"runtimes") == 0; }

    /// 是否跳过该目录（不向下递归）。
    /// 目的是避免把"开发者/用户的构建产物、中间产物、备份恢复目录"误判成安装目录并整体删除。
    /// 正常安装目录（Program Files、LOCALAPPDATA\Programs、桌面、下载目录里的部署/便携副本）不受影响。
    bool IsSkippedDirName(const std::wstring& name)
    {
        // 1) 构建 / 中间产物目录：按完整目录名精确匹配
        static const wchar_t* kExact[] = {
            L"bin", L"obj", L"payload_web", L"dist", L"build", L"out", L"target",
            L"artifacts", L"packages", L"node_modules", L"__pycache__",
            L".git", L".vs", L".idea", L".vscode", L"Debug", L"Release", L"cache",
        };
        for (size_t i = 0; i < sizeof(kExact) / sizeof(kExact[0]); i++)
            if (_wcsicmp(name.c_str(), kExact[i]) == 0) return true;

        // 2) 备份 / 恢复类目录：目录名含以下词（不区分大小写）即跳过
        static const wchar_t* kSub[] = {
            L"backup", L"recovery", L"restore", L"snapshot",
            L"备份", L"恢复", L"还原",
        };
        std::wstring lower;
        lower.reserve(name.size());
        for (size_t i = 0; i < name.size(); i++) lower.push_back((wchar_t)towlower(name[i]));
        for (size_t i = 0; i < sizeof(kSub) / sizeof(kSub[0]); i++)
        {
            std::wstring k = kSub[i];
            for (size_t j = 0; j < k.size(); j++) k[j] = (wchar_t)towlower(k[j]);
            if (lower.find(k) != std::wstring::npos) return true;
        }
        return false;
    }

    // ---------------- 全盘搜索 ----------------
    void ScanDir(const std::wstring& dir, std::vector<std::wstring>& hits)
    {
        WIN32_FIND_DATAW fd;
        std::wstring pat = dir + L"\\*";
        HANDLE h = FindFirstFileW(pat.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return;   // 权限不足等，直接跳过

        do
        {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
                if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) continue;   // 不跟软链/接合点，防成环
                if (_wcsicmp(fd.cFileName, L"$Recycle.Bin") == 0) continue;
                if (_wcsicmp(fd.cFileName, L"System Volume Information") == 0) continue;
                if (IsSkippedDirName(fd.cFileName)) continue;                       // 构建/备份目录，跳过
                ScanDir(dir + L"\\" + fd.cFileName, hits);
            }
            else if (_wcsicmp(fd.cFileName, kAppExe) == 0)
            {
                hits.push_back(dir);
            }
        } while (FindNextFileW(h, &fd));

        FindClose(h);
    }

    void ScanAllDrives(std::vector<std::wstring>& hits)
    {
        DWORD mask = GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if (!(mask & (1u << i))) continue;
            std::wstring root;
            root.push_back((wchar_t)(L'A' + i));
            root += L":\\";
            UINT type = GetDriveTypeW(root.c_str());
            if (type != DRIVE_FIXED) continue;
            OutLine(L"  正在扫描 " + root + L" …");
            ScanDir(root.substr(0, 2), hits);
        }
    }

    // ---------------- 删除 ----------------
    bool RemoveDirTree(const std::wstring& dir)
    {
        WIN32_FIND_DATAW fd;
        std::wstring pat = dir + L"\\*";
        HANDLE h = FindFirstFileW(pat.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return false;
        do
        {
            if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
            std::wstring full = dir + L"\\" + fd.cFileName;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                RemoveDirTree(full);
            }
            else
            {
                SetFileAttributesW(full.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileW(full.c_str());
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
        return RemoveDirectoryW(dir.c_str()) != 0;
    }

    /// 该目录是否只含本程序组件（可以整目录删除）
    bool IsExclusivelyOurs(const std::wstring& dir, const std::wstring& selfPath)
    {
        WIN32_FIND_DATAW fd;
        std::wstring pat = dir + L"\\*";
        HANDLE h = FindFirstFileW(pat.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return false;
        bool ok = true;
        do
        {
            if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
            std::wstring full = dir + L"\\" + fd.cFileName;
            if (_wcsicmp(full.c_str(), selfPath.c_str()) == 0) continue;   // 正在运行的本工具
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                if (!IsKnownComponentDir(fd.cFileName)) { ok = false; break; }
            }
            else if (!IsKnownComponentFile(fd.cFileName)) { ok = false; break; }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
        return ok;
    }

    // ---------------- 证书 ----------------
    struct DelCertCtx { CRYPT_HASH_BLOB blob; };

    void DelCertFromStore(HCERTSTORE st, void* p)
    {
        DelCertCtx* c = (DelCertCtx*)p;
        for (;;)
        {
            PCCERT_CONTEXT found = CertFindCertificateInStore(
                st, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0,
                CERT_FIND_SHA1_HASH, &c->blob, NULL);
            if (found == NULL) break;
            CertDeleteCertificateFromStore(found);   // 该 API 会自行释放上下文
        }
    }

    /// 把该 .cer 从本机三个受信任存储中移除；返回是否成功删到至少一条
    bool RemoveCertFromStores(const std::wstring& cerPath)
    {
        HANDLE h = CreateFileW(cerPath.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (h == INVALID_HANDLE_VALUE) return false;
        DWORD sz = GetFileSize(h, NULL);
        std::vector<BYTE> data;
        if (sz == INVALID_FILE_SIZE || sz == 0) { CloseHandle(h); return false; }
        data.resize(sz);
        DWORD rd = 0;
        BOOL readOk = ReadFile(h, &data[0], sz, &rd, NULL);
        CloseHandle(h);
        if (!readOk || rd != sz) return false;

        PCCERT_CONTEXT proto = CertCreateCertificateContext(
            X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, &data[0], (DWORD)data.size());
        if (proto == NULL) return false;

        BYTE hash[20] = { 0 };
        DWORD hashLen = sizeof(hash);
        BOOL got = CertGetCertificateContextProperty(proto, CERT_SHA1_HASH_PROP_ID, hash, &hashLen);
        CertFreeCertificateContext(proto);
        if (!got) return false;

        DelCertCtx ctx;
        ctx.blob.cbData = hashLen;
        ctx.blob.pbData = hash;

        const wchar_t* stores[] = { L"Root", L"TrustedPublisher", L"TrustedPeople" };
        for (int i = 0; i < 3; i++)
        {
            HCERTSTORE st = CertOpenStore(CERT_STORE_PROV_SYSTEM_W, 0, NULL,
                                          CERT_SYSTEM_STORE_LOCAL_MACHINE, stores[i]);
            if (st == NULL) continue;
            DelCertFromStore(st, &ctx);
            CertCloseStore(st, 0);
        }
        return true;
    }

    // ---------------- 其它痕迹 ----------------
    void RemoveShortcutsAndRegistry()
    {
        wchar_t desk[MAX_PATH] = { 0 }, menu[MAX_PATH] = { 0 };
        SHGetFolderPathW(NULL, CSIDL_DESKTOPDIRECTORY, NULL, 0, desk);
        SHGetFolderPathW(NULL, CSIDL_PROGRAMS, NULL, 0, menu);
        DeleteFileW((std::wstring(desk) + L"\\" + kAppName + L".lnk").c_str());
        DeleteFileW((std::wstring(menu) + L"\\" + kAppName + L".lnk").c_str());
        SHDeleteKeyW(HKEY_CURRENT_USER, kRegKey);
    }

    void RemoveDataDir()
    {
        wchar_t appdata[MAX_PATH] = { 0 };
        if (FAILED(SHGetFolderPathW(NULL, CSIDL_APPDATA, NULL, 0, appdata))) return;
        std::wstring dir = std::wstring(appdata) + L"\\" + kDataDir;
        if (GetFileAttributesW(dir.c_str()) != INVALID_FILE_ATTRIBUTES) RemoveDirTree(dir);
    }

    void KillRunningApp()
    {
        // 结束所有版本的"彩色文本生成器.exe"，否则文件被占用删不掉
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
        if (killed) Sleep(800);
    }
}

int wmain(int argc, wchar_t** argv)
{
    g_out = GetStdHandle(STD_OUTPUT_HANDLE);

    bool assumeYes = false;
    bool dryRun = false;
    for (int i = 1; i < argc; i++)
    {
        if (_wcsicmp(argv[i], L"--yes") == 0 || _wcsicmp(argv[i], L"-y") == 0) assumeYes = true;
        if (_wcsicmp(argv[i], L"--dry-run") == 0) dryRun = true;
    }

    OutLine(L"============================================================");
    OutLine(L" 彩色文本生成器 · 彻底卸载工具");
    OutLine(L" 会删除：所有版本的 彩色文本生成器.exe 及其安装目录、");
    OutLine(L"         快捷方式、卸载注册项、%APPDATA% 数据目录、代码签名证书");
    OutLine(L" 会跳过：构建/中间目录（bin、obj、payload_web、dist 等）");
    OutLine(L"         与备份/恢复目录（目录名含 backup / recovery / 备份 等）");
    OutLine(L"============================================================");
    OutLine(L"");

    std::wstring self = SelfPath();
    std::wstring selfDir = SelfDir();

    OutLine(L"[1/5] 全盘搜索（只搜固定磁盘，跳过系统还原点与回收站）…");
    std::vector<std::wstring> hits;
    ScanAllDrives(hits);

    // 去重
    std::vector<std::wstring> dirs;
    for (size_t i = 0; i < hits.size(); i++)
    {
        bool dup = false;
        for (size_t j = 0; j < dirs.size(); j++)
            if (_wcsicmp(dirs[j].c_str(), hits[i].c_str()) == 0) { dup = true; break; }
        if (!dup) dirs.push_back(hits[i]);
    }

    OutLine(L"");
    if (dirs.empty())
    {
        OutLine(L"没有找到任何 彩色文本生成器 的安装目录。");
        OutLine(L"（若你确定装过，可能是目录名/文件名被改过，请手动确认。）");
    }
    else
    {
        OutLine(L"找到以下安装目录：");
        for (size_t i = 0; i < dirs.size(); i++)
        {
            std::wstring mark = (_wcsicmp(dirs[i].c_str(), selfDir.c_str()) == 0) ? L"   ← 本工具所在目录，将跳过" : L"";
            OutLine(L"  · " + dirs[i] + mark);
        }
    }
    OutLine(L"");
    OutLine(L"另外还会执行：");
    OutLine(L"  · 结束所有正在运行的 彩色文本生成器.exe");
    OutLine(L"  · 删除桌面 / 开始菜单快捷方式与卸载注册项");
    OutLine(L"  · 删除 %APPDATA%\\RichTextGen 数据目录");
    OutLine(L"  · 从 Root / TrustedPublisher / TrustedPeople 移除代码签名证书");
    OutLine(L"");

    if (dryRun)
    {
        OutLine(L"【--dry-run 预演模式】下面只做展示与判定，不会改动任何文件、注册表或证书。");
        OutLine(L"");
    }
    else if (!assumeYes)
    {
        OutLine(L"该操作不可撤销。确认彻底卸载请输入 Y 再回车（其它任意键取消）：");
        std::wstring ans = ReadLine();
        if (!(ans.size() == 1 && (ans[0] == L'Y' || ans[0] == L'y')))
        {
            OutLine(L"已取消，未做任何改动。按回车退出。");
            ReadLine();
            return 1;
        }
    }
    OutLine(L"");

    OutLine(L"[2/5] 结束正在运行的程序 …");
    if (dryRun) OutLine(L"  （预演）将结束所有 彩色文本生成器.exe 进程");
    else        KillRunningApp();

    OutLine(L"[3/5] 清理安装目录与证书 …");
    for (size_t i = 0; i < dirs.size(); i++)
    {
        const std::wstring& dir = dirs[i];
        if (_wcsicmp(dir.c_str(), selfDir.c_str()) == 0)
        {
            OutLine(L"  跳过（本工具所在目录）：" + dir);
            continue;
        }

        // 先把该目录里的证书从受信任存储摘掉（文件随后就会被删）
        std::wstring cer = dir + L"\\" + kCertFile;
        bool hasCer = GetFileAttributesW(cer.c_str()) != INVALID_FILE_ATTRIBUTES;
        if (hasCer)
        {
            if (dryRun) OutLine(L"  （预演）将从受信任存储移除证书：" + cer);
            else if (RemoveCertFromStores(cer)) OutLine(L"  已移除受信任存储中的证书：" + cer);
        }

        if (IsExclusivelyOurs(dir, self))
        {
            if (dryRun)         OutLine(L"  （预演）将整目录删除：" + dir);
            else if (RemoveDirTree(dir)) OutLine(L"  已删除目录：" + dir);
            else                OutLine(L"  删除未完全成功（可能有文件被占用）：" + dir);
        }
        else
        {
            // 混有其它文件的目录：只清理本程序组件，绝不连目录一起删
            OutLine(L"  目录含其它文件，只清理本程序组件：" + dir);
            WIN32_FIND_DATAW fd;
            std::wstring pat = dir + L"\\*";
            HANDLE h = FindFirstFileW(pat.c_str(), &fd);
            if (h != INVALID_HANDLE_VALUE)
            {
                do
                {
                    if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
                    std::wstring full = dir + L"\\" + fd.cFileName;
                    if (_wcsicmp(full.c_str(), self.c_str()) == 0) continue;
                    if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;   // 只删已知文件
                    if (IsKnownComponentFile(fd.cFileName))
                    {
                        if (dryRun) OutLine(L"    （预演）将删除组件：" + std::wstring(fd.cFileName));
                        else
                        {
                            SetFileAttributesW(full.c_str(), FILE_ATTRIBUTE_NORMAL);
                            DeleteFileW(full.c_str());
                        }
                    }
                } while (FindNextFileW(h, &fd));
                FindClose(h);
            }
        }
    }

    OutLine(L"[4/5] 清理快捷方式与注册项 …");
    if (dryRun) OutLine(L"  （预演）将删除桌面/开始菜单快捷方式与卸载注册项");
    else        RemoveShortcutsAndRegistry();

    OutLine(L"[5/5] 清理数据目录 …");
    if (dryRun) OutLine(L"  （预演）将删除 %APPDATA%\\RichTextGen");
    else        RemoveDataDir();

    OutLine(L"");
    if (dryRun)
    {
        OutLine(L"预演结束，未做任何改动。去掉 --dry-run 再运行即可真正执行。按回车退出。");
        ReadLine();
        return 0;
    }
    OutLine(L"彻底卸载完成。按回车退出。");
    if (!assumeYes) ReadLine();
    return 0;
}
