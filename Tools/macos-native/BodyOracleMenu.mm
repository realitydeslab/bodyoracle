// View > Enter Full Screen (Control-Command-F) for the Body Oracle Mac player. The item uses the
// window's native macOS full screen, the same as the green window button, and its title follows
// the window however full screen was entered or left.
#import <AppKit/AppKit.h>

@interface BOMenuTarget : NSObject
@property (nonatomic, strong) NSMenuItem *item;
@end

@implementation BOMenuTarget
- (NSWindow *)playerWindow
{
    return NSApp.mainWindow ?: NSApp.keyWindow ?: NSApp.windows.firstObject;
}

- (void)toggleFullScreen:(id)sender
{
    NSWindow *window = [self playerWindow];
    window.collectionBehavior |= NSWindowCollectionBehaviorFullScreenPrimary;
    [window toggleFullScreen:sender];
}

- (void)fullScreenChanged:(NSNotification *)note
{
    BOOL full = ([(NSWindow *)note.object styleMask] & NSWindowStyleMaskFullScreen) != 0;
    self.item.title = full ? @"Exit Full Screen" : @"Enter Full Screen";
}
@end

static BOMenuTarget *target;

// Adds the menu once, after the application menu and before Window, as in other Mac apps.
extern "C" void BodyOracleMenu_Install(void)
{
    NSMenu *bar = NSApp.mainMenu;
    if (target != nil || bar == nil) return;
    target = [BOMenuTarget new];
    NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:@"Enter Full Screen" action:@selector(toggleFullScreen:) keyEquivalent:@"f"];
    item.keyEquivalentModifierMask = NSEventModifierFlagControl | NSEventModifierFlagCommand;
    item.target = target;
    target.item = item;
    NSMenu *view = [[NSMenu alloc] initWithTitle:@"View"];
    [view addItem:item];
    NSMenuItem *holder = [[NSMenuItem alloc] initWithTitle:@"View" action:NULL keyEquivalent:@""];
    holder.submenu = view;
    NSInteger window = [bar indexOfItemWithTitle:@"Window"];
    [bar insertItem:holder atIndex:window >= 0 ? window : MIN(1, bar.numberOfItems)];
    NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
    [center addObserver:target selector:@selector(fullScreenChanged:) name:NSWindowDidEnterFullScreenNotification object:nil];
    [center addObserver:target selector:@selector(fullScreenChanged:) name:NSWindowDidExitFullScreenNotification object:nil];
}

// Escape leaves full screen, as in other Mac apps; Unity receives the key, so it asks here.
extern "C" void BodyOracleMenu_ExitFullScreen(void)
{
    NSWindow *window = [target playerWindow];
    if (window != nil && (window.styleMask & NSWindowStyleMaskFullScreen) != 0) [window toggleFullScreen:nil];
}
