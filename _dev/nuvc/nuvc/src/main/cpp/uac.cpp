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
static const uint8_t SUB_AUDIOSTREAMING   = 0x02;
static const uint8_t CS_INTERFACE         = 0x24;
static const uint8_t AS_FORMAT_TYPE       = 0x02;

bool Uac::open(int fd) {
    if (libusb_init(&ctx_) != 0) { LE("libusb_init failed"); return false; }
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

// find the first AudioStreaming interface that has a non-zero-bandwidth
// altsetting with an isochronous IN endpoint, and a FORMAT_TYPE_I PCM descriptor.
bool Uac::scan() {
    libusb_device* dev = libusb_get_device(h_);
    libusb_config_descriptor* cfg = nullptr;
    if (libusb_get_active_config_descriptor(dev, &cfg) != 0 || !cfg) { LE("no config desc"); return false; }

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
    int r = libusb_claim_interface(h_, ifnum_);
    if (r != 0) { LE("claim IF %d: %s", ifnum_, libusb_error_name(r)); return false; }
    r = libusb_set_interface_alt_setting(h_, ifnum_, alt_);
    if (r != 0) { LE("set alt %d: %s", alt_, libusb_error_name(r)); libusb_release_interface(h_, ifnum_); return false; }

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
    if (x->status == LIBUSB_TRANSFER_COMPLETED) {
        for (int i = 0; i < x->num_iso_packets; i++) {
            const auto& d = x->iso_packet_desc[i];
            if (d.status == LIBUSB_TRANSFER_COMPLETED && d.actual_length > 0)
                push(libusb_get_iso_packet_buffer_simple(x, i), d.actual_length);
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
