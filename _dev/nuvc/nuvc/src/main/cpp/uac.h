#pragma once
#include <cstdint>
#include <cstddef>
#include <atomic>
#include <thread>
#include <vector>
#include <mutex>
#include <libusb.h>

namespace nuvc {

// From-scratch USB Audio Class 1.0 isochronous capture. Bypasses the Android
// audio HAL entirely (it cannot sustain a read from this card's isochronous
// endpoint — confirmed: fails identically with zero video traffic on the bus,
// so it's not USB bandwidth contention, it's a HAL/device incompatibility).
// Own libusb_context, own fd/connection, own thread — never touches the
// video Uvc object's context (that coupling is what SIGSEGV'd the old plugin).
class Uac {
public:
    bool open(int fd);
    void close();

    // Finds the first AudioStreaming interface with an isochronous IN
    // endpoint, activates its alternate setting, and starts capturing.
    bool start();
    void stop();

    // Pop up to `cap` bytes of interleaved PCM into dst. Returns bytes copied
    // (0 if the ring is empty right now — caller should back off briefly).
    size_t read(uint8_t* dst, size_t cap);

    int sampleRate() const { return rate_; }
    int channels()   const { return ch_; }
    int bitDepth()   const { return bits_; }
    bool running()   const { return running_; }

private:
    bool scan();   // find AS interface/altsetting/endpoint/format
    void event_loop();
    static void LIBUSB_CALL on_xfer(libusb_transfer* x);
    void handle_xfer(libusb_transfer* x);
    void push(const uint8_t* data, size_t n);

    libusb_context*        ctx_ = nullptr;
    libusb_device_handle*  h_   = nullptr;
    int      ifnum_ = -1, alt_ = -1;
    uint8_t  ep_in_ = 0;
    uint16_t pktsize_ = 0;
    int      rate_ = 48000, ch_ = 2, bits_ = 16;

    static const int kPacketsPerXfer = 32;   // ~32ms/transfer at 1ms interval
    static const int kNumXfers = 8;          // ~256ms of ring across in-flight transfers
    std::vector<libusb_transfer*> ring_;
    std::vector<std::vector<uint8_t>> ring_buf_;
    std::atomic<bool> running_{false};
    std::thread evt_;

    // byte ring buffer for assembled PCM
    std::mutex mu_;
    std::vector<uint8_t> pcm_;
    size_t r_ = 0, w_ = 0, count_ = 0;
};

} // namespace nuvc
