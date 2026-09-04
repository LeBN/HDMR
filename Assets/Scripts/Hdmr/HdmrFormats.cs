using System;

namespace Hdmr
{
    /// <summary>Maps UVC4UnityAndroid / aandusb raw_frame_t values to friendly names.</summary>
    public static class HdmrFormats
    {
        // from aandusb_native.h : enum raw_frame
        public const uint UNKNOWN = 0x00000000;
        public const uint YUY2    = 0x00010005; // RAW_FRAME_UNCOMPRESSED_YUYV
        public const uint NV21    = 0x00050005;
        public const uint NV12    = 0x000b0005;
        public const uint RGB565  = 0x000d0005;
        public const uint RGBX    = 0x00100005;
        public const uint MJPEG   = 0x00000007;
        public const uint H264    = 0x00000014;
        public const uint H264F   = 0x00030011; // FRAME_TYPE_H264_FRAME

        public static string Name(uint ft)
        {
            switch (ft)
            {
                case MJPEG:  return "MJPEG";
                case H264:   return "H264";
                case H264F:  return "H264-frame";
                case YUY2:   return "YUY2";
                case NV21:   return "NV21";
                case NV12:   return "NV12";
                case RGB565: return "RGB565";
                case RGBX:   return "RGBX";
                case UNKNOWN: return "raw/unknown";
                default:     return $"0x{ft:X}";
            }
        }

        public static bool IsCompressed(uint ft) => ft == MJPEG || ft == H264 || ft == H264F;
    }
}
