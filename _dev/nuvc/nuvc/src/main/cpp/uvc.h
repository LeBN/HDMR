#pragma once
#include <cstdint>
#include <cstddef>
#include <functional>
#include <atomic>
#include <thread>
#include <vector>
#include <mutex>
#include <libusb.h>

namespace nuvc {

// one assembled video frame, payload only (UVC headers stripped)
using FrameCb = std::function<void(const uint8_t* data, size_t len,
                                   uint16_t w, uint16_t h, bool mjpeg)>;

struct Mode {
    uint8_t  fmt_index = 0, frame_index = 0;
    uint16_t w = 0, h = 0;
    uint32_t interval = 0;   // 100ns units
    bool     mjpeg = false;
    uint32_t max_frame_size = 0;
};

class Uvc {
public:
    // fd from UsbDeviceConnection.getFileDescriptor() (Java side owns it)
    bool open(int fd);
    void close();

    // negotiate & bulk-stream the first available 1280x720 mode
    // (uncompressed preferred, else MJPEG); returns false with a reason in log.
    // cb may be null — the newest frame is always kept for latest_into().
    bool start_preferred(FrameCb cb = nullptr);
    void stop();

    // Copy the most recent assembled frame into dst (>= cap bytes).
    // Returns its sequence number (monotonic from 1), or 0 if there is no
    // frame or none newer than `since`. Fills w/h/fmt when it returns > 0.
    // fmt: 0 = NV12, 1 = YUY2, 2 = MJPEG.
    uint64_t latest_into(uint8_t* dst, size_t cap, uint64_t since,
                         uint16_t* w, uint16_t* h, uint8_t* fmt);

    // live stats (event thread writes, any thread reads)
    uint64_t frames()      const { return frames_; }
    uint64_t bad_frames()  const { return bad_; }
    uint32_t last_bytes()  const { return last_bytes_; }
    float    fps()         const { return fps_; }
    const Mode& mode()     const { return mode_; }
    int      link_speed()  const { return speed_; }   // libusb_speed enum
    uint8_t  fmt()         const { return fmt_; }      // 0 NV12, 1 YUY2, 2 MJPEG

private:
    bool negotiate(const Mode& m);
    void event_loop();
    static void LIBUSB_CALL on_xfer(libusb_transfer* x);
    void handle_xfer(libusb_transfer* x);

    libusb_context*        ctx_ = nullptr;
    libusb_device_handle*  h_   = nullptr;
    int                    vs_ifnum_ = -1;
    uint8_t                ep_in_   = 0;
    uint32_t               max_payload_ = 0;
    int                    speed_ = 0;

    std::vector<libusb_transfer*> ring_;
    std::vector<std::vector<uint8_t>> ring_buf_;   // fallback host buffers
    std::vector<uint8_t*>  ring_ptr_;              // actual buffer per transfer
    bool                   dev_mem_ = false;       // ring_ptr_ came from libusb_dev_mem_alloc
    size_t                 buf_sz_ = 0;
    std::atomic<bool>      running_{false};
    std::thread            evt_;

    // frame assembly
    std::vector<uint8_t>   frame_;
    int                    fid_ = -1;
    FrameCb                cb_;
    Mode                   mode_;

    // newest complete frame, for the polling consumer (Unity)
    std::mutex             latest_mu_;
    std::vector<uint8_t>   latest_;
    uint64_t               latest_seq_ = 0;
    uint8_t                fmt_ = 0;   // 0 NV12, 1 YUY2, 2 MJPEG

    std::atomic<uint64_t>  frames_{0}, bad_{0};
    std::atomic<uint32_t>  last_bytes_{0};
    std::atomic<float>     fps_{0};
    int64_t                fps_t0_ = 0;
    uint64_t               fps_n0_ = 0;
};

} // namespace nuvc
