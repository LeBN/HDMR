package com.natis.nuvc;

import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbManager;
import android.os.Build;
import android.util.Log;

import java.nio.ByteBuffer;

/**
 * nuvc — from-scratch USB Video Class capture. P1: open a UVC device by fd,
 * negotiate a 720p mode, bulk-stream, expose frame stats. No decode, no display.
 *
 * Usage (Java owns the fd for the connection's lifetime):
 *   Nuvc n = new Nuvc();
 *   n.openFirstUvc(context);   // needs USB permission already granted
 *   n.start();
 *   ... poll n.stats() ...
 *   n.stop(); n.close();
 */
public final class Nuvc {
    static { System.loadLibrary("nuvc"); }
    private static final String TAG = "nuvc";

    private static final String ACTION_PERM = "com.natis.nuvc.USB_PERMISSION";

    private UsbDeviceConnection conn;
    private long handle;
    private volatile boolean permRequested;
    private BroadcastReceiver permRx;

    /**
     * Convenience for a host with no USB plumbing of its own (e.g. Unity).
     * Returns true once the device is open. If permission is missing it fires
     * one system permission request and returns false — call again (poll) until
     * it returns true or {@link #permissionDenied()} is true.
     */
    public boolean ensureOpen(Context ctx) {
        if (handle != 0) return true;
        UsbManager um = (UsbManager) ctx.getSystemService(Context.USB_SERVICE);
        if (um == null) return false;
        UsbDevice dev = null;
        for (UsbDevice d : um.getDeviceList().values()) { if (isUvc(d)) { dev = d; break; } }
        if (dev == null) return false;
        if (um.hasPermission(dev)) return openFirstUvc(ctx);
        if (!permRequested) {
            permRequested = true;
            final Context app = ctx.getApplicationContext();
            permRx = new BroadcastReceiver() {
                @Override public void onReceive(Context c, Intent i) {
                    if (ACTION_PERM.equals(i.getAction())) {
                        boolean ok = i.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false);
                        Log.i(TAG, "USB permission " + (ok ? "granted" : "denied"));
                        permDenied = !ok;
                        try { app.unregisterReceiver(this); } catch (Exception ignored) {}
                        permRx = null;
                    }
                }
            };
            IntentFilter f = new IntentFilter(ACTION_PERM);
            if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(permRx, f, Context.RECEIVER_NOT_EXPORTED);
            else app.registerReceiver(permRx, f);
            int flags = Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0;
            PendingIntent pi = PendingIntent.getBroadcast(app, 0,
                    new Intent(ACTION_PERM).setPackage(app.getPackageName()), flags);
            um.requestPermission(dev, pi);
            Log.i(TAG, "requested USB permission for " + dev.getDeviceName());
        }
        return false;
    }

    private volatile boolean permDenied;
    public boolean permissionDenied() { return permDenied; }

    /** Open the first attached UVC device we have permission for. */
    public boolean openFirstUvc(Context ctx) {
        UsbManager um = (UsbManager) ctx.getSystemService(Context.USB_SERVICE);
        if (um == null) return false;
        for (UsbDevice d : um.getDeviceList().values()) {
            if (!isUvc(d)) continue;
            if (!um.hasPermission(d)) { Log.w(TAG, "no permission for " + d.getDeviceName()); continue; }
            UsbDeviceConnection c = um.openDevice(d);
            if (c == null) { Log.e(TAG, "openDevice null"); continue; }
            int fd = c.getFileDescriptor();
            Log.i(TAG, String.format("opening %04x:%04x fd=%d", d.getVendorId(), d.getProductId(), fd));
            long h = nativeOpen(fd);
            if (h == 0) { c.close(); Log.e(TAG, "nativeOpen failed"); continue; }
            conn = c; handle = h;
            return true;
        }
        Log.e(TAG, "no permitted UVC device");
        return false;
    }

    /** Open a specific device the caller already has permission for. */
    public boolean open(UsbManager um, UsbDevice d) {
        UsbDeviceConnection c = um.openDevice(d);
        if (c == null) return false;
        long h = nativeOpen(c.getFileDescriptor());
        if (h == 0) { c.close(); return false; }
        conn = c; handle = h;
        return true;
    }

    public boolean start() { return handle != 0 && nativeStart(handle); }
    public void stop()     { if (handle != 0) nativeStop(handle); }

    public void close() {
        if (handle != 0) { nativeClose(handle); handle = 0; }
        if (conn != null) { conn.close(); conn = null; }
    }

    /** [frames, badFrames, lastBytes, fpsx100, w, h, mjpeg, linkSpeed, fmt] */
    public int[] stats() { return handle != 0 ? nativeStats(handle) : new int[9]; }

    public static final int FMT_NV12 = 0, FMT_YUY2 = 1, FMT_MJPEG = 2;

    /**
     * Copy the newest frame into {@code dst} (must be a direct ByteBuffer big
     * enough for one frame — w*h*2 covers every raw format). Returns the frame's
     * sequence number (monotonic from 1), or 0 if nothing is newer than
     * {@code since}. Pass the last returned value as {@code since} to poll.
     * The consumer never blocks the USB thread beyond one memcpy.
     */
    public long latestInto(ByteBuffer dst, long since) {
        return handle != 0 ? nativeLatestInto(handle, dst, since) : 0;
    }

    /** Copy the newest frame to a raw address (Unity: a pinned NativeArray ptr). */
    public long latestIntoAddr(long addr, long cap, long since) {
        return handle != 0 ? nativeLatestIntoAddr(handle, addr, cap, since) : 0;
    }

    /** [now_ns, newest-frame EOF ns, last frame USB receive span us]. */
    public long[] lat() { return handle != 0 ? nativeLat(handle) : new long[3]; }

    /** One-shot: logs every interface/altsetting/endpoint on the whole device (logcat tag "nuvc"). */
    public void dumpAll() { if (handle != 0) nativeDumpAll(handle); }

    private static boolean isUvc(UsbDevice d) {
        if (d.getDeviceClass() == 0x0E /*video*/) return true;
        for (int i = 0; i < d.getInterfaceCount(); i++)
            if (d.getInterface(i).getInterfaceClass() == UsbConstants.USB_CLASS_VIDEO) return true;
        return false;
    }

    private static native long nativeOpen(int fd);
    private static native boolean nativeStart(long h);
    private static native void nativeStop(long h);
    private static native void nativeClose(long h);
    private static native int[] nativeStats(long h);
    private static native long nativeLatestInto(long h, ByteBuffer dst, long since);
    private static native long nativeLatestIntoAddr(long h, long addr, long cap, long since);
    private static native long[] nativeLat(long h);
    private static native void nativeDumpAll(long h);
}
