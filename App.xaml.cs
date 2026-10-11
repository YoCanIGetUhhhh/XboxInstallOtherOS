using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace XboxVmHost
{
    sealed partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
            // Pointer mode on: the Xbox arrow (moved by the controller) is the only pointing device UWP apps get.
            this.RequiresPointerMode = ApplicationRequiresPointerMode.Auto;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }
            Window.Current.Activate();
        }
    }
}
