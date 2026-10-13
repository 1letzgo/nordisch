package de.letzgo.legamez;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInstaller;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * In-app update for GitHub release APKs (called from LeGamez.Core.AppUpdater): streams the downloaded APK into a
 * PackageInstaller session; Android then shows its own "update this app?" confirmation. Same package + same release
 * signature, so the update replaces the running app and keeps its data.
 */
public final class NordischUpdater {
    private static final String ACTION = "de.letzgo.legamez.UPDATE_STATUS";
    private static volatile String status = "";
    private static BroadcastReceiver receiver;

    private NordischUpdater() {}

    /** Empty while idle/running, "pending" while Android asks the user, else "ok" or an error message. */
    public static String getStatus() { return status; }

    /** Android 8+: the user must allow this app to install apps ("unknown sources") once. */
    public static boolean canInstall(Activity activity) {
        return Build.VERSION.SDK_INT < 26 || activity.getPackageManager().canRequestPackageInstalls();
    }

    public static void openInstallPermission(Activity activity) {
        Intent i = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + activity.getPackageName()));
        activity.startActivity(i);
    }

    public static void install(final Activity activity, final String apkPath) {
        status = "";
        registerReceiver(activity);
        new Thread(() -> {
            try {
                PackageInstaller installer = activity.getPackageManager().getPackageInstaller();
                PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
                params.setAppPackageName(activity.getPackageName());
                int id = installer.createSession(params);
                File apk = new File(apkPath);
                try (PackageInstaller.Session session = installer.openSession(id)) {
                    try (InputStream in = new FileInputStream(apk);
                         OutputStream out = session.openWrite("base.apk", 0, apk.length())) {
                        byte[] buf = new byte[1 << 16];
                        int n;
                        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                        session.fsync(out);
                    }
                    Intent intent = new Intent(ACTION).setPackage(activity.getPackageName());
                    int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
                    PendingIntent pi = PendingIntent.getBroadcast(activity, id, intent, flags);
                    session.commit(pi.getIntentSender());
                }
            } catch (Exception e) {
                status = "error: " + e;
            }
        }).start();
    }

    private static synchronized void registerReceiver(Activity activity) {
        if (receiver != null) return;
        receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                int s = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
                if (s == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                    Intent confirm = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                    if (confirm != null) {
                        confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                        context.startActivity(confirm);
                    }
                    status = "pending";
                } else if (s == PackageInstaller.STATUS_SUCCESS) {
                    status = "ok";
                } else {
                    String msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                    status = "error: " + (msg != null ? msg : ("status " + s));
                }
            }
        };
        IntentFilter filter = new IntentFilter(ACTION);
        Context app = activity.getApplicationContext();
        if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
        else app.registerReceiver(receiver, filter);
    }
}
