// Paw Noon — iOS share sheet bridge (UIActivityViewController).
// Called from C# via [DllImport("__Internal")] (see Code/Runtime/ShareService.cs, NativeShare).
// "Save Image" in the sheet needs NSPhotoLibraryAddUsageDescription in Info.plist (added by
// Code/Editor/PawSharePostBuild.cs on iOS builds); without it iOS terminates the app on that action.
#import <UIKit/UIKit.h>

extern UIViewController *UnityGetGLViewController(void);

extern "C" {

void PawShare_ShareImage(const char *path, const char *text)
{
    NSString *p = path != NULL ? [NSString stringWithUTF8String:path] : nil;
    NSString *t = text != NULL ? [NSString stringWithUTF8String:text] : @"";
    dispatch_async(dispatch_get_main_queue(), ^{
        NSMutableArray *items = [NSMutableArray array];
        UIImage *img = p != nil ? [UIImage imageWithContentsOfFile:p] : nil;
        if (img != nil) [items addObject:img];
        else if (p != nil) [items addObject:[NSURL fileURLWithPath:p]];
        if (t.length > 0) [items addObject:t];
        if (items.count == 0) return;

        UIViewController *root = UnityGetGLViewController();
        if (root == nil) return;
        while (root.presentedViewController != nil) root = root.presentedViewController;

        UIActivityViewController *vc = [[UIActivityViewController alloc] initWithActivityItems:items applicationActivities:nil];
        // iPad presents the sheet as a popover: anchor it to the middle of the game view
        UIPopoverPresentationController *pop = vc.popoverPresentationController;
        if (pop != nil)
        {
            pop.sourceView = root.view;
            pop.sourceRect = CGRectMake(CGRectGetMidX(root.view.bounds), CGRectGetMidY(root.view.bounds), 1, 1);
            pop.permittedArrowDirections = 0;
        }
        [root presentViewController:vc animated:YES completion:nil];
    });
}

}
