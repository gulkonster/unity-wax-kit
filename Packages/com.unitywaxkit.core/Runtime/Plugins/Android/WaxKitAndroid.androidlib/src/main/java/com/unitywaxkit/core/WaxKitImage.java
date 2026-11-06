package com.unitywaxkit.core;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import java.nio.ByteBuffer;

/**
 * NFT media on Android (WaxMediaPlatformDecoder.cs): decodes an image (WebP) with BitmapFactory, the first frame of an
 * animation. Result: big-endian int32 width, height, frame count (1), loop count (0), delay (100 ms), then RGBA rows top to
 * bottom, not premultiplied. Null when the image cannot be decoded or is larger than the limits.
 */
public final class WaxKitImage {
    private WaxKitImage() {
    }

    public static byte[] decode(byte[] data, int maxWidth, int maxHeight) {
        if (data == null || data.length == 0) return null;
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(data, 0, data.length, bounds);
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0 || bounds.outWidth > maxWidth || bounds.outHeight > maxHeight) return null;

        BitmapFactory.Options options = new BitmapFactory.Options();
        options.inPreferredConfig = Bitmap.Config.ARGB_8888;
        options.inPremultiplied = false;
        options.inScaled = false;
        Bitmap bitmap = BitmapFactory.decodeByteArray(data, 0, data.length, options);
        if (bitmap == null) return null;
        try {
            if (bitmap.getConfig() != Bitmap.Config.ARGB_8888) {
                Bitmap copy = bitmap.copy(Bitmap.Config.ARGB_8888, false);
                bitmap.recycle();
                bitmap = copy;
                if (bitmap == null) return null;
            }
            int width = bitmap.getWidth(), height = bitmap.getHeight();
            ByteBuffer out = ByteBuffer.allocate(20 + width * height * 4);
            out.putInt(width).putInt(height).putInt(1).putInt(0).putInt(100);
            // ARGB_8888 is stored as R, G, B, A bytes; an unpremultiplied bitmap is copied as it is.
            bitmap.copyPixelsToBuffer(out);
            return out.array();
        } finally {
            bitmap.recycle();
        }
    }
}
