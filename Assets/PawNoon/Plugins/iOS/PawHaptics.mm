// Paw Noon: iOS haptics bridge.
// Core Haptics (iOS 13+, iPhone 8 and later) for every designed moment: one CHHapticEngine, prepared at duel
// start, playing precompiled CHHapticPatternPlayers (one per preset and 0.05 intensity level, built once and
// reused, so nothing is allocated per beat). UIKit feedback generators are the fallback when the engine is
// unavailable (no Taptic Engine, engine stopped / being reset, creation failed) and stay the voice of Notify.
// Called from C# via [DllImport("__Internal")] (see Code/Runtime/PawHaptics.cs). Main thread only: every entry
// point runs on Unity's player thread, which is the iOS main thread; engine callbacks hop back to it.
#import <UIKit/UIKit.h>
#import <CoreHaptics/CoreHaptics.h>
#include <stdlib.h>
#include <string.h>

#if __has_feature(objc_arc)
#define PAW_KEEP(x) (x)
#define PAW_AUTO(x) (x)
#define PAW_DROP(x) ((x) = nil)
#else
#define PAW_KEEP(x) ([(x) retain])
#define PAW_AUTO(x) ([(x) autorelease])
#define PAW_DROP(x) do { [(x) release]; (x) = nil; } while (0)
#endif

#define PAW_SLOTS 128      // HapticPreset.TotalSlots
#define PAW_LEVELS 21      // HapticPolicy.Levels (0.05 steps)
#define PAW_STRIDE 6       // HapticPresets.Stride
#define PAW_KEEPALIVE_S 12.0

static UIImpactFeedbackGenerator *pawImpact[3];
static UINotificationFeedbackGenerator *pawNotify;

static float *pawData[PAW_SLOTS];
static int pawCount[PAW_SLOTS];                    // floats
static id pawPlayers[PAW_SLOTS][PAW_LEVELS];       // id<CHHapticPatternPlayer>
static unsigned char pawWarm[PAW_SLOTS][PAW_LEVELS];
static id pawTransient[PAW_LEVELS][PAW_LEVELS];    // ad-hoc Transient(i, s)
static NSMutableDictionary *pawContinuous;         // ad-hoc Continuous(d, i, s), key = small int (tagged NSNumber)

static id pawEngine;                               // CHHapticEngine
static BOOL pawProbed, pawSupported, pawRunning, pawStarting;
static double pawKeepAliveUntil;
static BOOL pawKeepAliveArmed;
static int pawResets;

// ------------------------------------------------------------------ UIKit fallback

static void PawEnsureGenerators(void)
{
    if (pawNotify != nil) return;
    pawImpact[0] = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleLight];
    pawImpact[1] = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleMedium];
    pawImpact[2] = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleHeavy];
    pawNotify = [[UINotificationFeedbackGenerator alloc] init];
}

static void PawUIKitImpact(float intensity)
{
    PawEnsureGenerators();
    int style = intensity >= 0.85f ? 2 : (intensity >= 0.55f ? 1 : 0);
    UIImpactFeedbackGenerator *g = pawImpact[style];
    if (@available(iOS 13.0, *))
    {
        CGFloat k = style == 2 ? 1.0 : (CGFloat)fminf(1.0f, fmaxf(0.35f, intensity / (style == 1 ? 0.85f : 0.55f)));
        [g impactOccurredWithIntensity:k];
    }
    else [g impactOccurred];
    [g prepare];
}

/// Plays the transients of a preset on the UIKit generators, at their times (frame-quantized: the main queue
/// is serviced between frames). Continuous tails are dropped (a generator can only tap); a preset made only of
/// continuous events becomes one tap at its start intensity.
static void PawUIKitPreset(const float *ev, int count, float scale, double delay)
{
    int n = count / PAW_STRIDE;
    BOOL anyTransient = NO;
    for (int e = 0; e < n; e++) if (ev[e * PAW_STRIDE + 1] <= 0.0f) anyTransient = YES;
    for (int e = 0; e < n; e++)
    {
        const float *o = ev + e * PAW_STRIDE;
        if (anyTransient && o[1] > 0.0f) continue;
        float k = o[2] * scale;
        if (k <= 0.02f) continue;
        double at = delay + o[0];
        if (at <= 0.004) PawUIKitImpact(k);
        else dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(at * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{ PawUIKitImpact(k); });
    }
}

// ------------------------------------------------------------------ Core Haptics engine

static void PawDropPlayers(void)
{
    for (int s = 0; s < PAW_SLOTS; s++)
        for (int l = 0; l < PAW_LEVELS; l++)
            if (pawPlayers[s][l] != nil) PAW_DROP(pawPlayers[s][l]);
    for (int i = 0; i < PAW_LEVELS; i++)
        for (int j = 0; j < PAW_LEVELS; j++)
            if (pawTransient[i][j] != nil) PAW_DROP(pawTransient[i][j]);
    [pawContinuous removeAllObjects];
}

static void PawWarmAll(void);
static void PawStartEngine(void);

static BOOL PawEnsureEngine(void)
{
    if (@available(iOS 13.0, *))
    {
        if (!pawProbed)
        {
            pawProbed = YES;
            pawSupported = [CHHapticEngine capabilitiesForHardware].supportsHaptics;
            pawContinuous = [[NSMutableDictionary alloc] init];
        }
        if (!pawSupported) return NO;
        if (pawEngine == nil)
        {
            NSError *err = nil;
            CHHapticEngine *e = [[CHHapticEngine alloc] initAndReturnError:&err];
            if (e == nil)
            {
                NSLog(@"[PawHaptics] CHHapticEngine unavailable: %@", err);
                return NO;
            }
            e.playsHapticsOnly = YES;          // never touches Unity's audio session
            e.autoShutdownEnabled = YES;       // idle power-down; Prepare() holds it up during a duel
            e.stoppedHandler = ^(CHHapticEngineStoppedReason reason) {
                dispatch_async(dispatch_get_main_queue(), ^{ pawRunning = NO; pawStarting = NO; });
            };
            e.resetHandler = ^{
                // the haptic server restarted: every player is invalid; rebuild on the main thread and restart
                dispatch_async(dispatch_get_main_queue(), ^{
                    pawResets++;
                    pawRunning = NO;
                    pawStarting = NO;
                    PawDropPlayers();
                    PawStartEngine();
                });
            };
            pawEngine = e;   // alloc'd: already +1 under MRC
        }
        return YES;
    }
    return NO;
}

static void PawStartEngine(void)
{
    if (@available(iOS 13.0, *))
    {
        if (!PawEnsureEngine() || pawRunning || pawStarting) return;
        pawStarting = YES;
        [(CHHapticEngine *)pawEngine startWithCompletionHandler:^(NSError *error) {
            dispatch_async(dispatch_get_main_queue(), ^{
                pawStarting = NO;
                pawRunning = error == nil;
                if (error != nil) NSLog(@"[PawHaptics] engine start failed: %@", error);
                else PawWarmAll();
            });
        }];
    }
}

static CHHapticEventParameter *PawParam(CHHapticEventParameterID pid, float v) API_AVAILABLE(ios(13.0));
static CHHapticEventParameter *PawParam(CHHapticEventParameterID pid, float v)
{
    return PAW_AUTO([[CHHapticEventParameter alloc] initWithParameterID:pid value:fminf(1.0f, fmaxf(0.0f, v))]);
}

static CHHapticParameterCurveControlPoint *PawPoint(double t, float v) API_AVAILABLE(ios(13.0));
static CHHapticParameterCurveControlPoint *PawPoint(double t, float v)
{
    return PAW_AUTO([[CHHapticParameterCurveControlPoint alloc] initWithRelativeTime:t value:v]);
}

/// Builds a player for events scaled by `scale`. Continuous events get an intensity curve (multiplier) and a
/// sharpness curve (offset) over their window; each curve snaps back to neutral right after its event ends so
/// a later transient is never scaled by it (authoring rule: no transient inside a continuous window).
static id PawBuildPlayer(const float *ev, int count, float scale) API_AVAILABLE(ios(13.0));
static id PawBuildPlayer(const float *ev, int count, float scale)
{
    if (pawEngine == nil || count < PAW_STRIDE) return nil;
    int n = count / PAW_STRIDE;
    NSMutableArray *events = [NSMutableArray arrayWithCapacity:n];
    NSMutableArray *curves = [NSMutableArray array];
    for (int e = 0; e < n; e++)
    {
        const float *o = ev + e * PAW_STRIDE;
        double t = o[0];
        if (o[1] <= 0.0f)
        {
            NSArray *ps = @[ PawParam(CHHapticEventParameterIDHapticIntensity, o[2] * scale),
                             PawParam(CHHapticEventParameterIDHapticSharpness, o[4]) ];
            [events addObject:PAW_AUTO([[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticTransient parameters:ps relativeTime:t])];
        }
        else
        {
            double d = o[1];
            NSArray *ps = @[ PawParam(CHHapticEventParameterIDHapticIntensity, 1.0f),
                             PawParam(CHHapticEventParameterIDHapticSharpness, o[4]) ];
            [events addObject:PAW_AUTO([[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticContinuous parameters:ps relativeTime:t duration:d])];
            float i0 = fminf(1.0f, fmaxf(0.0f, o[2] * scale)), i1 = fminf(1.0f, fmaxf(0.0f, o[3] * scale));
            NSArray *ip = @[ PawPoint(0, i0), PawPoint(d, i1), PawPoint(d + 0.002, 1.0f) ];
            [curves addObject:PAW_AUTO([[CHHapticParameterCurve alloc] initWithParameterID:CHHapticDynamicParameterIDHapticIntensityControl controlPoints:ip relativeTime:t])];
            float ds = o[5] - o[4];
            if (fabsf(ds) > 0.01f)
            {
                NSArray *sp = @[ PawPoint(0, 0.0f), PawPoint(d, ds), PawPoint(d + 0.002, 0.0f) ];
                [curves addObject:PAW_AUTO([[CHHapticParameterCurve alloc] initWithParameterID:CHHapticDynamicParameterIDHapticSharpnessControl controlPoints:sp relativeTime:t])];
            }
        }
    }
    NSError *err = nil;
    CHHapticPattern *pattern = [[CHHapticPattern alloc] initWithEvents:events parameterCurves:curves error:&err];
    if (pattern == nil) { NSLog(@"[PawHaptics] pattern error: %@", err); return nil; }
    id<CHHapticPatternPlayer> p = [(CHHapticEngine *)pawEngine createPlayerWithPattern:pattern error:&err];
#if !__has_feature(objc_arc)
    [pattern release];
#endif
    if (p == nil) { NSLog(@"[PawHaptics] player error: %@", err); return nil; }
    return PAW_KEEP(p);
}

static id PawPresetPlayer(int slot, int level)
{
    if (@available(iOS 13.0, *))
    {
        if (slot < 0 || slot >= PAW_SLOTS || level < 0 || level >= PAW_LEVELS || pawData[slot] == NULL) return nil;
        id p = pawPlayers[slot][level];
        if (p == nil)
        {
            p = PawBuildPlayer(pawData[slot], pawCount[slot], level / (float)(PAW_LEVELS - 1));
            pawPlayers[slot][level] = p;
        }
        return p;
    }
    return nil;
}

static void PawWarmAll(void)
{
    if (!pawRunning) return;
    for (int s = 0; s < PAW_SLOTS; s++)
        for (int l = 0; l < PAW_LEVELS; l++)
            if (pawWarm[s][l]) PawPresetPlayer(s, l);
}

/// Starts a player `delay` seconds from now on the engine clock. NO if it could not (caller falls back).
static BOOL PawStart(id player, double delay)
{
    if (@available(iOS 13.0, *))
    {
        if (player == nil || !pawRunning) return NO;
        NSTimeInterval at = delay > 0.0005 ? ((CHHapticEngine *)pawEngine).currentTime + delay : CHHapticTimeImmediate;
        NSError *err = nil;
        if ([(id<CHHapticPatternPlayer>)player startAtTime:at error:&err]) return YES;
        NSLog(@"[PawHaptics] start failed: %@", err);
        pawRunning = NO;   // treat as stopped: next Prepare restarts the engine
        return NO;
    }
    return NO;
}

/// Re-enables idle auto-shutdown once no Prepare() came for PAW_KEEPALIVE_S (main queue, no blocks).
static void PawKeepAliveCheck(void *ctx)
{
    double now = CACurrentMediaTime();
    if (now >= pawKeepAliveUntil - 0.01)
    {
        pawKeepAliveArmed = NO;
        if (@available(iOS 13.0, *)) { if (pawEngine != nil) ((CHHapticEngine *)pawEngine).autoShutdownEnabled = YES; }
        return;
    }
    dispatch_after_f(dispatch_time(DISPATCH_TIME_NOW, (int64_t)((pawKeepAliveUntil - now) * NSEC_PER_SEC)), dispatch_get_main_queue(), NULL, PawKeepAliveCheck);
}

static int PawLevel(float v)
{
    if (!(v > 0.0f)) return 0;
    if (v >= 1.0f) return PAW_LEVELS - 1;
    return (int)lrintf(v * (PAW_LEVELS - 1));
}

extern "C" {

// ------------------------------------------------------------------ legacy entry points (kept)

/// Prepares the UIKit generators and starts (or keeps up) the Core Haptics engine. Cheap when already up:
/// call at duel start, on the last countdown numeral and once a second during the hold.
void PawHaptics_Prepare(void)
{
    PawEnsureGenerators();
    for (int i = 0; i < 3; i++) [pawImpact[i] prepare];
    [pawNotify prepare];
    if (@available(iOS 13.0, *))
    {
        if (!PawEnsureEngine()) return;
        // hold the engine up for the duel; it powers down again PAW_KEEPALIVE_S after the last Prepare
        pawKeepAliveUntil = CACurrentMediaTime() + PAW_KEEPALIVE_S;
        ((CHHapticEngine *)pawEngine).autoShutdownEnabled = NO;
        if (!pawKeepAliveArmed)
        {
            pawKeepAliveArmed = YES;
            dispatch_after_f(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(PAW_KEEPALIVE_S * NSEC_PER_SEC)), dispatch_get_main_queue(), NULL, PawKeepAliveCheck);
        }
        if (!pawRunning) PawStartEngine();
        else PawWarmAll();
    }
}

void PawHaptics_Impact(int style)
{
    PawEnsureGenerators();
    if (style < 0) style = 0;
    if (style > 2) style = 2;
    [pawImpact[style] impactOccurred];
    [pawImpact[style] prepare];
}

void PawHaptics_Notify(int type)
{
    PawEnsureGenerators();
    UINotificationFeedbackType t = UINotificationFeedbackTypeSuccess;
    if (type == 1) t = UINotificationFeedbackTypeWarning;
    else if (type >= 2) t = UINotificationFeedbackTypeError;
    [pawNotify notificationOccurred:t];
    [pawNotify prepare];
}

// ------------------------------------------------------------------ Core Haptics entry points

/// Stores a preset's events (stride 6: t, dur, i0, i1, s0, s1). Replacing a slot drops its players.
void PawHaptics_RegisterPreset(int slot, const float *data, int count)
{
    if (slot < 0 || slot >= PAW_SLOTS || data == NULL || count < PAW_STRIDE) return;
    for (int l = 0; l < PAW_LEVELS; l++) if (pawPlayers[slot][l] != nil) PAW_DROP(pawPlayers[slot][l]);
    free(pawData[slot]);
    pawData[slot] = (float *)malloc(sizeof(float) * count);
    memcpy(pawData[slot], data, sizeof(float) * count);
    pawCount[slot] = count;
}

/// Marks (slot, level) to be precompiled whenever the engine (re)starts, and builds it now if it is running.
void PawHaptics_Warm(int slot, int level)
{
    if (slot < 0 || slot >= PAW_SLOTS || level < 0 || level >= PAW_LEVELS) return;
    pawWarm[slot][level] = 1;
    if (pawRunning) PawPresetPlayer(slot, level);
}

/// Plays a registered preset at scale (0..1, quantized to 0.05) `delay` seconds from now.
/// Engine down: UIKit fallback (and the engine is asked to start for next time).
void PawHaptics_PlayPreset(int slot, float scale, double delay)
{
    if (slot < 0 || slot >= PAW_SLOTS || pawData[slot] == NULL) return;
    int level = PawLevel(scale);
    if (level == 0) return;
    if (pawRunning && PawStart(PawPresetPlayer(slot, level), delay)) return;
    PawUIKitPreset(pawData[slot], pawCount[slot], level / (float)(PAW_LEVELS - 1), delay);
    if (!pawRunning) PawStartEngine();
}

void PawHaptics_Transient(float intensity, float sharpness, double delay)
{
    int il = PawLevel(intensity), sl = PawLevel(sharpness);
    if (il == 0) return;
    if (@available(iOS 13.0, *))
    {
        if (pawRunning)
        {
            id p = pawTransient[il][sl];
            if (p == nil)
            {
                float ev[PAW_STRIDE] = { 0, 0, il / (float)(PAW_LEVELS - 1), 0, sl / (float)(PAW_LEVELS - 1), 0 };
                p = PawBuildPlayer(ev, PAW_STRIDE, 1.0f);
                pawTransient[il][sl] = p;
            }
            if (PawStart(p, delay)) return;
        }
    }
    float ev[PAW_STRIDE] = { 0, 0, intensity, intensity, sharpness, sharpness };
    PawUIKitPreset(ev, PAW_STRIDE, 1.0f, delay);
    if (!pawRunning) PawStartEngine();
}

void PawHaptics_Continuous(float duration, float intensity, float sharpness, double delay)
{
    int il = PawLevel(intensity), sl = PawLevel(sharpness);
    int dq = (int)lrintf(fminf(2.0f, fmaxf(0.01f, duration)) * 100.0f);   // 10 ms steps, 0.01..2 s
    if (il == 0) return;
    if (@available(iOS 13.0, *))
    {
        if (pawRunning)
        {
            NSNumber *key = @((dq * PAW_LEVELS + il) * PAW_LEVELS + sl);   // small int: tagged pointer, no allocation
            id p = [pawContinuous objectForKey:key];
            if (p == nil)
            {
                float k = il / (float)(PAW_LEVELS - 1), s = sl / (float)(PAW_LEVELS - 1);
                float ev[PAW_STRIDE] = { 0, dq / 100.0f, k, k, s, s };
                p = PawBuildPlayer(ev, PAW_STRIDE, 1.0f);
                if (p != nil)
                {
                    [pawContinuous setObject:p forKey:key];
#if !__has_feature(objc_arc)
                    [p release];
#endif
                }
            }
            if (PawStart(p, delay)) return;
        }
    }
    float ev[PAW_STRIDE] = { 0, 0, intensity, intensity, sharpness, sharpness };
    PawUIKitPreset(ev, PAW_STRIDE, 1.0f, delay);
    if (!pawRunning) PawStartEngine();
}

/// Stops everything playing (Haptics toggled off). The engine stays prepared.
void PawHaptics_StopAll(void)
{
    if (@available(iOS 13.0, *))
    {
        if (pawEngine == nil || !pawRunning) return;
        for (int s = 0; s < PAW_SLOTS; s++)
            for (int l = 0; l < PAW_LEVELS; l++)
                if (pawPlayers[s][l] != nil) [(id<CHHapticPatternPlayer>)pawPlayers[s][l] stopAtTime:CHHapticTimeImmediate error:nil];
    }
}

/// Bit 0: Core Haptics supported, 1: engine running, 2: engine starting, 3: engine created; bits 8+: resets.
int PawHaptics_Caps(void)
{
    if (@available(iOS 13.0, *))
    {
        if (!pawProbed) PawEnsureEngine();
    }
    return (pawSupported ? 1 : 0) | (pawRunning ? 2 : 0) | (pawStarting ? 4 : 0) | (pawEngine != nil ? 8 : 0) | (pawResets << 8);
}

}
