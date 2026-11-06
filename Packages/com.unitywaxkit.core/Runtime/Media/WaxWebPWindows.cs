#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityWaxKit
{
    /// <summary>
    /// WebP on Windows x64 through the shipped libwebp 1.6.0 (Plugins/Windows/libwebp, see VERSION.md): WebPDecodeRGBA for still
    /// images, WebPAnimDecoder for animations. The kit's limits are checked before libwebp is called: the header size (by the
    /// loader), then the canvas, frame count and decoded bytes reported by the demuxer.
    /// </summary>
    internal static class WaxWebPWindows
    {
        const string Lib = "libwebp";
        const string Demux = "libwebpdemux";
        const int DemuxAbiVersion = 0x0107; // WEBP_DEMUX_ABI_VERSION of libwebp 1.6.0
        const int ModeRgba = 1;             // MODE_RGBA: not premultiplied

        [StructLayout(LayoutKind.Sequential)]
        struct WebPData
        {
            public IntPtr Bytes;
            public UIntPtr Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WebPAnimDecoderOptions
        {
            public int ColorMode;
            public int UseThreads;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public uint[] Padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WebPAnimInfo
        {
            public uint CanvasWidth;
            public uint CanvasHeight;
            public uint LoopCount;
            public uint BgColor;
            public uint FrameCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] Pad;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int WebPGetDecoderVersion();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int WebPGetInfo(byte[] data, UIntPtr size, out int width, out int height);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr WebPDecodeRGBA(byte[] data, UIntPtr size, out int width, out int height);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void WebPFree(IntPtr pointer);
        [DllImport(Demux, CallingConvention = CallingConvention.Cdecl)] static extern int WebPAnimDecoderOptionsInitInternal(ref WebPAnimDecoderOptions options, int abiVersion);
        [DllImport(Demux, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr WebPAnimDecoderNewInternal(ref WebPData data, ref WebPAnimDecoderOptions options, int abiVersion);
        [DllImport(Demux, CallingConvention = CallingConvention.Cdecl)] static extern int WebPAnimDecoderGetInfo(IntPtr decoder, ref WebPAnimInfo info);
        [DllImport(Demux, CallingConvention = CallingConvention.Cdecl)] static extern int WebPAnimDecoderHasMoreFrames(IntPtr decoder);
        [DllImport(Demux, CallingConvention = CallingConvention.Cdecl)] static extern int WebPAnimDecoderGetNext(IntPtr decoder, out IntPtr buffer, out int timestamp);
        [DllImport(Demux, CallingConvention = CallingConvention.Cdecl)] static extern void WebPAnimDecoderDelete(IntPtr decoder);

        /// <summary>The libwebp version as "1.6.0" (for the logs and tests).</summary>
        internal static string Version
        {
            get
            {
                var v = WebPGetDecoderVersion();
                return $"{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
            }
        }

        public static Task<WaxDecodedImage> Decode(byte[] data, WaxMediaType type, WaxMediaLimits limits, bool firstFrameOnly, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            // Decoding runs off the main thread; libwebp keeps no global state between calls.
            return Task.Run(() => WaxMediaFormat.IsAnimatedWebP(data) ? DecodeAnimation(data, limits, firstFrameOnly, cancellation) : DecodeStill(data, limits), cancellation);
        }

        static WaxDecodedImage DecodeStill(byte[] data, WaxMediaLimits limits)
        {
            if (WebPGetInfo(data, (UIntPtr)data.Length, out var width, out var height) == 0) throw Broken();
            limits.CheckSize(width, height);
            var pixels = WebPDecodeRGBA(data, (UIntPtr)data.Length, out width, out height);
            if (pixels == IntPtr.Zero) throw Broken();
            try
            {
                var frame = new byte[width * height * 4];
                Marshal.Copy(pixels, frame, 0, frame.Length);
                var image = new WaxDecodedImage { Width = width, Height = height, LoopCount = 0 };
                image.Frames.Add(frame);
                image.DelaysMs.Add(100);
                return image;
            }
            finally
            {
                WebPFree(pixels);
            }
        }

        static WaxDecodedImage DecodeAnimation(byte[] data, WaxMediaLimits limits, bool firstFrameOnly, CancellationToken cancellation)
        {
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            var decoder = IntPtr.Zero;
            try
            {
                var options = new WebPAnimDecoderOptions { Padding = new uint[7] };
                if (WebPAnimDecoderOptionsInitInternal(ref options, DemuxAbiVersion) == 0) throw Broken();
                options.ColorMode = ModeRgba;
                options.UseThreads = 0;
                var webp = new WebPData { Bytes = pinned.AddrOfPinnedObject(), Size = (UIntPtr)data.Length };
                decoder = WebPAnimDecoderNewInternal(ref webp, ref options, DemuxAbiVersion);
                if (decoder == IntPtr.Zero) throw Broken();

                var info = new WebPAnimInfo { Pad = new uint[4] };
                if (WebPAnimDecoderGetInfo(decoder, ref info) == 0) throw Broken();
                if (info.CanvasWidth > int.MaxValue || info.CanvasHeight > int.MaxValue) throw Broken();
                int width = (int)info.CanvasWidth, height = (int)info.CanvasHeight;
                limits.CheckSize(width, height);
                var frames = firstFrameOnly ? 1L : info.FrameCount;
                if (frames > limits.MaxFrames)
                    throw new WaxKitException(WaxErrorKind.NotSupported, $"The WebP has more than {limits.MaxFrames} frames (MaxFrames).");
                if (frames * width * height * 4 > limits.MaxDecodedBytes)
                    throw new WaxKitException(WaxErrorKind.NotSupported, $"The WebP needs more than {WaxMediaLimits.Size(limits.MaxDecodedBytes)} decoded (MaxDecodedBytes).");

                // WebP loop_count: 0 = forever, n = played n times; the kit uses GIF's meaning (n = repeated n times, -1 = once).
                var image = new WaxDecodedImage { Width = width, Height = height, LoopCount = info.LoopCount == 0 ? 0 : info.LoopCount == 1 ? -1 : (int)info.LoopCount - 1 };
                var previous = 0;
                while (WebPAnimDecoderHasMoreFrames(decoder) != 0 && image.Frames.Count < frames)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (WebPAnimDecoderGetNext(decoder, out var buffer, out var timestamp) == 0)
                    {
                        // A cut-off file keeps the frames that finished.
                        if (image.Frames.Count == 0) throw Broken();
                        image.Truncated = true;
                        break;
                    }
                    var frame = new byte[width * height * 4];
                    Marshal.Copy(buffer, frame, 0, frame.Length); // the canvas, owned by the decoder
                    image.Frames.Add(frame);
                    image.DelaysMs.Add(Math.Max(20, timestamp - previous));
                    previous = timestamp;
                }
                if (image.Frames.Count == 0) throw Broken();
                return image;
            }
            finally
            {
                if (decoder != IntPtr.Zero) WebPAnimDecoderDelete(decoder);
                pinned.Free();
            }
        }

        static WaxKitException Broken() => new WaxKitException(WaxErrorKind.NotSupported, "The WebP image is broken or cannot be decoded.");
    }
}
#endif
