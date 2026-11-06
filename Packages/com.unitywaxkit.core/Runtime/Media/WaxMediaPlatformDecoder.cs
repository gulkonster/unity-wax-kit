using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityWaxKit
{
    /// <summary>
    /// Image decoders of the platform, used for WebP. WebGL: the browser (static; every frame with WebCodecs ImageDecoder
    /// where the browser has it). Android: BitmapFactory (the first frame). Windows x64 (player and Editor): the shipped libwebp.
    /// </summary>
    internal static class WaxMediaPlatformDecoder
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        public static WaxPlatformDecode Decoder => WebGL.Decode;
#elif UNITY_ANDROID && !UNITY_EDITOR
        public static WaxPlatformDecode Decoder => Android.Decode;
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        public static WaxPlatformDecode Decoder => Environment.Is64BitProcess ? WaxWebPWindows.Decode : (WaxPlatformDecode)null;
#else
        public static WaxPlatformDecode Decoder => null;
#endif

        /// <summary>
        /// Reads the decoder result layout shared by the jslib and the Java plugin: big-endian int32 width, height, frame
        /// count, loop count, then one int32 delay (ms) per frame, then the RGBA frames (rows top to bottom).
        /// </summary>
        internal static WaxDecodedImage ReadResult(byte[] result, WaxMediaLimits limits)
        {
            if (result == null || result.Length < 16) throw new WaxKitException(WaxErrorKind.NotSupported, "The image could not be decoded.");
            int Int(int offset) => result[offset] << 24 | result[offset + 1] << 16 | result[offset + 2] << 8 | result[offset + 3];
            int width = Int(0), height = Int(4), count = Int(8), loop = Int(12);
            limits.CheckSize(width, height);
            var frameBytes = width * height * 4;
            if (count < 1 || count > limits.MaxFrames || result.Length != 16 + count * 4 + (long)count * frameBytes)
                throw new WaxKitException(WaxErrorKind.NotSupported, "The image could not be decoded (unexpected result).");
            var image = new WaxDecodedImage { Width = width, Height = height, LoopCount = loop };
            for (var i = 0; i < count; i++)
            {
                image.DelaysMs.Add(Math.Max(20, Int(16 + i * 4)));
                var frame = new byte[frameBytes];
                Buffer.BlockCopy(result, 16 + count * 4 + i * frameBytes, frame, 0, frameBytes);
                image.Frames.Add(frame);
            }
            return image;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        static class WebGL
        {
            [DllImport("__Internal")] static extern void WaxKitMedia_Init(Action<int, IntPtr, int, string> onDone);
            [DllImport("__Internal")] static extern void WaxKitMedia_Decode(int id, byte[] data, int length, string mime, int firstFrameOnly, int maxFrames, int maxMegabytes);
            [DllImport("__Internal")] static extern void WaxKitMedia_Free(IntPtr pointer);

            static readonly Dictionary<int, (TaskCompletionSource<byte[]> done, WaxMediaLimits limits)> Pending = new Dictionary<int, (TaskCompletionSource<byte[]>, WaxMediaLimits)>();
            static bool initialized;
            static int nextId = 1;

            public static async Task<WaxDecodedImage> Decode(byte[] data, WaxMediaType type, WaxMediaLimits limits, bool firstFrameOnly, CancellationToken cancellation)
            {
                if (!initialized)
                {
                    WaxKitMedia_Init(OnDone);
                    initialized = true;
                }
                var id = nextId++;
                var done = new TaskCompletionSource<byte[]>();
                Pending[id] = (done, limits);
                WaxKitMedia_Decode(id, data, data.Length, "image/webp", firstFrameOnly ? 1 : 0, limits.MaxFrames,
                    (int)Math.Min(int.MaxValue, Math.Max(1, limits.MaxDecodedBytes >> 20)));
                using (cancellation.Register(() => done.TrySetCanceled()))
                {
                    var result = await done.Task;
                    return ReadResult(result, limits);
                }
            }

            [AOT.MonoPInvokeCallback(typeof(Action<int, IntPtr, int, string>))]
            static void OnDone(int id, IntPtr pointer, int length, string error)
            {
                byte[] bytes = null;
                if (pointer != IntPtr.Zero)
                {
                    bytes = new byte[length];
                    Marshal.Copy(pointer, bytes, 0, length);
                    WaxKitMedia_Free(pointer);
                }
                if (!Pending.TryGetValue(id, out var entry)) return;
                Pending.Remove(id);
                if (bytes != null) entry.done.TrySetResult(bytes);
                else entry.done.TrySetException(new WaxKitException(WaxErrorKind.NotSupported, "The browser could not decode the image: " + error));
            }
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        static class Android
        {
            public static Task<WaxDecodedImage> Decode(byte[] data, WaxMediaType type, WaxMediaLimits limits, bool firstFrameOnly, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var input = new sbyte[data.Length];
                Buffer.BlockCopy(data, 0, input, 0, data.Length);
                sbyte[] output;
                using (var plugin = new AndroidJavaClass("com.unitywaxkit.core.WaxKitImage"))
                    output = plugin.CallStatic<sbyte[]>("decode", input, limits.MaxWidth, limits.MaxHeight);
                if (output == null) throw new WaxKitException(WaxErrorKind.NotSupported, "Android could not decode the image.");
                var bytes = new byte[output.Length];
                Buffer.BlockCopy(output, 0, bytes, 0, output.Length);
                return Task.FromResult(ReadResult(bytes, limits));
            }
        }
#endif
    }
}
