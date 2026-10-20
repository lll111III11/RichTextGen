package com.richtextgen.android;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.database.Cursor;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.view.ViewGroup;
import android.webkit.JavascriptInterface;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.ByteBuffer;
import java.nio.charset.Charset;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;

/**
 * 彩色文本生成器 · 安卓版
 *
 * v1.1.0 起与 PC 端同步以下能力：
 *   - 主题：浅色 / 深色 / 跟随系统
 *   - 背景图（相册选择，本机保存，可调透明度）
 *   - 打开 / 保存文本文件（系统文件选择器，UTF-8 / UTF-16 / GBK 自动识别）
 *   - 检查更新（读取 GitHub 上的 version.json，跳转下载页）
 *
 * 界面仍由 assets/index.html 提供，原生只做手机必须由系统完成的事。
 */
public class MainActivity extends Activity {

    private static final String APP_VERSION = "1.1.0";
    private static final int APP_VERSION_CODE = 2;

    private static final int REQ_OPEN = 1001;
    private static final int REQ_SAVE = 1002;
    private static final int REQ_IMG = 1003;

    private static final String UPDATE_URL =
            "https://raw.githubusercontent.com/lll111III11/RichTextGen/main/version.json";

    private WebView web;
    private long lastBack = 0L;
    private String pendingSaveText = "";

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);

        web = new WebView(this);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);        // localStorage：主题、背景图路径
        s.setAllowFileAccess(true);
        s.setAllowFileAccessFromFileURLs(true);   // 页面在 file:// 下要能读本机背景图
        s.setAllowContentAccess(true);
        s.setBuiltInZoomControls(false);
        s.setDisplayZoomControls(false);
        s.setTextZoom(100);

        web.setBackgroundColor(isSystemDark() ? 0xFF202020 : 0xFFF3F3F3);
        web.addJavascriptInterface(new Bridge(), "RTG");
        web.loadUrl("file:///android_asset/index.html");

        setContentView(web, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
    }

    private boolean isSystemDark() {
        int mode = getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
        return mode == Configuration.UI_MODE_NIGHT_YES;
    }

    @Override
    public void onConfigurationChanged(Configuration cfg) {
        super.onConfigurationChanged(cfg);
        if (web != null) web.evaluateJavascript("window.onSystemTheme&&onSystemTheme()", null);
    }

    /** 返回键：两秒内按两次退出（避免误触直接丢掉正在编辑的文本） */
    @Override
    public void onBackPressed() {
        long now = System.currentTimeMillis();
        if (now - lastBack < 2000) {
            super.onBackPressed();
        } else {
            lastBack = now;
            Toast.makeText(this, "再按一次退出", Toast.LENGTH_SHORT).show();
        }
    }

    @Override
    protected void onDestroy() {
        if (web != null) {
            web.removeJavascriptInterface("RTG");
            web.destroy();
            web = null;
        }
        super.onDestroy();
    }

    /** 调用页面里的 window.xxx(...)，参数按 JSON 字符串转义 */
    private void callJs(String fn, String... args) {
        StringBuilder sb = new StringBuilder("window.").append(fn).append("&&").append(fn).append("(");
        for (int i = 0; i < args.length; i++) {
            if (i > 0) sb.append(',');
            sb.append(JSONObject.quote(args[i] == null ? "" : args[i]));
        }
        sb.append(");");
        final String js = sb.toString();
        runOnUiThread(new Runnable() {
            public void run() {
                if (web != null) web.evaluateJavascript(js, null);
            }
        });
    }

    private static String displayName(Context ctx, Uri uri) {
        String name = null;
        try {
            Cursor c = ctx.getContentResolver().query(uri, null, null, null, null);
            if (c != null) {
                int i = c.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (c.moveToFirst() && i >= 0) name = c.getString(i);
                c.close();
            }
        } catch (Exception ignored) { }
        if (name == null || name.length() == 0) name = "未命名.txt";
        return name;
    }

    private static byte[] readAll(InputStream in) throws Exception {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
        return bos.toByteArray();
    }

    /** UTF-8 / UTF-16 BOM / GBK 自动识别 */
    private static String decode(byte[] d) {
        try {
            if (d.length >= 3 && (d[0] & 0xFF) == 0xEF && (d[1] & 0xFF) == 0xBB && (d[2] & 0xFF) == 0xBF)
                return new String(d, 3, d.length - 3, StandardCharsets.UTF_8);
            if (d.length >= 2 && (d[0] & 0xFF) == 0xFF && (d[1] & 0xFF) == 0xFE)
                return new String(d, 2, d.length - 2, StandardCharsets.UTF_16LE);
            if (d.length >= 2 && (d[0] & 0xFF) == 0xFE && (d[1] & 0xFF) == 0xFF)
                return new String(d, 2, d.length - 2, StandardCharsets.UTF_16BE);
            try {
                return StandardCharsets.UTF_8.newDecoder()
                        .onMalformedInput(CodingErrorAction.REPORT)
                        .onUnmappableCharacter(CodingErrorAction.REPORT)
                        .decode(ByteBuffer.wrap(d)).toString();
            } catch (Exception e) {
                return new String(d, Charset.forName("GBK"));
            }
        } catch (Exception e) {
            return new String(d, StandardCharsets.UTF_8);
        }
    }

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (res != RESULT_OK || data == null) return;
        final Uri uri = data.getData();
        if (uri == null) return;

        if (req == REQ_IMG) {
            try {
                getContentResolver().takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
            } catch (Exception ignored) { }
            try {
                InputStream in = getContentResolver().openInputStream(uri);
                File f = new File(getFilesDir(), "bg.img");
                FileOutputStream out = new FileOutputStream(f);
                byte[] buf = new byte[8192];
                int n;
                while (in != null && (n = in.read(buf)) > 0) out.write(buf, 0, n);
                if (in != null) in.close();
                out.close();
                callJs("onBgPicked", f.getAbsolutePath());
            } catch (Exception e) {
                callJs("onBgPickFailed", String.valueOf(e.getMessage()));
            }
            return;
        }

        if (req == REQ_OPEN) {
            try {
                InputStream in = getContentResolver().openInputStream(uri);
                byte[] d = readAll(in);
                if (in != null) in.close();
                callJs("onTextOpened", displayName(this, uri), decode(d));
            } catch (Exception e) {
                callJs("onTextOpenFailed", String.valueOf(e.getMessage()));
            }
            return;
        }

        if (req == REQ_SAVE) {
            try {
                OutputStream out = getContentResolver().openOutputStream(uri, "wt");
                out.write(pendingSaveText.getBytes(StandardCharsets.UTF_8));
                out.close();
                callJs("onTextSaved", displayName(this, uri));
            } catch (Exception e) {
                callJs("onTextSaveFailed", String.valueOf(e.getMessage()));
            }
        }
    }

    /** 暴露给页面：window.RTG.xxx(...)。JS 桥回调在后台线程，凡碰 UI 的都切回主线程。 */
    public class Bridge {

        @JavascriptInterface
        public void copy(final String text) {
            runOnUiThread(new Runnable() {
                public void run() {
                    try {
                        ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                        cm.setPrimaryClip(ClipData.newPlainText("彩色文本生成器", text == null ? "" : text));
                        toast("已复制到剪贴板");
                    } catch (Exception e) {
                        toast("复制失败：" + e.getMessage());
                    }
                }
            });
        }

        @JavascriptInterface
        public void share(final String text) {
            runOnUiThread(new Runnable() {
                public void run() {
                    try {
                        Intent i = new Intent(Intent.ACTION_SEND);
                        i.setType("text/plain");
                        i.putExtra(Intent.EXTRA_TEXT, text == null ? "" : text);
                        startActivity(Intent.createChooser(i, "分享到"));
                    } catch (Exception e) {
                        toast("分享失败：" + e.getMessage());
                    }
                }
            });
        }

        @JavascriptInterface
        public void toast(final String text) {
            final String t = text == null ? "" : text;
            runOnUiThread(new Runnable() {
                public void run() {
                    Toast.makeText(MainActivity.this, t, Toast.LENGTH_SHORT).show();
                }
            });
        }

        @JavascriptInterface
        public String version() {
            return APP_VERSION;
        }

        @JavascriptInterface
        public int versionCode() {
            return APP_VERSION_CODE;
        }

        @JavascriptInterface
        public String platform() {
            return "android-" + Build.VERSION.SDK_INT;
        }

        /** 系统当前是深色还是浅色（用于「跟随系统」） */
        @JavascriptInterface
        public String systemTheme() {
            return isSystemDark() ? "dark" : "light";
        }

        /** 打开相册选背景图 */
        @JavascriptInterface
        public void pickImage() {
            runOnUiThread(new Runnable() {
                public void run() {
                    Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                    i.addCategory(Intent.CATEGORY_OPENABLE);
                    i.setType("image/*");
                    i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
                            | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
                    try {
                        startActivityForResult(i, REQ_IMG);
                    } catch (Exception e) {
                        toast("无法打开相册：" + e.getMessage());
                    }
                }
            });
        }

        /** 清除背景图 */
        @JavascriptInterface
        public void clearBg() {
            try {
                File f = new File(getFilesDir(), "bg.img");
                if (f.exists()) f.delete();
            } catch (Exception ignored) { }
        }

        /** 打开文本文件 */
        @JavascriptInterface
        public void openText() {
            runOnUiThread(new Runnable() {
                public void run() {
                    Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                    i.addCategory(Intent.CATEGORY_OPENABLE);
                    i.setType("*/*");
                    i.putExtra(Intent.EXTRA_MIME_TYPES, new String[]{
                            "text/plain", "text/markdown", "application/json",
                            "text/csv", "text/xml", "application/xml", "text/*"
                    });
                    i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                    try {
                        startActivityForResult(i, REQ_OPEN);
                    } catch (Exception e) {
                        toast("无法打开文件选择器：" + e.getMessage());
                    }
                }
            });
        }

        /** 另存为文本（UTF-8） */
        @JavascriptInterface
        public void saveText(final String name, final String text) {
            pendingSaveText = text == null ? "" : text;
            runOnUiThread(new Runnable() {
                public void run() {
                    Intent i = new Intent(Intent.ACTION_CREATE_DOCUMENT);
                    i.addCategory(Intent.CATEGORY_OPENABLE);
                    i.setType("text/plain");
                    i.putExtra(Intent.EXTRA_TITLE, (name == null || name.length() == 0) ? "彩色文本.txt" : name);
                    try {
                        startActivityForResult(i, REQ_SAVE);
                    } catch (Exception e) {
                        toast("无法打开保存对话框：" + e.getMessage());
                    }
                }
            });
        }

        /** 用系统浏览器打开链接（下载 APK / 发布页） */
        @JavascriptInterface
        public void openUrl(final String url) {
            runOnUiThread(new Runnable() {
                public void run() {
                    try {
                        startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)));
                    } catch (Exception e) {
                        toast("无法打开链接：" + e.getMessage());
                    }
                }
            });
        }

        /** 检查更新：后台线程拉 version.json，失败也会回调 */
        @JavascriptInterface
        public void checkUpdate() {
            new Thread(new Runnable() {
                public void run() {
                    HttpURLConnection c = null;
                    try {
                        URL u = new URL(UPDATE_URL + "?t=" + System.currentTimeMillis());
                        c = (HttpURLConnection) u.openConnection();
                        c.setConnectTimeout(8000);
                        c.setReadTimeout(8000);
                        c.setRequestProperty("User-Agent", "RichTextGen-Android");
                        int code = c.getResponseCode();
                        if (code != 200) throw new Exception("HTTP " + code);
                        InputStream in = c.getInputStream();
                        String body = new String(readAll(in), StandardCharsets.UTF_8);
                        in.close();
                        JSONObject all = new JSONObject(body);
                        JSONObject a = all.optJSONObject("android");
                        if (a == null) throw new Exception("清单中没有安卓版本信息");
                        callJs("onUpdateInfo",
                                a.optString("version", ""),
                                String.valueOf(a.optInt("versionCode", 0)),
                                a.optString("download", ""),
                                a.optString("notes", ""));
                    } catch (Exception e) {
                        callJs("onUpdateFailed", String.valueOf(e.getMessage()));
                    } finally {
                        if (c != null) c.disconnect();
                    }
                }
            }).start();
        }
    }
}
