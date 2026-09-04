package com.natis.nuvc;

import android.content.Context;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbManager;
import android.util.Log;

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

    private UsbDeviceConnection conn;
    private long handle;

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

    /** [frames, badFrames, lastBytes, fpsx100, w, h, mjpeg, linkSpeed] */
    public int[] stats() { return handle != 0 ? nativeStats(handle) : new int[8]; }

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
}
