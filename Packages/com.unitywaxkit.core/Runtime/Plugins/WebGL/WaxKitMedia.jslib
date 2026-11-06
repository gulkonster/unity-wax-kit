// NFT media on WebGL (WaxMediaPlatformDecoder.cs): the browser decodes WebP. All frames with WebCodecs ImageDecoder where the
// browser has it and the call wants them; otherwise the first frame through createImageBitmap. The result goes back to C# as
// one heap buffer: big-endian int32 width, height, frame count, loop count, one int32 delay (ms) per frame, then RGBA frames.
var WaxKitMediaPlugin = {
  $WaxKitMedia: {
    onDone: 0,

    done: function (id, bytes, error) {
      var ptr = 0, length = 0;
      if (bytes) {
        length = bytes.length;
        ptr = _malloc(length);
        HEAPU8.set(bytes, ptr);
      }
      var text = error ? String(error) : '';
      var size = lengthBytesUTF8(text) + 1;
      var textPtr = _malloc(size);
      stringToUTF8(text, textPtr, size);
      try {
        {{{ makeDynCall('viiii', 'WaxKitMedia.onDone') }}}(id, ptr, length, textPtr);
      } finally {
        _free(textPtr);
      }
    },

    pack: function (width, height, loop, delays, frames) {
      var frameBytes = width * height * 4;
      var out = new Uint8Array(16 + delays.length * 4 + frames.length * frameBytes);
      var view = new DataView(out.buffer);
      view.setInt32(0, width);
      view.setInt32(4, height);
      view.setInt32(8, frames.length);
      view.setInt32(12, loop);
      for (var i = 0; i < delays.length; i++) view.setInt32(16 + i * 4, delays[i]);
      var offset = 16 + delays.length * 4;
      for (var f = 0; f < frames.length; f++) {
        out.set(frames[f], offset);
        offset += frameBytes;
      }
      return out;
    },

    canvas: function (width, height) {
      var c = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(width, height) : document.createElement('canvas');
      c.width = width;
      c.height = height;
      return c;
    },

    pixels: function (ctx, source, width, height) {
      ctx.clearRect(0, 0, width, height);
      ctx.drawImage(source, 0, 0, width, height);
      return new Uint8Array(ctx.getImageData(0, 0, width, height).data.buffer);
    },
  },

  WaxKitMedia_Init: function (onDone) {
    WaxKitMedia.onDone = onDone;
  },

  WaxKitMedia_Free: function (ptr) {
    _free(ptr);
  },

  WaxKitMedia_Decode: function (id, dataPtr, length, mimePtr, firstFrameOnly, maxFrames, maxMegabytes) {
    var bytes = HEAPU8.slice(dataPtr, dataPtr + length);
    var mime = UTF8ToString(mimePtr);
    var maxBytes = maxMegabytes * 1024 * 1024;

    function fail(e) { WaxKitMedia.done(id, null, (e && e.message) || e || 'decode failed'); }

    function still() {
      if (typeof createImageBitmap !== 'function') return fail('this browser has no createImageBitmap');
      createImageBitmap(new Blob([bytes], { type: mime }), { premultiplyAlpha: 'none', colorSpaceConversion: 'none' }).then(function (bitmap) {
        var w = bitmap.width, h = bitmap.height;
        if (w * h * 4 > maxBytes) { bitmap.close && bitmap.close(); return fail('the image is larger than the limit'); }
        var ctx = WaxKitMedia.canvas(w, h).getContext('2d', { willReadFrequently: true });
        var frame = WaxKitMedia.pixels(ctx, bitmap, w, h);
        bitmap.close && bitmap.close();
        WaxKitMedia.done(id, WaxKitMedia.pack(w, h, 0, [100], [frame]), null);
      }).catch(fail);
    }

    if (firstFrameOnly || typeof ImageDecoder === 'undefined') return still();

    ImageDecoder.isTypeSupported(mime).then(function (supported) {
      if (!supported) return still();
      var decoder = new ImageDecoder({ data: bytes, type: mime });
      return decoder.tracks.ready.then(function () {
        var track = decoder.tracks.selectedTrack;
        var count = track ? track.frameCount : 1;
        if (count > maxFrames) { decoder.close(); return fail('the animation has more frames than the limit'); }
        var frames = [], delays = [], ctx = null, w = 0, h = 0;
        var loop = !track || track.repetitionCount === Infinity ? 0 : Math.round(track.repetitionCount);
        function next(i) {
          if (i >= count) {
            decoder.close();
            return WaxKitMedia.done(id, WaxKitMedia.pack(w, h, loop, delays, frames), null);
          }
          return decoder.decode({ frameIndex: i, completeFramesOnly: true }).then(function (result) {
            var image = result.image;
            if (!ctx) {
              w = image.displayWidth;
              h = image.displayHeight;
              if (w * h * 4 * count > maxBytes) { image.close(); decoder.close(); throw new Error('the animation needs more memory than the limit'); }
              ctx = WaxKitMedia.canvas(w, h).getContext('2d', { willReadFrequently: true });
            }
            frames.push(WaxKitMedia.pixels(ctx, image, w, h));
            delays.push(Math.round((image.duration || 100000) / 1000));
            image.close();
            return next(i + 1);
          });
        }
        return next(0);
      });
    }).catch(fail);
  },
};

autoAddDeps(WaxKitMediaPlugin, '$WaxKitMedia');
mergeInto(LibraryManager.library, WaxKitMediaPlugin);
