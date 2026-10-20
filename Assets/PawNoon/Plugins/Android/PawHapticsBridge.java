// Paw Noon: Android haptics bridge (compiled by Unity's Gradle build into unityLibrary).
// Called from C# (Code/Runtime/PawHaptics.cs): one-time setup through AndroidJavaClass, hot calls through raw
// JNI with reused argument arrays (no C# allocation per haptic).
//
// - Vibrator from VibratorManager (API 31+) or the VIBRATOR_SERVICE (26..30).
// - Effects are built once and cached (presets per 0.05 scale level, transients per 0.05 intensity/sharpness):
//   Composition primitives (API 30+, when the device supports them) for tap-only presets, otherwise
//   createWaveform with amplitudes (hasAmplitudeControl) or on/off, createPredefined (API 29+) for plain taps on
//   devices without amplitude control, createOneShot for the rest.
// - Every vibrate() runs on a dedicated high-priority HandlerThread, scheduled with sendMessageAtTime, so the
//   Unity thread never blocks on the vibrator service and the DRAW cue can be timed to the millisecond.
// - The system "Touch feedback" setting (Settings.System.HAPTIC_FEEDBACK_ENABLED) is respected: off = silent.
package com.pawnoon.haptics;

import android.annotation.TargetApi;
import android.content.Context;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.Message;
import android.os.Process;
import android.os.SystemClock;
import android.os.VibrationAttributes;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.os.VibratorManager;
import android.provider.Settings;
import android.util.Log;
import android.util.SparseArray;

public final class PawHapticsBridge {
    private static final String TAG = "PawHaptics";
    private static final int LEVELS = 21;          // HapticPolicy.Levels
    private static final int SLOTS = 128;          // HapticPreset.TotalSlots
    private static final int MSG_PRESET = 1, MSG_TRANSIENT = 2, MSG_CONTINUOUS = 3, MSG_CANCEL = 4, MSG_REFRESH = 5;

    /** Capability bits returned by init(): mirrored in PawHaptics.cs. */
    public static final int CAP_VIBRATOR = 1, CAP_AMPLITUDE = 2, CAP_PRIMITIVES = 4, CAP_SYSTEM_ON = 8;

    private static Context context;
    private static Vibrator vibrator;
    private static boolean amplitude;
    private static boolean primitives;
    private static boolean primThud, primLowTick;
    private static volatile boolean systemOn = true;
    private static Handler handler;
    private static Object attributes;              // VibrationAttributes (API 33+)

    private static final VibrationEffect[] presets = new VibrationEffect[SLOTS * LEVELS];
    private static final int[] presetDelayMs = new int[SLOTS * LEVELS];
    private static final VibrationEffect[] transients = new VibrationEffect[LEVELS * LEVELS];
    private static final SparseArray<VibrationEffect> continuous = new SparseArray<VibrationEffect>();

    private PawHapticsBridge() { }

    /** Sets everything up (idempotent). Returns the CAP_* bits (0 = no vibrator / failure). */
    public static synchronized int init(Context ctx) {
        try {
            if (vibrator == null && ctx != null) {
                context = ctx.getApplicationContext();
                if (Build.VERSION.SDK_INT >= 31) vibrator = defaultVibrator31(context);
                else vibrator = (Vibrator) context.getSystemService(Context.VIBRATOR_SERVICE);
                if (vibrator != null && !vibrator.hasVibrator()) vibrator = null;
                if (vibrator != null) {
                    amplitude = vibrator.hasAmplitudeControl();
                    if (Build.VERSION.SDK_INT >= 30) probePrimitives30();
                    if (Build.VERSION.SDK_INT >= 33) attributes = mediaAttributes33();
                    HandlerThread t = new HandlerThread("PawHaptics", Process.THREAD_PRIORITY_DISPLAY);
                    t.start();
                    handler = new Handler(t.getLooper()) {
                        @Override public void handleMessage(Message m) { handle(m); }
                    };
                }
            }
            readSystemSetting();
        } catch (Throwable e) {
            Log.w(TAG, "init failed: " + e);
            vibrator = null;
        }
        return caps();
    }

    public static int caps() {
        if (vibrator == null) return 0;
        return CAP_VIBRATOR | (amplitude ? CAP_AMPLITUDE : 0) | (primitives ? CAP_PRIMITIVES : 0) | (systemOn ? CAP_SYSTEM_ON : 0);
    }

    /** Re-reads the Touch feedback setting off the Unity thread; returns the last known value. */
    public static boolean prepare() {
        Handler h = handler;
        if (h != null) h.sendEmptyMessage(MSG_REFRESH);
        return systemOn;
    }

    public static int apiLevel() { return Build.VERSION.SDK_INT; }

    /**
     * Registers a preset at one scale level. timings/amps: createWaveform data (leading silence removed, given as
     * startDelayMs). transients: {ms, intensity, sharpness} triples, or empty when the preset has a continuous part.
     */
    public static void registerPreset(int slot, int level, long[] timings, int[] amps, float[] transients, int startDelayMs) {
        if (vibrator == null || slot < 0 || slot >= SLOTS || level < 0 || level >= LEVELS) return;
        try {
            VibrationEffect fx = null;
            if (primitives && transients != null && transients.length >= 3) fx = composition30(transients);
            if (fx == null && timings != null && timings.length > 0) {
                if (timings.length == 1) fx = VibrationEffect.createOneShot(timings[0], amplitude ? amps[0] : VibrationEffect.DEFAULT_AMPLITUDE);
                else fx = VibrationEffect.createWaveform(timings, amps, -1);
            }
            synchronized (presets) {
                presets[slot * LEVELS + level] = fx;
                presetDelayMs[slot * LEVELS + level] = Math.max(0, startDelayMs);
            }
        } catch (Throwable e) {
            Log.w(TAG, "registerPreset " + slot + " failed: " + e);
        }
    }

    // ------------------------------------------------------------------ hot path (raw JNI from C#)

    /** Plays a registered preset (slot, level) delayMs from now. */
    public static void playPreset(int key, int delayMs) {
        post(MSG_PRESET, key, delayMs);
    }

    /** One tap: intensity and sharpness levels 0..20. */
    public static void transientFx(int intensityLevel, int sharpnessLevel, int delayMs) {
        post(MSG_TRANSIENT, intensityLevel * LEVELS + sharpnessLevel, delayMs);
    }

    /** A buzz: duration in 10 ms steps (1..200), intensity level 0..20 (sharpness has no Android equivalent). */
    public static void continuousFx(int durationSteps, int intensityLevel, int delayMs) {
        post(MSG_CONTINUOUS, durationSteps * LEVELS + intensityLevel, delayMs);
    }

    public static void cancel() {
        Handler h = handler;
        if (h == null) return;
        h.removeMessages(MSG_PRESET);
        h.removeMessages(MSG_TRANSIENT);
        h.removeMessages(MSG_CONTINUOUS);
        h.sendEmptyMessage(MSG_CANCEL);
    }

    private static void post(int what, int arg, int delayMs) {
        Handler h = handler;
        if (h == null || !systemOn) return;
        Message m = Message.obtain(h, what, arg, 0);   // pooled
        h.sendMessageAtTime(m, SystemClock.uptimeMillis() + Math.max(0, delayMs));
    }

    // ------------------------------------------------------------------ haptic thread

    private static void handle(Message m) {
        try {
            switch (m.what) {
                case MSG_REFRESH: readSystemSetting(); return;
                case MSG_CANCEL: vibrator.cancel(); return;
                case MSG_PRESET: {
                    VibrationEffect fx;
                    int extra;
                    synchronized (presets) {
                        if (m.arg1 < 0 || m.arg1 >= presets.length) return;
                        fx = presets[m.arg1];
                        extra = presetDelayMs[m.arg1];
                    }
                    if (fx == null) return;
                    if (extra > 0 && m.arg2 == 0) {
                        // leading silence of the preset: wait it out here instead of cancelling the current effect early
                        Message again = Message.obtain(handler, MSG_PRESET, m.arg1, 1);
                        handler.sendMessageAtTime(again, SystemClock.uptimeMillis() + extra);
                        return;
                    }
                    vibrate(fx);
                    return;
                }
                case MSG_TRANSIENT: {
                    int k = m.arg1;
                    if (k < 0 || k >= transients.length) return;
                    VibrationEffect fx = transients[k];
                    if (fx == null) { fx = buildTransient(k / LEVELS, k % LEVELS); transients[k] = fx; }
                    if (fx != null) vibrate(fx);
                    return;
                }
                case MSG_CONTINUOUS: {
                    int k = m.arg1;
                    VibrationEffect fx = continuous.get(k);
                    if (fx == null) {
                        int steps = k / LEVELS, il = k % LEVELS;
                        int amp = Math.max(1, Math.min(255, Math.round(il * 255f / (LEVELS - 1))));
                        fx = VibrationEffect.createOneShot(Math.max(10, steps * 10), amplitude ? amp : VibrationEffect.DEFAULT_AMPLITUDE);
                        continuous.put(k, fx);
                    }
                    vibrate(fx);
                    return;
                }
                default: return;
            }
        } catch (Throwable e) {
            Log.w(TAG, "haptic failed: " + e);
        }
    }

    private static void vibrate(VibrationEffect fx) {
        if (vibrator == null || fx == null || !systemOn) return;
        if (Build.VERSION.SDK_INT >= 33 && attributes != null) vibrate33(fx);
        else vibrator.vibrate(fx);
    }

    private static VibrationEffect buildTransient(int il, int sl) {
        if (il <= 0) return null;
        float i = il / (float) (LEVELS - 1), s = sl / (float) (LEVELS - 1);
        if (primitives) {
            VibrationEffect c = composition30(new float[] { 0f, i, s });
            if (c != null) return c;
        }
        int ms = Math.round(20 - 12 * s);   // HapticRaster.TransientMs
        if (amplitude) return VibrationEffect.createOneShot(ms, Math.max(1, Math.min(255, Math.round(i * 255f))));
        if (Build.VERSION.SDK_INT >= 29) return predefined29(i);
        return VibrationEffect.createOneShot(ms, VibrationEffect.DEFAULT_AMPLITUDE);
    }

    private static void readSystemSetting() {
        try {
            if (context != null)
                systemOn = Settings.System.getInt(context.getContentResolver(), Settings.System.HAPTIC_FEEDBACK_ENABLED, 1) != 0;
        } catch (Throwable e) {
            systemOn = true;
        }
    }

    // ------------------------------------------------------------------ API-level helpers

    @TargetApi(31)
    private static Vibrator defaultVibrator31(Context c) {
        VibratorManager vm = (VibratorManager) c.getSystemService(Context.VIBRATOR_MANAGER_SERVICE);
        return vm != null ? vm.getDefaultVibrator() : null;
    }

    @TargetApi(30)
    private static void probePrimitives30() {
        primitives = vibrator.areAllPrimitivesSupported(VibrationEffect.Composition.PRIMITIVE_CLICK, VibrationEffect.Composition.PRIMITIVE_TICK);
        if (Build.VERSION.SDK_INT >= 31) {
            primThud = vibrator.areAllPrimitivesSupported(VibrationEffect.Composition.PRIMITIVE_THUD);
            primLowTick = vibrator.areAllPrimitivesSupported(VibrationEffect.Composition.PRIMITIVE_LOW_TICK);
        }
    }

    /** Sharp = CLICK (or TICK when light), soft = THUD (or LOW_TICK when light) where supported. */
    @TargetApi(30)
    private static int primitiveFor(float intensity, float sharpness) {
        if (sharpness >= 0.45f) return intensity < 0.5f ? VibrationEffect.Composition.PRIMITIVE_TICK : VibrationEffect.Composition.PRIMITIVE_CLICK;
        if (intensity < 0.5f && primLowTick) return VibrationEffect.Composition.PRIMITIVE_LOW_TICK;   // API 31
        if (primThud) return VibrationEffect.Composition.PRIMITIVE_THUD;                            // API 31
        return VibrationEffect.Composition.PRIMITIVE_CLICK;
    }

    /** {ms, intensity, sharpness} triples to a Composition; null if empty. */
    @TargetApi(30)
    private static VibrationEffect composition30(float[] t) {
        int n = t.length / 3;
        if (n == 0) return null;
        VibrationEffect.Composition c = VibrationEffect.startComposition();
        int prevMs = 0;
        for (int e = 0; e < n; e++) {
            int at = Math.round(t[e * 3]);
            float scale = Math.max(0f, Math.min(1f, t[e * 3 + 1]));
            c.addPrimitive(primitiveFor(scale, t[e * 3 + 2]), scale, Math.max(0, at - prevMs));
            prevMs = at;
        }
        return c.compose();
    }

    @TargetApi(29)
    private static VibrationEffect predefined29(float intensity) {
        int id = intensity >= 0.85f ? VibrationEffect.EFFECT_HEAVY_CLICK : (intensity >= 0.5f ? VibrationEffect.EFFECT_CLICK : VibrationEffect.EFFECT_TICK);
        return VibrationEffect.createPredefined(id);
    }

    /** USAGE_MEDIA: Android's usage for game / interactive-media haptics. */
    @TargetApi(33)
    private static Object mediaAttributes33() {
        return VibrationAttributes.createForUsage(VibrationAttributes.USAGE_MEDIA);
    }

    @TargetApi(33)
    private static void vibrate33(VibrationEffect fx) {
        vibrator.vibrate(fx, (VibrationAttributes) attributes);
    }
}
