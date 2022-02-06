using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Media.Capture;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;
using Windows.Devices;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Input;
using Windows.UI.Core;

namespace VirtualMonitor
{
    public sealed partial class MainPage : Page
    {
        const string DEFAULT_STATUS = "Please connect to display or listen.";

        private AudioGraph audioGraph;
        private AudioDeviceInputNode inputNode;
        private AudioDeviceOutputNode outputNode;
        private MediaCapture mediaCapture;
        private bool shouldBeConnected;
        private bool isCaptureInitialized;
        private string selectedAudioDevice;
        private string selectedVideoDevice;
        private Dictionary<string, DeviceInformation> audioDevices;
        private Dictionary<string, DeviceInformation> videoDevices;
        private DispatcherTimer timer;
        private CoreCursor cursor;

        public MainPage()
        {
            this.InitializeComponent();
            ResizeWindowTo(1280, 720);

            var view = ApplicationView.GetForCurrentView();
            Window.Current.SizeChanged += Current_SizeChanged;
            Window.Current.CoreWindow.KeyUp += CoreWindow_KeyUp;
            Window.Current.CoreWindow.Activated += CoreWindow_Activated;
            PopulateAVOptions();
            elSelectAudio.SelectionChanged += ElSelectAudio_SelectionChanged;
            elSelectVideo.SelectionChanged += ElSelectVideo_SelectionChanged;
            elTitle.TextChanged += ElTitle_TextChanged;

            cursor = Window.Current.CoreWindow.PointerCursor;
            timer = new DispatcherTimer();
            timer.Interval = new TimeSpan(0, 0, 2);
            timer.Tick += Timer_Tick;

            SetStatus(DEFAULT_STATUS);
        }

        private void HideCursor()
        {
            timer.Stop();
            Window.Current.CoreWindow.PointerCursor = null;
        }

        private void ShowCursor()
        {
            timer.Stop();
            Window.Current.CoreWindow.PointerCursor = cursor;
        }

        private void ExitFullScreen()
        {
            ApplicationView.GetForCurrentView().ExitFullScreenMode();
            toggleFullScreen.Content = "Enter Full Screen";
        }

        private void ToggleFullScreen()
        {
            var view = ApplicationView.GetForCurrentView();
            if (view.IsFullScreenMode)
            {
                ExitFullScreen();
            } else
            {
                if (view.TryEnterFullScreenMode()) toggleFullScreen.Content = "Exit Full Screen";
            }
        }

        private async void PopulateAVOptions()
        {
            // Get the selected input audio & connect it to the output
            elSelectAudio.Items.Clear();
            elSelectVideo.Items.Clear();
            audioDevices = new Dictionary<string, DeviceInformation>();
            videoDevices = new Dictionary<string, DeviceInformation>();

            // Get audio devices
            int i = 0;
            int index = 0;
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(Windows.Media.Devices.MediaDevice.GetAudioCaptureSelector());
            foreach (DeviceInformation device in devices)
            {
                if (device.Name.Contains("Cam Link"))
                {
                    index = i;
                }
                elSelectAudio.Items.Add(device.Name);
                audioDevices.Add(device.Name, device);
                i++;
            }
            elSelectAudio.SelectedIndex = index;

            // Get video devices
            i = 0;
            index = 0;
            devices = await DeviceInformation.FindAllAsync(Windows.Media.Devices.MediaDevice.GetVideoCaptureSelector());
            foreach (DeviceInformation device in devices)
            {
                if (device.Name.Contains("Cam Link"))
                {
                    index = i;
                }
                elSelectVideo.Items.Add(device.Name);
                videoDevices.Add(device.Name, device);
                i++;
            }
            elSelectVideo.SelectedIndex = index;
        }

        private async void ConnectAudio()
        {
            if (selectedAudioDevice == null || selectedAudioDevice == "") return;

            // Initiate the audio graph for usage.
            AudioGraphSettings settings = new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Media);
            CreateAudioGraphResult resultCreate = await AudioGraph.CreateAsync(settings);
            if (resultCreate.Status != AudioGraphCreationStatus.Success)
            {
                SetStatus("Failed to init audioGraph: " + resultCreate.ToString());
                return;
            }
            audioGraph = resultCreate.Graph;

            // Create the output node for audio to route to. Right now it's whatever is the default that is selected by Windows. We cannot seem to custom it.
            CreateAudioDeviceOutputNodeResult resultOutput = await audioGraph.CreateDeviceOutputNodeAsync();
            outputNode = resultOutput.DeviceOutputNode;

            // Get the selected input audio & connect it to the output
            CreateAudioDeviceInputNodeResult resultInput = await audioGraph.CreateDeviceInputNodeAsync(MediaCategory.Media, audioGraph.EncodingProperties, audioDevices[selectedAudioDevice]);
            if (resultInput.Status != AudioDeviceNodeCreationStatus.Success)
            {
                SetStatus("Failed to create input node: " + resultInput.ToString());
                return;
            }
            inputNode = resultInput.DeviceInputNode;
            inputNode.AddOutgoingConnection(outputNode);

            // Start it!
            audioGraph.Start();
        }

        private void DisconnectAudio()
        {
            if (audioGraph != null) audioGraph.Stop();
            if (inputNode != null) inputNode.Dispose();
            if (outputNode != null) outputNode.Dispose();
            if (audioGraph != null) audioGraph.Dispose();
            outputNode = null;
            inputNode = null;
            audioGraph = null;
        }

        private async void ConnectVideo()
        {
            if (selectedVideoDevice == null || selectedVideoDevice == "") return;
            try
            {
                SetStatus("Initializing camera to capture audio and video...");
                mediaCapture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
                settings.MediaCategory = MediaCategory.Media;
                settings.AudioProcessing = Windows.Media.AudioProcessing.Default;
                settings.VideoDeviceId = videoDevices[selectedVideoDevice].Id;
                if (selectedAudioDevice != null && selectedAudioDevice != "")
                {
                    // I'm setting this here, but it really does not matter until we can figure out how to get an audio node from the mediaCapture object.
                    settings.AudioDeviceId = audioDevices[selectedAudioDevice].Id;
                }
                await mediaCapture.InitializeAsync(settings);

                // Set callbacks for failure and recording limit exceeded
                SetStatus("Device successfully initialized for video recording!");
                mediaCapture.Failed += new MediaCaptureFailedEventHandler(mediaCapture_Failed);

                // Start Preview
                previewElement.Source = mediaCapture;
                await mediaCapture.StartPreviewAsync();
                isCaptureInitialized = true;
                SetStatus("Camera preview succeeded");
            }
            catch (Exception ex)
            {
                SetStatus("Unable to initialize camera for audio/video mode: " + ex.Message);
                isCaptureInitialized = false;
            }
        }

        private async void DisconnectVideo()
        {
            if (mediaCapture == null) return;
            if (isCaptureInitialized)
            {
                try
                {
                    await mediaCapture.StopPreviewAsync();
                } catch (Exception)
                {
                    // Do nothing...
                }
                isCaptureInitialized = false;
            }
            mediaCapture.Dispose();
            mediaCapture = null;
        }

        private bool IsConnected() {
            return mediaCapture != null;
        }

        private void Disconnect()
        {
            DisconnectAudio();
            DisconnectVideo();
            toggleConnect.Content = "Connect";
            elSelectAudio.IsEnabled = true;
            elSelectVideo.IsEnabled = true;
            ShowCursor();
            ShowOptions();
            SetStatus(DEFAULT_STATUS);
        }

        private void Connect()
        {
            SetStatus("Connecting...");
            Debug.WriteLine(selectedVideoDevice);
            Debug.WriteLine(videoDevices[selectedVideoDevice]);
            Debug.WriteLine(videoDevices[selectedVideoDevice].Id);
            ConnectVideo();
            ConnectAudio();
            toggleConnect.Content = "Disconnect";
            elSelectAudio.IsEnabled = false;
            elSelectVideo.IsEnabled = false;
            HideCursor();
            HideOptions();
        }

        private void ToggleConnection()
        {
            if (IsConnected())
            {
                Disconnect();
                shouldBeConnected = false;
            }
            else
            {
                Connect();
                shouldBeConnected = true;
            }
        }

        private void SetStatus(string status)
        {
            Debug.WriteLine(status);
            SetStatusDisplay(status);
        }

        private void ResizeWindowTo(int width, int height)
        {
            ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size(width, height));
        }

        private async void mediaCapture_Failed(MediaCapture currentCaptureObject, MediaCaptureFailedEventArgs currentFailure)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (currentFailure != null) SetStatus(currentFailure.Message);
            });
        }

        private void StackPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ShowOptions();
        }

        private void StackPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!IsConnected()) return;
            HideOptions();
        }

        private void ShowOptions()
        {
            optionsElement.Opacity = 1;
        }

        private void HideOptions()
        {
            optionsElement.Opacity = 0;
        }

        private void SetStatusDisplay(string message)
        {
            elStatusText.Text = message;
        }

        private void Resize1080p()
        {
            ResizeWindowTo(1920, 1080);
        }

        private void Resize720p()
        {
            ResizeWindowTo(1280, 720);
        }

        private void toggleFullScreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        private void toggleConnect_Click(object sender, RoutedEventArgs e)
        {
            ToggleConnection();
        }

        private void setSize1080p_Click(object sender, RoutedEventArgs e)
        {
            Resize1080p();
        }

        private void setSize720p_Click(object sender, RoutedEventArgs e)
        {
            Resize720p();
        }

        private void ElSelectVideo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedVideoDevice = e.AddedItems[0].ToString();
            Debug.WriteLine(selectedVideoDevice);
        }

        private void ElSelectAudio_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedAudioDevice = e.AddedItems[0].ToString();
            Debug.WriteLine(selectedAudioDevice);
        }

        private void CoreWindow_Activated(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.WindowActivatedEventArgs args)
        {
            Debug.WriteLine("Activated: " + Window.Current.CoreWindow.ActivationMode);
            if (!shouldBeConnected) return;
            if (Window.Current.CoreWindow.ActivationMode == Windows.UI.Core.CoreWindowActivationMode.Deactivated)
            {
                Disconnect();
                return;
            }
            Connect();
        }

        private void CoreWindow_KeyUp(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.KeyEventArgs args)
        {
            Debug.WriteLine("New KeyUp ", args.VirtualKey.ToString());
            switch (args.VirtualKey)
            {
                case Windows.System.VirtualKey.Escape:
                    ExitFullScreen();
                    break;
                case Windows.System.VirtualKey.F11:
                    ToggleFullScreen();
                    break;
                case Windows.System.VirtualKey.Number1:
                    Resize1080p();
                    break;
                case Windows.System.VirtualKey.Number2:
                    Resize720p();
                    break;
                case Windows.System.VirtualKey.Enter:
                    ToggleConnection();
                    break;
            }
        }

        private void Current_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            Debug.WriteLine("Size changed!  " + e.Size.ToString());
        }

        private void Timer_Tick(object sender, object e)
        {
            Debug.WriteLine("Tick");
            HideCursor();
        }

        private void RelativePanel_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ShowCursor();
            if (!IsConnected()) { return; }
            Debug.WriteLine("Moved, start timer!");
            timer.Start();
        }

        private void ElTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplicationView.GetForCurrentView().Title = elTitle.Text;
        }
    }
}
