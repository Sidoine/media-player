using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MediaPlayer.AndroidTv;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Android.Content.Intent.ActionMain }, Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
public class MainActivity : MauiAppCompatActivity
{
}
