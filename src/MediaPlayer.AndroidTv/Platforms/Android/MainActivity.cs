using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace MediaPlayer.AndroidTv;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ResizeableActivity = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Android.Content.Intent.ActionMain }, Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
public class MainActivity : MauiAppCompatActivity
{
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.Action == KeyEventActions.Down)
        {
            var verticalDirection = e.KeyCode switch
            {
                Keycode.DpadUp => -1,
                Keycode.DpadDown => 1,
                _ => 0
            };
            if (verticalDirection != 0 && MainPage.Current?.HandleVerticalDpad(verticalDirection) == true) return true;

            var horizontalDirection = e.KeyCode switch
            {
                Keycode.DpadLeft => -1,
                Keycode.DpadRight => 1,
                _ => 0
            };
            if (horizontalDirection != 0 && MainPage.Current?.HandleHorizontalDpad(horizontalDirection) == true) return true;
        }
        return base.DispatchKeyEvent(e);
    }
}
