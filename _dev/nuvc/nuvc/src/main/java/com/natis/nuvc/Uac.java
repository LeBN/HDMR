package com.natis.nuvc;

import android.content.Context;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbManager;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.util.Log;

/**
 * From-scratch USB Audio Class capture: the Android audio HAL cannot sustain
 * a read from this card's isochronous endpoint (confirmed independent of any
 * video USB traffic — a HAL/device incompatibility, not bandwidth contention).
 * This claims the AudioStreaming interface directly via libusb (uac.cpp) on
 * its OWN UsbDeviceConnection/fd and its OWN libusb_context — entirely
 * separate from Nuvc's video context — and drains PCM into an AudioTrack on
 * its own Java thread.
 */
public final class Uac {
    private static final String TAG = "nuvc-uac";
    static { System.loadLibrary("nuvc"); }

    private UsbDeviceConnection conn;
    private long handle;
    private Thread playThread;
    private volatile boolean running;
    public volatile String status = "idle";

    /** Open a second connection to the same card and start isochronous capture + playback. */
    public boolean start(Context ctx) {
        if (handle != 0) return true;
        UsbManager um = (UsbManager) ctx.getSystemService(Context.USB_SERVICE);
        if (um == null) { status = "no UsbManager"; return false; }
        UsbDevice dev = null;
        for (UsbDevice d : um.getDeviceList().values()) { if (isUvc(d)) { dev = d; break; } }
        if (dev == null) { status = "no capture card"; return false; }
        if (!um.hasPermission(dev)) { status = "no USB permission"; return false; }

        UsbDeviceConnection c = um.openDevice(dev);
        if (c == null) { status = "openDevice failed"; return false; }
        long h = nativeUacOpen(c.getFileDescriptor());
        if (h == 0) { c.close(); status = "nativeUacOpen failed"; return false; }
        if (!nativeUacStart(h)) { nativeUacClose(h); c.close(); status = "no isochronous AS interface"; return false; }

        conn = c; handle = h;
        running = true;
        playThread = new Thread(this::playLoop, "nuvc-uac-play");
        playThread.setPriority(Thread.MAX_PRIORITY);
        playThread.start();
        return true;
    }

    public void stop() {
        running = false;
        if (playThread != null) { try { playThread.join(500); } catch (InterruptedException ignored) {} playThread = null; }
        if (handle != 0) { nativeUacClose(handle); handle = 0; }
        if (conn != null) { conn.close(); conn = null; }
        status = "stopped";
    }

    private void playLoop() {
        int[] info = nativeUacInfo(handle);
        int rate = info[0] > 0 ? info[0] : 48000;
        int ch = info[1] == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
        AudioTrack tr = null;
        try {
            int minBuf = AudioTrack.getMinBufferSize(rate, ch, AudioFormat.ENCODING_PCM_16BIT);
            tr = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                            .setFlags(AudioAttributes.FLAG_LOW_LATENCY)
                            .build())
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(rate).setChannelMask(ch).build())
                    .setBufferSizeInBytes(Math.max(minBuf, 4096))
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build();
            tr.play();
            status = "capturing " + rate + "Hz " + info[1] + "ch";
            Log.i(TAG, status);

            byte[] buf = new byte[3840];   // ~20ms @ 48kHz stereo 16-bit
            int silence = 0;
            while (running) {
                int n = nativeUacRead(handle, buf, buf.length);
                if (n > 0) { tr.write(buf, 0, n); silence = 0; }
                else { silence++; try { Thread.sleep(2); } catch (InterruptedException ignored) {} }
                if (silence == 250) status = "no data from card (250 empty reads)";
            }
        } catch (Exception e) {
            status = "playback error: " + e.getMessage();
            Log.e(TAG, "playLoop", e);
        } finally {
            try { if (tr != null) { tr.stop(); tr.release(); } } catch (Exception ignored) {}
        }
    }

    private static boolean isUvc(UsbDevice d) {
        if (d.getDeviceClass() == 0x0E) return true;
        for (int i = 0; i < d.getInterfaceCount(); i++)
            if (d.getInterface(i).getInterfaceClass() == UsbConstants.USB_CLASS_VIDEO) return true;
        return false;
    }

    private static native long nativeUacOpen(int fd);
    private static native boolean nativeUacStart(long h);
    private static native void nativeUacStop(long h);
    private static native void nativeUacClose(long h);
    private static native int nativeUacRead(long h, byte[] dst, int len);
    private static native int[] nativeUacInfo(long h);
}
