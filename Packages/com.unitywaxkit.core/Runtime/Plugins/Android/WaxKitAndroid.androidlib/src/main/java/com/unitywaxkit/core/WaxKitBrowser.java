package com.unitywaxkit.core;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.net.Uri;

import androidx.browser.customtabs.CustomTabsClient;
import androidx.browser.customtabs.CustomTabsIntent;

import java.util.Collections;

/**
 * The in-app browser (Custom Tabs) for the wallet pages of My Cloud Wallet and WebAuth web.
 */
public final class WaxKitBrowser {
    private static final int BROWSER_REQUEST = 0x5741;
    private static final String CHROME = "com.android.chrome";
    // Manifest meta-data written by the build step (Project Settings > Unity WAX Kit); missing = on.
    private static final String PREFER_CHROME = "waxkit.mycloudwallet.chrome";

    private WaxKitBrowser() {
    }

    /** True when a browser that supports Custom Tabs is installed. */
    public static boolean canUseCustomTabs(Activity activity) {
        return customTabsBrowser(activity) != null;
    }

    /**
     * The browser for Custom Tabs. My Cloud Wallet signs with a passkey (WebAuthn), which some default browsers do not
     * support (for example the own browser on many Xiaomi phones: login works there, signing does not). So Chrome is
     * used whenever it is installed and enabled, whatever the default browser is, unless the game turned this off.
     * Otherwise the default browser, when it supports Custom Tabs.
     */
    private static String customTabsBrowser(Activity activity) {
        if (preferChrome(activity)) {
            // Only finds Chrome when it is installed, enabled and offers Custom Tabs.
            String chrome = CustomTabsClient.getPackageName(activity, Collections.singletonList(CHROME), true);
            if (chrome != null) return chrome;
        }
        return CustomTabsClient.getPackageName(activity, null);
    }

    private static boolean preferChrome(Activity activity) {
        try {
            ApplicationInfo info = activity.getPackageManager()
                    .getApplicationInfo(activity.getPackageName(), PackageManager.GET_META_DATA);
            return info.metaData == null || info.metaData.getBoolean(PREFER_CHROME, true);
        } catch (PackageManager.NameNotFoundException e) {
            return true;
        }
    }

    /**
     * Opens the url in a Custom Tab on top of the game: a resizable bottom sheet (partial Custom Tab) where the
     * browser supports it, otherwise full height. Started for a result, so it stays in the game's task.
     * Without any browser that supports Custom Tabs (for example a phone without Chrome) the url opens in the
     * normal browser instead, outside the game. Returns true for a Custom Tab, false for the normal browser.
     */
    public static boolean openBrowser(Activity activity, String url, int heightPx) {
        String browser = customTabsBrowser(activity);
        if (browser == null) {
            openPlainBrowser(activity, url);
            return false;
        }
        CustomTabsIntent.Builder builder = new CustomTabsIntent.Builder()
                .setShowTitle(true)
                .setShareState(CustomTabsIntent.SHARE_STATE_OFF)
                .setUrlBarHidingEnabled(true);
        if (heightPx > 0) {
            builder.setInitialActivityHeightPx(heightPx, CustomTabsIntent.ACTIVITY_HEIGHT_ADJUSTABLE);
        }
        CustomTabsIntent tab = builder.build();
        tab.intent.setPackage(browser);
        tab.intent.setData(Uri.parse(url));
        try {
            activity.startActivityForResult(tab.intent, BROWSER_REQUEST);
            return true;
        } catch (ActivityNotFoundException e) {
            openPlainBrowser(activity, url);
            return false;
        }
    }

    private static void openPlainBrowser(Activity activity, String url) {
        Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(url));
        intent.addCategory(Intent.CATEGORY_BROWSABLE);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        activity.startActivity(intent);
    }
}
