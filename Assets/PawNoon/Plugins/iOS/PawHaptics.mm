// Paw Noon — iOS haptics bridge (UIKit feedback generators).
// Called from C# via [DllImport("__Internal")] (see Code/Runtime/PawHaptics.cs).
#import <UIKit/UIKit.h>

static UIImpactFeedbackGenerator *pawImpact[3];
static UINotificationFeedbackGenerator *pawNotify;

static void PawEnsureGenerators(void)
{
    if (pawNotify != nil) return;
    pawImpact[0] = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleLight];
    pawImpact[1] = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleMedium];
    pawImpact[2] = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleHeavy];
    pawNotify = [[UINotificationFeedbackGenerator alloc] init];
}

extern "C" {

void PawHaptics_Prepare(void)
{
    PawEnsureGenerators();
    for (int i = 0; i < 3; i++) [pawImpact[i] prepare];
    [pawNotify prepare];
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

}
