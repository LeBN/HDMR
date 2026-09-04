package com.natis.nuvc;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioDeviceInfo;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.util.Log;

/**
 * Card audio, entirely outside the video USB context. The capture card's UAC
 * interface is left unclaimed by nuvc (we claim only VideoStreaming), so the
 * Android audio HAL owns it — we just AudioRecord from the USB input device and
 * AudioTrack it to the headset. Its own thread; never touches libusb.
 *
 * This is the fix for the old plugin's SIGSEGV: that crash came from claiming
 * the UAC interface inside the video event loop. Here there is no such coupling.
 */
public final class UsbAudio {
    private static final String TAG = "nuvc-audio";

    private volatile boolean running;
    private Thread thread;
    public volatile String status = "idle";

    /** Start loopback. Safe to call when no USB audio device is present (no-op + status). */
    public synchronized boolean start(Context ctx) {
        if (running) return true;
        AudioManager am = (AudioManager) ctx.getSystemService(Context.AUDIO_SERVICE);
        if (am == null) { status = "no AudioManager"; return false; }

        AudioDeviceInfo in = null;
        for (AudioDeviceInfo d : am.getDevices(AudioManager.GET_DEVICES_INPUTS)) {
            int t = d.getType();
            if (t == AudioDeviceInfo.TYPE_USB_DEVICE || t == AudioDeviceInfo.TYPE_USB_HEADSET
                || t == AudioDeviceInfo.TYPE_USB_ACCESSORY) { in = d; break; }
        }
        if (in == null) { status = "no USB audio input (HAL hasn't claimed the card's UAC?)"; return false; }

        final AudioDeviceInfo dev = in;
        final int rate = pickRate(in);
        running = true;
        thread = new Thread(() -> loop(dev, rate), "nuvc-audio");
        thread.setPriority(Thread.MAX_PRIORITY);
        thread.start();
        return true;
    }

    public synchronized void stop() {
        running = false;
        if (thread != null) { try { thread.join(500); } catch (InterruptedException ignored) {} thread = null; }
        status = "stopped";
    }

    private static int pickRate(AudioDeviceInfo d) {
        int best = 48000;
        int[] rates = d.getSampleRates();
        if (rates != null && rates.length > 0) {
            best = 0;
            for (int r : rates) if (r == 48000) return 48000; else if (r > best) best = r;
            if (best == 0) best = 48000;
        }
        return best;
    }

    private void loop(AudioDeviceInfo in, int rate) {
        AudioRecord rec = null;
        AudioTrack tr = null;
        try {
            int inCfg = AudioFormat.CHANNEL_IN_STEREO;
            int fmt = AudioFormat.ENCODING_PCM_16BIT;
            int minRec = AudioRecord.getMinBufferSize(rate, inCfg, fmt);
            int minTrk = AudioTrack.getMinBufferSize(rate, AudioFormat.CHANNEL_OUT_STEREO, fmt);
            int buf = Math.max(minRec, minTrk);

            rec = new AudioRecord(MediaRecorder.AudioSource.UNPROCESSED, rate, inCfg, fmt, buf * 2);
            if (rec.getState() != AudioRecord.STATE_INITIALIZED) {
                rec = new AudioRecord(MediaRecorder.AudioSource.MIC, rate, inCfg, fmt, buf * 2);
            }
            rec.setPreferredDevice(in);

            tr = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                            .setFlags(AudioAttributes.FLAG_LOW_LATENCY)
                            .build())
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(fmt).setSampleRate(rate)
                            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).build())
                    .setBufferSizeInBytes(buf)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build();

            rec.startRecording();
            tr.play();
            status = "loopback " + rate + "Hz";
            Log.i(TAG, status);

            byte[] b = new byte[buf];
            while (running) {
                int n = rec.read(b, 0, b.length);
                if (n > 0) tr.write(b, 0, n);
                else if (n < 0) { status = "read err " + n; break; }
            }
        } catch (Exception e) {
            status = "error: " + e.getMessage();
            Log.e(TAG, "loop", e);
        } finally {
            try { if (rec != null) { rec.stop(); rec.release(); } } catch (Exception ignored) {}
            try { if (tr != null) { tr.stop(); tr.release(); } } catch (Exception ignored) {}
        }
    }
}
