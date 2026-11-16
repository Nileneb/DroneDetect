package dronedetect.rec;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.provider.MediaStore;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;

/**
 * Screen recording of the ground station (video + boxes + telemetry) into Movies/DroneFollow.
 * MediaProjection feeds the hardware H.264 encoder through a Surface, so Unity does no pixel readback.
 * start() opens a transparent consent Activity, which then starts the foreground Service that records.
 */
public final class ScreenRecorder {
    static final String TAG = "ScreenRecorder";
    static volatile boolean recording;
    static volatile String lastFile = "";

    private ScreenRecorder() {}

    public static void start(Activity activity) {
        if (recording)
            return;
        activity.startActivity(new Intent(activity, ConsentActivity.class));
    }

    public static void stop(Context context) {
        context.stopService(new Intent(context, RecordService.class));
    }

    public static boolean isRecording() { return recording; }

    public static String lastFile() { return lastFile; }

    /** Transparent Activity that only asks for the MediaProjection consent. */
    public static final class ConsentActivity extends Activity {
        private static final int REQUEST = 4711;

        @Override
        protected void onCreate(Bundle state) {
            super.onCreate(state);
            MediaProjectionManager manager = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            // Android 14+: ask for the whole default display directly instead of the single-app picker.
            Intent request = Build.VERSION.SDK_INT >= 34
                ? manager.createScreenCaptureIntent(android.media.projection.MediaProjectionConfig.createConfigForDefaultDisplay())
                : manager.createScreenCaptureIntent();
            startActivityForResult(request, REQUEST);
        }

        @Override
        protected void onActivityResult(int requestCode, int resultCode, Intent data) {
            super.onActivityResult(requestCode, resultCode, data);
            if (requestCode == REQUEST && resultCode == RESULT_OK && data != null) {
                Intent service = new Intent(this, RecordService.class);
                service.putExtra("resultCode", resultCode);
                service.putExtra("data", data);
                startForegroundService(service);
            } else {
                Log.w(TAG, "Screen capture consent denied");
            }
            finish();
        }
    }

    public static final class RecordService extends Service {
        private MediaProjection projection;
        private MediaRecorder recorder;
        private VirtualDisplay display;
        private Uri outputUri;
        private ParcelFileDescriptor outputFd;

        @Override
        public IBinder onBind(Intent intent) { return null; }

        @Override
        public int onStartCommand(Intent intent, int flags, int startId) {
            if (intent == null || recording)
                return START_NOT_STICKY;

            String channelId = "dronefollow_rec";
            NotificationManager notifications = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            notifications.createNotificationChannel(
                new NotificationChannel(channelId, "Recording", NotificationManager.IMPORTANCE_LOW));
            Notification notification = new Notification.Builder(this, channelId)
                .setContentTitle("DroneFollow recording")
                .setSmallIcon(android.R.drawable.ic_menu_camera)
                .build();
            // The foreground service must be running before the projection token is used (Android 14+).
            startForeground(4712, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);

            try {
                MediaProjectionManager manager = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
                projection = manager.getMediaProjection(intent.getIntExtra("resultCode", 0), (Intent) intent.getParcelableExtra("data"));
                projection.registerCallback(new MediaProjection.Callback() {
                    @Override public void onStop() { stopSelf(); }
                }, new Handler(Looper.getMainLooper()));
                beginRecording();
                recording = true;
            } catch (Exception e) {
                Log.e(TAG, "Could not start recording", e);
                stopSelf();
            }
            return START_NOT_STICKY;
        }

        private void beginRecording() throws Exception {
            DisplayMetrics metrics = new DisplayMetrics();
            ((WindowManager) getSystemService(WINDOW_SERVICE)).getDefaultDisplay().getRealMetrics(metrics);
            // Cap the long side at 1280 px: the phone is already busy with four neural networks.
            float scale = Math.min(1f, 1280f / Math.max(metrics.widthPixels, metrics.heightPixels));
            int width = (Math.round(metrics.widthPixels * scale) / 2) * 2;
            int height = (Math.round(metrics.heightPixels * scale) / 2) * 2;

            String name = "DroneFollow_" + new java.text.SimpleDateFormat("yyyyMMdd_HHmmss", java.util.Locale.US).format(new java.util.Date());
            ContentValues values = new ContentValues();
            values.put(MediaStore.Video.Media.DISPLAY_NAME, name + ".mp4");
            values.put(MediaStore.Video.Media.MIME_TYPE, "video/mp4");
            values.put(MediaStore.Video.Media.RELATIVE_PATH, "Movies/DroneFollow");
            values.put(MediaStore.Video.Media.IS_PENDING, 1);
            ContentResolver resolver = getContentResolver();
            outputUri = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values);
            outputFd = resolver.openFileDescriptor(outputUri, "w");

            recorder = new MediaRecorder(this);
            recorder.setVideoSource(MediaRecorder.VideoSource.SURFACE);
            recorder.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4);
            recorder.setVideoEncoder(MediaRecorder.VideoEncoder.H264);
            recorder.setVideoSize(width, height);
            recorder.setVideoFrameRate(30);
            recorder.setVideoEncodingBitRate(6_000_000);
            recorder.setOutputFile(outputFd.getFileDescriptor());
            recorder.prepare();

            display = projection.createVirtualDisplay("DroneFollowRec", width, height, metrics.densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, recorder.getSurface(), null, null);
            recorder.start();
            lastFile = name + ".mp4";
            Log.i(TAG, "Recording " + width + "x" + height + " to Movies/DroneFollow/" + lastFile);
        }

        @Override
        public void onDestroy() {
            recording = false;
            try { if (recorder != null) recorder.stop(); } catch (RuntimeException e) { Log.w(TAG, "stop: " + e); }
            if (recorder != null) recorder.release();
            if (display != null) display.release();
            if (projection != null) projection.stop();
            try { if (outputFd != null) outputFd.close(); } catch (java.io.IOException ignored) {}
            if (outputUri != null) {
                ContentValues done = new ContentValues();
                done.put(MediaStore.Video.Media.IS_PENDING, 0);
                getContentResolver().update(outputUri, done, null, null);
            }
            super.onDestroy();
        }
    }
}
