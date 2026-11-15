package dronedetect.ardrone;

import android.content.Context;
import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.util.Log;

import java.nio.ByteBuffer;

/**
 * Hardware H.264 decoding of the AR.Drone 2.0 video stream with MediaCodec.
 * Decoded frames are converted to bottom-up RGBA (Unity texture layout) and
 * cropped to the display size; Unity polls the newest frame via latestRgba().
 */
public final class ArDroneDecoder {
    private static final String TAG = "ArDroneDecoder";
    private static final long INPUT_TIMEOUT_US = 10_000;

    private final int width;
    private final int height;
    private final MediaCodec codec;
    private final MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
    private final byte[] latest;
    private boolean hasNewFrame;
    private long presentationUs;

    public ArDroneDecoder(int width, int height) throws java.io.IOException {
        this.width = width;
        this.height = height;
        this.latest = new byte[width * height * 4];
        codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
        // The drone encodes 640x368 (16-pixel macroblocks); SPS/PPS arrive in-band with every I-frame.
        MediaFormat format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, 368);
        codec.configure(format, null, null, 0);
        codec.start();
    }

    /** Called from Unity's network thread with one Annex-B access unit. */
    public void submit(byte[] data, int length, boolean keyFrame) {
        int inputIndex = codec.dequeueInputBuffer(INPUT_TIMEOUT_US);
        if (inputIndex < 0) {
            Log.w(TAG, "Decoder input queue full; dropping a frame");
            return;
        }
        ByteBuffer input = codec.getInputBuffer(inputIndex);
        input.clear();
        input.put(data, 0, length);
        presentationUs += 33_333;
        codec.queueInputBuffer(inputIndex, 0, length, presentationUs, keyFrame ? MediaCodec.BUFFER_FLAG_KEY_FRAME : 0);
        drainOutput();
    }

    private void drainOutput() {
        int outputIndex;
        while ((outputIndex = codec.dequeueOutputBuffer(info, 0)) >= 0) {
            Image image = codec.getOutputImage(outputIndex);
            if (image != null) {
                synchronized (latest) {
                    convertToRgba(image);
                    hasNewFrame = true;
                }
                image.close();
            }
            codec.releaseOutputBuffer(outputIndex, false);
        }
    }

    /** YUV_420_888 to RGBA (BT.601 video range), flipped vertically and cropped to width x height. */
    private void convertToRgba(Image image) {
        Image.Plane[] planes = image.getPlanes();
        ByteBuffer yBuffer = planes[0].getBuffer();
        ByteBuffer uBuffer = planes[1].getBuffer();
        ByteBuffer vBuffer = planes[2].getBuffer();
        int yRowStride = planes[0].getRowStride();
        int uvRowStride = planes[1].getRowStride();
        int uvPixelStride = planes[1].getPixelStride();
        int rows = Math.min(height, image.getHeight());
        int cols = Math.min(width, image.getWidth());

        for (int y = 0; y < rows; y++) {
            int outRow = (height - 1 - y) * width * 4;
            int yRow = y * yRowStride;
            int uvRow = (y >> 1) * uvRowStride;
            for (int x = 0; x < cols; x++) {
                int luma = (yBuffer.get(yRow + x) & 0xFF) - 16;
                int uvIndex = uvRow + (x >> 1) * uvPixelStride;
                int u = (uBuffer.get(uvIndex) & 0xFF) - 128;
                int v = (vBuffer.get(uvIndex) & 0xFF) - 128;
                int c = 298 * (luma < 0 ? 0 : luma);
                int r = (c + 409 * v + 128) >> 8;
                int g = (c - 100 * u - 208 * v + 128) >> 8;
                int b = (c + 516 * u + 128) >> 8;
                int o = outRow + x * 4;
                latest[o] = (byte) (r < 0 ? 0 : r > 255 ? 255 : r);
                latest[o + 1] = (byte) (g < 0 ? 0 : g > 255 ? 255 : g);
                latest[o + 2] = (byte) (b < 0 ? 0 : b > 255 ? 255 : b);
                latest[o + 3] = (byte) 255;
            }
        }
    }

    /** Returns a copy of the newest frame, or null if none arrived since the last call. */
    public byte[] latestRgba() {
        synchronized (latest) {
            if (!hasNewFrame) {
                return null;
            }
            hasNewFrame = false;
            return latest.clone();
        }
    }

    public void release() {
        codec.stop();
        codec.release();
    }

    /**
     * The drone's access point has no internet, so Android would route sockets over mobile data.
     * Binding the process to the Wi-Fi network keeps all drone traffic on the drone's Wi-Fi.
     */
    public static boolean bindProcessToWifi(Context context) {
        ConnectivityManager manager = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        for (Network network : manager.getAllNetworks()) {
            NetworkCapabilities capabilities = manager.getNetworkCapabilities(network);
            if (capabilities != null && capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
                return manager.bindProcessToNetwork(network);
            }
        }
        Log.w(TAG, "No Wi-Fi network found; connect the phone to the drone's ardrone2_xxxx network");
        return false;
    }
}
