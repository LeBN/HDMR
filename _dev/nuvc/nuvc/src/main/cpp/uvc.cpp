#include "uvc.h"
#include <libusb.h>
#include <android/log.h>
#include <cstring>
#include <ctime>
#include <pthread.h>
#include <sched.h>
#include <sys/resource.h>
#include <unistd.h>
#include <sys/syscall.h>

#define TAG "nuvc"
#define LI(...) __android_log_print(ANDROID_LOG_INFO,  TAG, __VA_ARGS__)
#define LW(...) __android_log_print(ANDROID_LOG_WARN,  TAG, __VA_ARGS__)
#define LE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

namespace nuvc {

// ---- UVC constants ---------------------------------------------------------
static const uint8_t CS_INTERFACE          = 0x24;
static const uint8_t VS_INPUT_HEADER        = 0x01;
static const uint8_t VS_FORMAT_UNCOMPRESSED = 0x04;
static const uint8_t VS_FRAME_UNCOMPRESSED  = 0x05;
static const uint8_t VS_FORMAT_MJPEG        = 0x06;
static const uint8_t VS_FRAME_MJPEG         = 0x07;

static const uint8_t SET_CUR = 0x01;
static const uint8_t GET_CUR = 0x81;
static const uint16_t VS_PROBE_CONTROL  = 0x0100;
static const uint16_t VS_COMMIT_CONTROL = 0x0200;

#pragma pack(push, 1)
struct StreamCtrl {              // UVC 1.0 = 26 bytes
    uint16_t bmHint;
    uint8_t  bFormatIndex;
    uint8_t  bFrameIndex;
    uint32_t dwFrameInterval;
    uint16_t wKeyFrameRate;
    uint16_t wPFrameRate;
    uint16_t wCompQuality;
    uint16_t wCompWindowSize;
    uint16_t wDelay;
    uint32_t dwMaxVideoFrameSize;
    uint32_t dwMaxPayloadTransferSize;
    // 1.1+: dwClockFrequency, bmFramingInfo, bPreferedVersion, bMinVersion, bMaxVersion
    uint32_t dwClockFrequency;
    uint8_t  bmFramingInfo;
    uint8_t  bPreferedVersion;
    uint8_t  bMinVersion;
    uint8_t  bMaxVersion;
};                              // 34 bytes (1.1)
#pragma pack(pop)

static int64_t now_ns() {
    timespec t; clock_gettime(CLOCK_MONOTONIC, &t);
    return (int64_t)t.tv_sec * 1000000000LL + t.tv_nsec;
}

// ---- open / close ---------------------------------------------------------
bool Uvc::open(int fd) {
    if (libusb_set_option(nullptr, LIBUSB_OPTION_NO_DEVICE_DISCOVERY, nullptr) != 0) {
        LE("set_option NO_DEVICE_DISCOVERY failed"); return false;
    }
    if (libusb_init(&ctx_) != 0) { LE("libusb_init failed"); return false; }
    int r = libusb_wrap_sys_device(ctx_, (intptr_t)fd, &h_);
    if (r != 0 || !h_) { LE("wrap_sys_device: %s", libusb_error_name(r)); close(); return false; }

    libusb_device* dev = libusb_get_device(h_);
    speed_ = libusb_get_device_speed(dev);
    LI("open ok  speed=%d (3=hi,4=super)", speed_);
    return true;
}

void Uvc::close() {
    stop();
    if (h_)   { libusb_close(h_); h_ = nullptr; }
    if (ctx_) { libusb_exit(ctx_); ctx_ = nullptr; }
}

// ---- descriptor scan -> pick a 720p mode ---------------------------------
static bool scan_720p(libusb_device_handle* h, int& vs_ifnum, uint8_t& ep_in, Mode& out) {
    libusb_device* dev = libusb_get_device(h);
    libusb_config_descriptor* cfg = nullptr;
    if (libusb_get_active_config_descriptor(dev, &cfg) != 0 || !cfg) {
        LE("no active config descriptor"); return false;
    }
    bool found = false;
    Mode best{};                        // fallback: smallest frame if no 720p

    for (int i = 0; i < cfg->bNumInterfaces && !found; i++) {
        const libusb_interface& itf = cfg->interface[i];
        for (int a = 0; a < itf.num_altsetting; a++) {
            const libusb_interface_descriptor& id = itf.altsetting[a];
            if (id.bInterfaceClass != 0x0E /*video*/ || id.bInterfaceSubClass != 0x02 /*streaming*/)
                continue;

            // bulk IN endpoint of this VS interface
            uint8_t ep = 0;
            for (int e = 0; e < id.bNumEndpoints; e++) {
                const auto& ed = id.endpoint[e];
                if ((ed.bEndpointAddress & 0x80) &&
                    (ed.bmAttributes & 0x03) == LIBUSB_TRANSFER_TYPE_BULK) {
                    ep = ed.bEndpointAddress; break;
                }
            }

            // walk the class-specific descriptors in .extra
            const uint8_t* p = id.extra;
            int left = id.extra_length;
            uint8_t cur_fmt_idx = 0; bool cur_mjpeg = false;
            while (left >= 3) {
                uint8_t len = p[0], type = p[1], sub = p[2];
                if (len < 3 || len > left) break;
                if (type == CS_INTERFACE) {
                    if (sub == VS_FORMAT_UNCOMPRESSED && len >= 5) {
                        cur_fmt_idx = p[3]; cur_mjpeg = false;
                    } else if (sub == VS_FORMAT_MJPEG && len >= 5) {
                        cur_fmt_idx = p[3]; cur_mjpeg = true;
                    } else if ((sub == VS_FRAME_UNCOMPRESSED || sub == VS_FRAME_MJPEG) && len >= 26) {
                        uint8_t  fr_idx = p[3];
                        uint16_t w = p[5] | (p[6] << 8);
                        uint16_t hgt = p[7] | (p[8] << 8);
                        uint32_t maxfs = p[17] | (p[18]<<8) | (p[19]<<16) | (uint32_t)(p[20]<<24);
                        uint32_t def_iv = p[21] | (p[22]<<8) | (p[23]<<16) | (uint32_t)(p[24]<<24);
                        Mode m{ cur_fmt_idx, fr_idx, w, hgt, def_iv, cur_mjpeg, maxfs };
                        LI("  mode fmt%u fr%u  %ux%u  %s  iv=%u maxfs=%u",
                           cur_fmt_idx, fr_idx, w, hgt, cur_mjpeg ? "MJPEG" : "raw", def_iv, maxfs);
                        if (best.w == 0 || (uint32_t)w * hgt < (uint32_t)best.w * best.h) best = m;
                        if (w == 1280 && hgt == 720 && !cur_mjpeg) { out = m; vs_ifnum = id.bInterfaceNumber; ep_in = ep; found = true; }
                    }
                }
                p += len; left -= len;
            }
            if (!found && best.w) {
                // remember VS iface/ep from any streaming altsetting with a bulk IN
                if (ep) { out = best; vs_ifnum = id.bInterfaceNumber; ep_in = ep; }
            }
        }
    }
    libusb_free_config_descriptor(cfg);
    if (out.w == 0 || vs_ifnum < 0 || ep_in == 0) { LE("no usable VS mode/endpoint"); return false; }
    if (!found) LW("no 1280x720 raw — falling back to %ux%u %s", out.w, out.h, out.mjpeg ? "MJPEG" : "raw");
    return true;
}

// ---- PROBE / COMMIT -----------------------------------------------------
bool Uvc::negotiate(const Mode& m) {
    StreamCtrl sc; memset(&sc, 0, sizeof(sc));
    sc.bmHint = 1;
    sc.bFormatIndex = m.fmt_index;
    sc.bFrameIndex  = m.frame_index;
    sc.dwFrameInterval = m.interval ? m.interval : 333333;  // 30fps default
    // ask for a large bulk payload — fewer URBs/frame if the card honours it
    sc.dwMaxPayloadTransferSize = 262144;

    const uint16_t wIndex = vs_ifnum_;
    int r;

    // SET_CUR PROBE
    r = libusb_control_transfer(h_, 0x21, SET_CUR, VS_PROBE_CONTROL, wIndex,
                                (uint8_t*)&sc, 26, 1000);
    if (r < 0) { LE("PROBE SET_CUR: %s", libusb_error_name(r)); return false; }

    // GET_CUR PROBE (device fills in maxima)
    StreamCtrl got; memset(&got, 0, sizeof(got));
    r = libusb_control_transfer(h_, 0xA1, GET_CUR, VS_PROBE_CONTROL, wIndex,
                                (uint8_t*)&got, 26, 1000);
    if (r < 0) { LE("PROBE GET_CUR: %s", libusb_error_name(r)); return false; }
    LI("probe -> fmt%u fr%u iv=%u maxframe=%u maxpayload=%u",
       got.bFormatIndex, got.bFrameIndex, got.dwFrameInterval,
       got.dwMaxVideoFrameSize, got.dwMaxPayloadTransferSize);

    max_payload_ = got.dwMaxPayloadTransferSize ? got.dwMaxPayloadTransferSize : 16384;
    mode_ = m;
    mode_.max_frame_size = got.dwMaxVideoFrameSize ? got.dwMaxVideoFrameSize : m.max_frame_size;

    // COMMIT with the negotiated struct
    r = libusb_control_transfer(h_, 0x21, SET_CUR, VS_COMMIT_CONTROL, wIndex,
                                (uint8_t*)&got, 26, 1000);
    if (r < 0) { LE("COMMIT SET_CUR: %s", libusb_error_name(r)); return false; }
    LI("commit ok");
    return true;
}

// ---- streaming ---------------------------------------------------------
bool Uvc::start_preferred(FrameCb cb) {
    if (!h_) return false;
    if (running_) return true;

    Mode m;
    if (!scan_720p(h_, vs_ifnum_, ep_in_, m)) return false;

    libusb_set_auto_detach_kernel_driver(h_, 1);
    int r = libusb_claim_interface(h_, vs_ifnum_);
    if (r != 0) { LE("claim VS iface %d: %s", vs_ifnum_, libusb_error_name(r)); return false; }

    if (!negotiate(m)) { libusb_release_interface(h_, vs_ifnum_); return false; }

    cb_ = std::move(cb);
    frame_.clear(); frame_.reserve(mode_.max_frame_size ? mode_.max_frame_size : (2u<<20));
    fid_ = -1;
    frames_ = bad_ = 0; last_bytes_ = 0; fps_ = 0;
    fps_t0_ = now_ns(); fps_n0_ = 0;

    // Deep ring: bulk transfers terminate on the card's short packet, so each
    // transfer carries exactly one UVC payload (~max_payload_ bytes). 720p NV12
    // is ~85 payloads/frame; at 60fps that's ~5000 transfers/s (~200us each).
    // With only a handful queued, any scheduler hiccup on the event thread
    // starves the endpoint and fps collapses. 32 deep = ~6ms of slack.
    const int N = (max_payload_ >= 131072) ? 16 : 64;
    const size_t BUF = max_payload_;
    buf_sz_ = BUF;
    ring_.resize(N, nullptr);
    ring_ptr_.resize(N, nullptr);
    running_ = true;

    // Try zero-copy kernel buffers (usbfs mmap) — big CPU win at this rate.
    dev_mem_ = true;
    for (int i = 0; i < N; i++) {
        ring_ptr_[i] = libusb_dev_mem_alloc(h_, BUF);
        if (!ring_ptr_[i]) { dev_mem_ = false; break; }
    }
    if (!dev_mem_) {
        for (int i = 0; i < N; i++)
            if (ring_ptr_[i]) { libusb_dev_mem_free(h_, ring_ptr_[i], BUF); ring_ptr_[i] = nullptr; }
        ring_buf_.resize(N);
        for (int i = 0; i < N; i++) { ring_buf_[i].resize(BUF); ring_ptr_[i] = ring_buf_[i].data(); }
    }

    for (int i = 0; i < N; i++) {
        libusb_transfer* x = libusb_alloc_transfer(0);
        libusb_fill_bulk_transfer(x, h_, ep_in_, ring_ptr_[i], (int)BUF,
                                  &Uvc::on_xfer, this, 1000);
        ring_[i] = x;
        r = libusb_submit_transfer(x);
        if (r != 0) { LE("submit[%d]: %s", i, libusb_error_name(r)); }
    }
    LI("streaming: %ux%u %s, %d transfers x %zu bytes, ep 0x%02x, %s buffers",
       mode_.w, mode_.h, mode_.mjpeg ? "MJPEG" : "raw", N, BUF, ep_in_,
       dev_mem_ ? "zero-copy" : "host");

    evt_ = std::thread(&Uvc::event_loop, this);
    return true;
}

void Uvc::stop() {
    if (!running_.exchange(false)) return;
    for (auto* x : ring_) if (x) libusb_cancel_transfer(x);
    // let the event loop drain the cancellations
    if (evt_.joinable()) evt_.join();
    for (auto* x : ring_) if (x) libusb_free_transfer(x);
    if (dev_mem_ && h_)
        for (auto* p : ring_ptr_) if (p) libusb_dev_mem_free(h_, p, buf_sz_);
    ring_.clear(); ring_buf_.clear(); ring_ptr_.clear(); dev_mem_ = false;
    if (h_ && vs_ifnum_ >= 0) libusb_release_interface(h_, vs_ifnum_);
    LI("stopped: %llu frames, %llu bad", (unsigned long long)frames_, (unsigned long long)bad_);
}

void Uvc::event_loop() {
    // This thread services ~5000 URB completions/s — it must not be starved.
    // Android drops a normal app off the big cores a few seconds after launch
    // (the launch cpu-boost expires) which halves our throughput; pin to the
    // performance cluster and raise priority (best-effort — may lack perms).
    setpriority(PRIO_PROCESS, (id_t)syscall(SYS_gettid), -19);
    { sched_param sp{}; sp.sched_priority = 2;
      pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp); }
    { cpu_set_t set; CPU_ZERO(&set);
      long n = sysconf(_SC_NPROCESSORS_CONF);
      for (long c = (n > 4 ? n - 4 : 0); c < n; c++) CPU_SET(c, &set);   // top 4 cores
      sched_setaffinity((pid_t)syscall(SYS_gettid), sizeof(set), &set); }

    timeval tv{0, 100000};
    while (running_) libusb_handle_events_timeout_completed(ctx_, &tv, nullptr);
    // drain
    for (int i = 0; i < 20; i++) libusb_handle_events_timeout_completed(ctx_, &tv, nullptr);
}

void LIBUSB_CALL Uvc::on_xfer(libusb_transfer* x) {
    static_cast<Uvc*>(x->user_data)->handle_xfer(x);
}

void Uvc::handle_xfer(libusb_transfer* x) {
    if (!running_) return;

    if (x->status == LIBUSB_TRANSFER_COMPLETED && x->actual_length >= 2) {
        const uint8_t* buf = x->buffer;
        int total = x->actual_length;
        uint8_t hlen = buf[0];
        uint8_t info = buf[1];
        if (hlen >= 2 && hlen <= total && !(info & 0x40 /*ERR*/)) {
            int fid = info & 0x01;
            bool eof = (info & 0x02) != 0;
            if (fid_ >= 0 && fid != fid_ && !frame_.empty()) {
                // lost EOF — flush what we have as a (possibly short) frame
                bad_++;
                frame_.clear();
            }
            fid_ = fid;
            const uint8_t* payload = buf + hlen;
            int plen = total - hlen;
            if (plen > 0) frame_.insert(frame_.end(), payload, payload + plen);

            if (eof) {
                if (!frame_.empty()) {
                    last_bytes_ = (uint32_t)frame_.size();
                    frames_++;
                    if (cb_) cb_(frame_.data(), frame_.size(), mode_.w, mode_.h, mode_.mjpeg);
                    // fps every ~1s
                    int64_t t = now_ns();
                    if (t - fps_t0_ > 1000000000LL) {
                        fps_ = (float)((frames_ - fps_n0_) * 1e9 / (t - fps_t0_));
                        fps_t0_ = t; fps_n0_ = frames_;
                        LI("fps=%.1f  last=%u bytes  bad=%llu",
                           (float)fps_, (uint32_t)last_bytes_, (unsigned long long)bad_);
                    }
                }
                frame_.clear();
            }
        }
    } else if (x->status == LIBUSB_TRANSFER_CANCELLED) {
        return;   // don't resubmit
    } else if (x->status != LIBUSB_TRANSFER_COMPLETED) {
        LW("xfer status=%d", x->status);
    }

    if (running_) {
        int r = libusb_submit_transfer(x);
        if (r != 0) LW("resubmit: %s", libusb_error_name(r));
    }
}

} // namespace nuvc
