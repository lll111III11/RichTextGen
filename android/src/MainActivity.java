package com.richtextgen.android;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.view.ViewGroup;
import android.webkit.JavascriptInterface;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.widget.Toast;

/**
 * 彩色文本生成器 · 安卓版（WebView 外壳）
 *
 * 界面全部由 assets/index.html 提供（同一份 HTML 用 CSS 媒体查询做 竖屏精简 / 横屏增强 两套布局）。
 * 这里只提供手机必须由原生完成的两件事：剪贴板、系统分享。
 */
public class MainActivity extends Activity {

    private static final String APP_VERSION = "1.0.0";

    private WebView web;
    private long lastBack = 0L;

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);

        web = new WebView(this);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);        // localStorage：记住主题与最近用色
        s.setAllowFileAccess(true);
        s.setBuiltInZoomControls(false);
        s.setDisplayZoomControls(false);
        s.setTextZoom(100);

        web.setBackgroundColor(0xFFF3F3F3);
        web.addJavascriptInterface(new Bridge(), "RTG");
        web.loadUrl("file:///android_asset/index.html");

        setContentView(web, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
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
        public String platform() {
            return "android-" + Build.VERSION.SDK_INT;
        }
    }
}
