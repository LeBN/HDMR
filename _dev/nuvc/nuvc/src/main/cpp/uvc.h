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

    // P1: negotiate & bulk-stream the first available 1280x720 mode
    // (uncompressed preferred, else MJPEG); returns false with a reason in log.
    bool start_preferred(FrameCb cb);
    void stop();

    // live stats (event thread writes, any thread reads)
    uint64_t frames()      const { return frames_; }
    uint64_t bad_frames()  const { return bad_; }
    uint32_t last_bytes()  const { return last_bytes_; }
    float    fps()         const { return fps_; }
    const Mode& mode()     const { return mode_; }
    int      link_speed()  const { return speed_; }   // libusb_speed enum

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
    std::vector<std::vector<uint8_t>> ring_buf_;
    std::atomic<bool>      running_{false};
    std::thread            evt_;

    // frame assembly
    std::vector<uint8_t>   frame_;
    int                    fid_ = -1;
    FrameCb                cb_;
    Mode                   mode_;

    std::atomic<uint64_t>  frames_{0}, bad_{0};
    std::atomic<uint32_t>  last_bytes_{0};
    std::atomic<float>     fps_{0};
    int64_t                fps_t0_ = 0;
    uint64_t               fps_n0_ = 0;
};

} // namespace nuvc
