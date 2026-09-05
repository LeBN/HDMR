#include "uac.h"
#include <libusb.h>
#include <android/log.h>
#include <cstring>

#define TAG "nuvc-uac"
#define LI(...) __android_log_print(ANDROID_LOG_INFO,  TAG, __VA_ARGS__)
#define LW(...) __android_log_print(ANDROID_LOG_WARN,  TAG, __VA_ARGS__)
#define LE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

namespace nuvc {

static const uint8_t CLASS_AUDIO          = 0x01;
static const uint8_t SUB_AUDIOCONTROL     = 0x01;
static const uint8_t SUB_AUDIOSTREAMING   = 0x02;
static const uint8_t CS_INTERFACE         = 0x24;
static const uint8_t AS_FORMAT_TYPE       = 0x02;
static const uint8_t AC_FEATURE_UNIT      = 0x06;
static const uint8_t UAC_SET_CUR          = 0x01;
static const uint8_t UAC_MUTE_CONTROL     = 0x01;
static const uint8_t UAC_FU_CONTROL_SEL_SHIFT = 8;

bool Uac::open(int fd) {
    // Android sandboxes /dev/bus/usb — without this a fresh context tries to
    // enumerate it and libusb_init fails (matches Uvc::open()'s fix).
    if (libusb_set_option(nullptr, LIBUSB_OPTION_NO_DEVICE_DISCOVERY, nullptr) != 0)
        LW("set_option NO_DEVICE_DISCOVERY failed (continuing)");
    int ir = libusb_init(&ctx_);
    if (ir != 0) { LE("libusb_init failed: %s", libusb_error_name(ir)); return false; }
    int r = libusb_wrap_sys_device(ctx_, (intptr_t)fd, &h_);
    if (r != 0 || !h_) { LE("wrap_sys_device: %s", libusb_error_name(r)); close(); return false; }
    LI("open ok");
    return true;
}

void Uac::close() {
    stop();
    if (h_)   { libusb_close(h_); h_ = nullptr; }
    if (ctx_) { libusb_exit(ctx_); ctx_ = nullptr; }
}

// One-shot, verbose. Everything the next hands-on session needs to see in the
// FIRST logcat: link speed (SuperSpeed vs High-Speed — the wrapped-fd usbfs
// isochronous path behaves differently), every interface/altsetting/endpoint,
// every class-specific descriptor as raw hex, and which interfaces the kernel
// audio driver (snd-usb-audio) is currently bound to.
void Uac::dump() {
    libusb_device* dev = libusb_get_device(h_);
    int speed = libusb_get_device_speed(dev);
    const char* sname =
        speed == LIBUSB_SPEED_SUPER_PLUS ? "SUPER+ (10Gb)" :
        speed == LIBUSB_SPEED_SUPER      ? "SUPER (5Gb)" :
        speed == LIBUSB_SPEED_HIGH       ? "HIGH (480Mb)" :
        speed == LIBUSB_SPEED_FULL       ? "FULL (12Mb)" : "unknown";
    LI("=== UAC descriptor dump ===  link speed: %s (%d)", sname, speed);

    libusb_config_descriptor* cfg = nullptr;
    if (libusb_get_active_config_descriptor(dev, &cfg) != 0 || !cfg) { LE("dump: no config desc"); return; }
    LI("config: %d interfaces, bmAttributes=0x%02x maxPower=%dmA",
       cfg->bNumInterfaces, cfg->bmAttributes, cfg->MaxPower * 2);

    for (int i = 0; i < cfg->bNumInterfaces; i++) {
        const libusb_interface& itf = cfg->interface[i];
        for (int a = 0; a < itf.num_altsetting; a++) {
            const libusb_interface_descriptor& id = itf.altsetting[a];
            int kd = libusb_kernel_driver_active(h_, id.bInterfaceNumber);
            LI("IF %d alt %d  class=0x%02x sub=0x%02x proto=0x%02x  %d endpoints  kernel_driver_active=%d",
               id.bInterfaceNumber, id.bAlternateSetting, id.bInterfaceClass,
               id.bInterfaceSubClass, id.bInterfaceProtocol, id.bNumEndpoints, kd);

            // class-specific interface descriptors (raw)
            const uint8_t* p = id.extra; int left = id.extra_length;
            while (left >= 2) {
                int len = p[0]; if (len < 2 || len > left) break;
                char hex[3 * 64 + 1]; int o = 0;
                for (int k = 0; k < len && k < 64; k++) o += snprintf(hex + o, sizeof(hex) - o, "%02x ", p[k]);
                LI("  CS-iface desc: %s", hex);
                p += len; left -= len;
            }

            for (int e = 0; e < id.bNumEndpoints; e++) {
                const libusb_endpoint_descriptor& ed = id.endpoint[e];
                int xfer = ed.bmAttributes & 0x03;
                int sync = (ed.bmAttributes >> 2) & 0x03;   // 0 none 1 async 2 adaptive 3 sync
                int usg  = (ed.bmAttributes >> 4) & 0x03;   // 0 data 1 feedback 2 implicit-fb
                int mps  = ed.wMaxPacketSize & 0x07FF;
                int mult = ((ed.wMaxPacketSize >> 11) & 0x03) + 1;   // high-bandwidth
                LI("  EP 0x%02x  xfer=%d sync=%d usage=%d  mps=%d x%d  bInterval=%d",
                   ed.bEndpointAddress, xfer, sync, usg, mps, mult, ed.bInterval);
                const uint8_t* q = ed.extra; int qleft = ed.extra_length;
                while (qleft >= 2) {
                    int len = q[0]; if (len < 2 || len > qleft) break;
                    char hex[3 * 32 + 1]; int o = 0;
                    for (int k = 0; k < len && k < 32; k++) o += snprintf(hex + o, sizeof(hex) - o, "%02x ", q[k]);
                    LI("    CS-EP desc: %s", hex);
                    q += len; qleft -= len;
                }
            }
        }
    }
    libusb_free_config_descriptor(cfg);
    LI("=== end dump ===");
}

// find the first AudioStreaming interface that has a non-zero-bandwidth
// altsetting with an isochronous IN endpoint, and a FORMAT_TYPE_I PCM descriptor.
bool Uac::scan() {
    static bool dumped = false;
    if (!dumped) { dumped = true; dump(); }
    libusb_device* dev = libusb_get_device(h_);
    libusb_config_descriptor* cfg = nullptr;
    if (libusb_get_active_config_descriptor(dev, &cfg) != 0 || !cfg) { LE("no config desc"); return false; }

    // AudioControl: find the Feature Unit id (for an explicit UNMUTE before capture —
    // some cheap chips gate the isochronous payload entirely while muted).
    for (int i = 0; i < cfg->bNumInterfaces; i++) {
        const libusb_interface_descriptor& id = cfg->interface[i].altsetting[0];
        if (id.bInterfaceClass != CLASS_AUDIO || id.bInterfaceSubClass != SUB_AUDIOCONTROL) continue;
        ac_ifnum_ = id.bInterfaceNumber;
        const uint8_t* p = id.extra; int left = id.extra_length;
        while (left >= 3) {
            uint8_t len = p[0], type = p[1], sub = p[2];
            if (len < 3 || len > left) break;
            if (type == CS_INTERFACE && sub == AC_FEATURE_UNIT && len >= 5) fu_id_ = p[3];
            p += len; left -= len;
        }
        LI("AC iface %d, feature unit id %d", ac_ifnum_, fu_id_);
    }

    bool found = false;
    for (int i = 0; i < cfg->bNumInterfaces && !found; i++) {
        const libusb_interface& itf = cfg->interface[i];
        for (int a = 0; a < itf.num_altsetting && !found; a++) {
            const libusb_interface_descriptor& id = itf.altsetting[a];
            if (id.bInterfaceClass != CLASS_AUDIO || id.bInterfaceSubClass != SUB_AUDIOSTREAMING) continue;
            if (id.bNumEndpoints == 0) continue;   // the zero-bandwidth alt

            uint8_t ep = 0; uint16_t mps = 0;
            for (int e = 0; e < id.bNumEndpoints; e++) {
                const auto& ed = id.endpoint[e];
                if ((ed.bEndpointAddress & 0x80) && (ed.bmAttributes & 0x03) == LIBUSB_TRANSFER_TYPE_ISOCHRONOUS) {
                    ep = ed.bEndpointAddress; mps = ed.wMaxPacketSize & 0x07FF; break;
                }
            }
            if (!ep) continue;

            int ch = ch_, bits = bits_, rate = rate_;
            const uint8_t* p = id.extra; int left = id.extra_length;
            while (left >= 3) {
                uint8_t len = p[0], type = p[1], sub = p[2];
                if (len < 3 || len > left) break;
                if (type == CS_INTERFACE && sub == AS_FORMAT_TYPE && len >= 8) {
                    ch = p[4]; bits = p[6];
                    rate = p[8] | (p[9] << 8) | (p[10] << 16);   // tSamFreq[0], 24-bit LE
                }
                p += len; left -= len;
            }

            ifnum_ = id.bInterfaceNumber; alt_ = id.bAlternateSetting;
            ep_in_ = ep; pktsize_ = mps; ch_ = ch; bits_ = bits; rate_ = rate ? rate : rate_;
            LI("AS iface %d alt %d  ep=0x%02x pktsize=%u  %dch %dbit %dHz",
               ifnum_, alt_, ep_in_, pktsize_, ch_, bits_, rate_);
            found = true;
        }
    }
    libusb_free_config_descriptor(cfg);
    if (!found) LE("no isochronous AudioStreaming interface found");
    return found;
}

bool Uac::start() {
    if (!h_) return false;
    if (running_) return true;
    if (!scan()) return false;

    libusb_set_auto_detach_kernel_driver(h_, 1);
    LI("before claim: kernel_driver_active(IF %d)=%d  kernel_driver_active(AC IF %d)=%d",
       ifnum_, libusb_kernel_driver_active(h_, ifnum_),
       ac_ifnum_, ac_ifnum_ >= 0 ? libusb_kernel_driver_active(h_, ac_ifnum_) : -1);
    // snd-usb-audio binds the AC + AS interfaces as one function; detaching only
    // the AS interface can leave the driver holding the streaming state. Detach
    // the AC interface too, up front.
    if (ac_ifnum_ >= 0 && libusb_kernel_driver_active(h_, ac_ifnum_) == 1) {
        int dr = libusb_detach_kernel_driver(h_, ac_ifnum_);
        LI("detach kernel driver AC IF %d -> %s", ac_ifnum_, dr == 0 ? "ok" : libusb_error_name(dr));
    }
    int r = libusb_claim_interface(h_, ifnum_);
    if (r != 0) { LE("claim IF %d: %s", ifnum_, libusb_error_name(r)); return false; }
    r = libusb_set_interface_alt_setting(h_, ifnum_, alt_);
    if (r != 0) { LE("set alt %d: %s", alt_, libusb_error_name(r)); libusb_release_interface(h_, ifnum_); return false; }
    // Confirm the device actually switched. GET_INTERFACE (standard request,
    // recipient=interface): bmRequestType 0x81, bRequest 0x0A, wIndex=ifnum.
    {
        uint8_t curAlt = 0xFF;
        int gr = libusb_control_transfer(h_, 0x81, 0x0A, 0, ifnum_, &curAlt, 1, 1000);
        LI("GET_INTERFACE(IF %d) -> %d, device reports current alt = %d (wanted %d)",
           ifnum_, gr, gr == 1 ? curAlt : -1, alt_);
    }

    // UAC1 endpoints with a Class-Specific AS Isochronous Data Endpoint
    // descriptor (bmAttributes bit0 = sampling-freq-control present) need an
    // explicit SET_CUR sample-rate lock before the device actually starts
    // streaming. This device's descriptor says it isn't present, but the
    // request is harmless, so still try it.
    {
        uint8_t freq[3] = { (uint8_t)(rate_ & 0xFF), (uint8_t)((rate_ >> 8) & 0xFF), (uint8_t)((rate_ >> 16) & 0xFF) };
        int fr = libusb_control_transfer(h_, 0x22 /*class, EP, host->dev*/, UAC_SET_CUR,
                                         (0x01 << UAC_FU_CONTROL_SEL_SHIFT) /*SAMPLING_FREQ_CONTROL, ch0*/,
                                         ep_in_, freq, 3, 1000);
        LI("SET_CUR sampling freq %dHz on ep 0x%02x -> %d", rate_, ep_in_, fr);
    }
    // Explicit UNMUTE on the Feature Unit — some cheap chips gate the whole
    // isochronous payload (zero-length packets, not zero-value samples) while
    // muted, and PAL's own init sequence likely does this implicitly. Some
    // usbfs paths reject an interface-recipient class request unless that
    // interface is claimed first (AC has no endpoints, so this is harmless).
    if (fu_id_ >= 0 && ac_ifnum_ >= 0) {
        int cr = libusb_claim_interface(h_, ac_ifnum_);
        if (cr != 0) LW("claim AC IF %d: %s", ac_ifnum_, libusb_error_name(cr));
        uint8_t mute0 = 0;
        int mr = libusb_control_transfer(h_, 0x21 /*class, interface, host->dev*/, UAC_SET_CUR,
                                         (UAC_MUTE_CONTROL << UAC_FU_CONTROL_SEL_SHIFT) /*ch0*/,
                                         (uint16_t)((fu_id_ << 8) | ac_ifnum_), &mute0, 1, 1000);
        LI("SET_CUR unmute FU %d (IF %d, claim=%d) -> %d", fu_id_, ac_ifnum_, cr, mr);
        if (cr == 0) libusb_release_interface(h_, ac_ifnum_);
    }

    pcm_.assign((size_t)rate_ * ch_ * (bits_ / 8) / 2, 0);   // 0.5s ring
    r_ = w_ = count_ = 0;

    running_ = true;
    ring_.assign(kNumXfers, nullptr);
    ring_buf_.assign(kNumXfers, {});
    int bufLen = pktsize_ * kPacketsPerXfer;
    for (int i = 0; i < kNumXfers; i++) {
        ring_buf_[i].resize(bufLen);
        libusb_transfer* x = libusb_alloc_transfer(kPacketsPerXfer);
        libusb_fill_iso_transfer(x, h_, ep_in_, ring_buf_[i].data(), bufLen,
                                 kPacketsPerXfer, &Uac::on_xfer, this, 1000);
        libusb_set_iso_packet_lengths(x, pktsize_);
        ring_[i] = x;
        int sr = libusb_submit_transfer(x);
        if (sr != 0) LE("iso submit[%d]: %s", i, libusb_error_name(sr));
    }
    LI("capturing: %d xfers x %d packets x %u bytes", kNumXfers, kPacketsPerXfer, pktsize_);
    evt_ = std::thread(&Uac::event_loop, this);
    return true;
}

void Uac::stop() {
    if (!running_.exchange(false)) return;
    for (auto* x : ring_) if (x) libusb_cancel_transfer(x);
    if (evt_.joinable()) evt_.join();
    for (auto* x : ring_) if (x) libusb_free_transfer(x);
    ring_.clear(); ring_buf_.clear();
    if (h_ && ifnum_ >= 0) libusb_release_interface(h_, ifnum_);
    LI("stopped");
}

void Uac::event_loop() {
    timeval tv{0, 50000};
    while (running_) libusb_handle_events_timeout_completed(ctx_, &tv, nullptr);
    for (int i = 0; i < 20; i++) libusb_handle_events_timeout_completed(ctx_, &tv, nullptr);
}

void LIBUSB_CALL Uac::on_xfer(libusb_transfer* x) {
    static_cast<Uac*>(x->user_data)->handle_xfer(x);
}

void Uac::handle_xfer(libusb_transfer* x) {
    if (!running_) return;
    static std::atomic<uint64_t> xfers{0}, pkt_ok{0}, pkt_zero{0}, pkt_err{0}, bytes{0};
    static int64_t last_log = 0;

    if (x->status == LIBUSB_TRANSFER_COMPLETED) {
        int okThis = 0, zeroThis = 0, errThis = 0; int firstStatus = -1, firstLen = -1;
        for (int i = 0; i < x->num_iso_packets; i++) {
            const auto& d = x->iso_packet_desc[i];
            if (i == 0) { firstStatus = d.status; firstLen = (int)d.actual_length; }
            if (d.status != LIBUSB_TRANSFER_COMPLETED) { errThis++; continue; }
            if (d.actual_length > 0) {
                push(libusb_get_iso_packet_buffer_simple(x, i), d.actual_length);
                okThis++; bytes += d.actual_length;
            } else zeroThis++;
        }
        xfers++; pkt_ok += okThis; pkt_zero += zeroThis; pkt_err += errThis;

        timespec t; clock_gettime(CLOCK_MONOTONIC, &t);
        int64_t now = (int64_t)t.tv_sec * 1000000000LL + t.tv_nsec;
        if (now - last_log > 1000000000LL) {
            last_log = now;
            LI("iso stats: xfers=%llu okPkt=%llu zeroPkt=%llu errPkt=%llu bytes=%llu  (last xfer: pkt0 status=%d len=%d)",
               (unsigned long long)xfers, (unsigned long long)pkt_ok, (unsigned long long)pkt_zero,
               (unsigned long long)pkt_err, (unsigned long long)bytes, firstStatus, firstLen);
        }
    } else if (x->status == LIBUSB_TRANSFER_CANCELLED) {
        return;
    } else {
        LW("iso xfer status=%d", x->status);
    }
    if (running_) {
        int r = libusb_submit_transfer(x);
        if (r != 0) LW("iso resubmit: %s", libusb_error_name(r));
    }
}

void Uac::push(const uint8_t* data, size_t n) {
    std::lock_guard<std::mutex> lk(mu_);
    if (pcm_.empty()) return;
    size_t cap = pcm_.size();
    if (n > cap) { data += (n - cap); n = cap; }   // shouldn't happen, guard anyway
    for (size_t i = 0; i < n; i++) {
        pcm_[w_] = data[i];
        w_ = (w_ + 1) % cap;
        if (count_ < cap) count_++;
        else r_ = (r_ + 1) % cap;   // ring full: drop oldest (keeps latency bounded)
    }
}

size_t Uac::read(uint8_t* dst, size_t cap) {
    std::lock_guard<std::mutex> lk(mu_);
    size_t n = count_ < cap ? count_ : cap;
    size_t sz = pcm_.size();
    for (size_t i = 0; i < n && sz; i++) { dst[i] = pcm_[r_]; r_ = (r_ + 1) % sz; }
    count_ -= n;
    return n;
}

} // namespace nuvc
