package com.natis.nuvctest;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Gravity;
import android.widget.TextView;

import com.natis.nuvc.Nuvc;

import java.nio.ByteBuffer;

public class MainActivity extends Activity {
    private static final String TAG = "nuvctest";
    private static final String ACTION_PERM = "com.natis.nuvctest.USB_PERM";

    private final Nuvc nuvc = new Nuvc();
    private TextView tv;
    private final Handler h = new Handler(Looper.getMainLooper());
    private boolean started;
    private long t0;
    private final ByteBuffer frameBuf = ByteBuffer.allocateDirect(3840 * 2160 * 2);
    private long lastSeq;
    private long pulls;

    private final BroadcastReceiver rx = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            if (!ACTION_PERM.equals(i.getAction())) return;
            boolean ok = i.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false);
            Log.i(TAG, "permission result: " + ok);
            if (ok) go();
            else say("USB permission denied");
        }
    };

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        tv = new TextView(this);
        tv.setTextSize(22);
        tv.setPadding(48, 48, 48, 48);
        tv.setGravity(Gravity.TOP | Gravity.START);
        tv.setTextColor(Color.WHITE);
        tv.setBackgroundColor(Color.rgb(12, 16, 20));
        setContentView(tv);
        say("nuvc test — looking for the capture card…");

        IntentFilter f = new IntentFilter(ACTION_PERM);
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(rx, f, Context.RECEIVER_NOT_EXPORTED);
        else registerReceiver(rx, f);

        requestOrGo();
    }

    private void requestOrGo() {
        UsbManager um = (UsbManager) getSystemService(Context.USB_SERVICE);
        UsbDevice dev = null;
        for (UsbDevice d : um.getDeviceList().values()) {
            if (d.getVendorId() == 0x345F || d.getDeviceClass() == UsbConstants.USB_CLASS_VIDEO) { dev = d; break; }
            for (int k = 0; k < d.getInterfaceCount(); k++)
                if (d.getInterface(k).getInterfaceClass() == UsbConstants.USB_CLASS_VIDEO) { dev = d; break; }
        }
        if (dev == null) { say("no capture card attached — plug it in and reopen"); return; }
        Log.i(TAG, String.format("device %04x:%04x  perm=%b", dev.getVendorId(), dev.getProductId(), um.hasPermission(dev)));
        if (um.hasPermission(dev)) { go(); return; }

        int flags = Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0;
        PendingIntent pi = PendingIntent.getBroadcast(this, 0,
                new Intent(ACTION_PERM).setPackage(getPackageName()), flags);
        um.requestPermission(dev, pi);
        say("asking for USB permission…");
    }

    private void go() {
        if (started) return;
        boolean opened = nuvc.openFirstUvc(this);
        Log.i(TAG, "openFirstUvc -> " + opened);
        if (!opened) { say("open failed — see logcat"); return; }
        boolean s = nuvc.start();
        Log.i(TAG, "start -> " + s);
        if (!s) { say("start failed — see logcat"); return; }
        started = true;
        t0 = System.currentTimeMillis();
        h.post(poll);
    }

    private final Runnable poll = new Runnable() {
        @Override public void run() {
            int[] s = nuvc.stats();
            String speed = s[7] == 4 ? "USB3 SuperSpeed" : s[7] == 3 ? "USB2 High" : "speed " + s[7];
            String fmt = s.length > 8 && s[8] == 2 ? "MJPEG" : s.length > 8 && s[8] == 1 ? "YUY2" : "NV12";
            long sec = (System.currentTimeMillis() - t0) / 1000;

            // pull the newest frame and fingerprint it — proves real image bytes
            frameBuf.clear();
            long seq = nuvc.latestInto(frameBuf, lastSeq);
            String pull = "waiting for first frame";
            if (seq != 0) {
                lastSeq = seq; pulls++;
                int n = s[2] > 0 ? s[2] : 1382400;
                long sum = 0; int step = Math.max(1, n / 4096);
                for (int i = 0; i < n; i += step) sum += (frameBuf.get(i) & 0xff);
                int y0 = frameBuf.get(0) & 0xff, ymid = frameBuf.get(n / 3) & 0xff;
                pull = String.format("pulled %d  seq=%d  Ysum≈%d  Y[0]=%d Y[n/3]=%d", pulls, seq, sum, y0, ymid);
            }

            say(String.format(
                "RUNNING  %ds%n%n" +
                "link      %s%n" +
                "mode      %dx%d  %s%n" +
                "frames    %d   (bad %d)%n" +
                "fps       %.1f%n" +
                "last frame %,d bytes  (NV12 = w*h*3/2 = %,d)%n" +
                "consumer  %s%n",
                sec, speed, s[4], s[5], fmt,
                s[0], s[1], s[3] / 100f, s[2], s[4] * s[5] * 3 / 2, pull));
            h.postDelayed(this, 500);
        }
    };

    private void say(String m) { h.post(() -> tv.setText(m)); Log.i(TAG, m.replace("\n", " | ")); }

    @Override protected void onDestroy() {
        super.onDestroy();
        h.removeCallbacksAndMessages(null);
        try { unregisterReceiver(rx); } catch (Exception ignored) {}
        nuvc.stop();
        nuvc.close();
    }
}
