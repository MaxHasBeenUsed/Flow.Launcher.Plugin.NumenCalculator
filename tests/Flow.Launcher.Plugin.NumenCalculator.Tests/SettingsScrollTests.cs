using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Flow.Launcher.Plugin.NumenCalculator;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class SettingsScrollTests
{
    [Fact]
    public void MouseWheelOverSettingsReachesHostScrollViewer()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = Application.Current;
            bool ownsApplication = app is null;
            Window? window = null;
            try
            {
                if (app is null)
                    app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var host = new ScrollViewer
                {
                    Height = 320,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                var settings = new SettingsControl();
                host.Content = settings;

                window = new Window
                {
                    Width = 600,
                    Height = 320,
                    Content = host,
                    ShowActivated = false,
                };
                window.Show();
                window.UpdateLayout();

                var target = (TextBox)settings.FindName("DateFormatBox");
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                };
                target.RaiseEvent(args);
                window.UpdateLayout();

                Assert.True(
                    host.VerticalOffset > 0,
                    $"Mouse wheel was swallowed; host offset stayed {host.VerticalOffset}.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
                if (ownsApplication)
                    app?.Shutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF scroll test timed out.");
        Assert.Null(failure);
    }
}
