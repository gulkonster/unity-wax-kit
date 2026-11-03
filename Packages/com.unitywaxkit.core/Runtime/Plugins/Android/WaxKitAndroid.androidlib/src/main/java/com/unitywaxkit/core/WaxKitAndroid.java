package com.unitywaxkit.core;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Bundle;

/** Android side of the Unity WAX Kit: wallet links and returning to the game. The in-app browser is in WaxKitBrowser. */
public final class WaxKitAndroid {
    private WaxKitAndroid() {
    }

    /** True when an installed app handles the link (needs a matching queries entry on Android 11+). */
    public static boolean canOpen(Activity activity, String url) {
        Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(url));
        return !activity.getPackageManager().queryIntentActivities(intent, 0).isEmpty();
    }

    /** Opens a wallet link (esr:, anchor:) in its app. False when no app handles it. */
    public static boolean openLink(Activity activity, String url) {
        try {
            Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(url));
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            activity.startActivity(intent);
            return true;
        } catch (ActivityNotFoundException e) {
            return false;
        }
    }

    /** Brings the game activity back in front; anything started above it in its task (the Custom Tab) is finished. */
    public static void bringToFront(Activity activity) {
        Intent intent = new Intent(activity, activity.getClass());
        intent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        activity.startActivity(intent);
    }

    /** A string from the application's meta-data, or null. */
    public static String metaData(Activity activity, String key) {
        try {
            ApplicationInfo info = activity.getPackageManager()
                    .getApplicationInfo(activity.getPackageName(), PackageManager.GET_META_DATA);
            Bundle data = info.metaData;
            return data == null ? null : data.getString(key);
        } catch (PackageManager.NameNotFoundException e) {
            return null;
        }
    }
}
