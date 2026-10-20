// Paw Noon: iOS audio session. Unity's default category (Ambient) obeys the ring/silent switch, so a phone in
// silent mode plays the game with no music or sound while Core Haptics still vibrates. A duel game with a
// soundtrack uses Playback instead. If the player already has their own audio playing (Spotify, a podcast) we
// mix with it rather than stopping it.
#import <AVFoundation/AVFoundation.h>

extern "C" void PawAudio_UsePlaybackSession(void)
{
    AVAudioSession* session = [AVAudioSession sharedInstance];
    AVAudioSessionCategoryOptions options = session.isOtherAudioPlaying ? AVAudioSessionCategoryOptionMixWithOthers : 0;
    NSError* error = nil;
    [session setCategory:AVAudioSessionCategoryPlayback mode:AVAudioSessionModeDefault options:options error:&error];
    if (error != nil) NSLog(@"[PawNoon] audio session category: %@", error);
    error = nil;
    [session setActive:YES error:&error];
    if (error != nil) NSLog(@"[PawNoon] audio session activate: %@", error);
}
