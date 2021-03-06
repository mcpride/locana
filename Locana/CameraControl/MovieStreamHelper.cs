﻿using Kazyx.ImageStream;
using Kazyx.RemoteApi;
using Kazyx.RemoteApi.AvContent;
using Locana.DataModel;
using Locana.Utility;
using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml.Media.Imaging;

namespace Locana.CameraControl
{
    public class MovieStreamHelper
    {
        private static MovieStreamHelper instance = new MovieStreamHelper();

        public static MovieStreamHelper INSTANCE
        {
            get { return instance; }
        }

        public bool IsProcessing { get { return AvContent != null; } }

        private AvContentApiClient AvContent = null;

        private MoviePlaybackData _MoviePlaybackData = new MoviePlaybackData();
        public MoviePlaybackData MoviePlaybackData { get { return _MoviePlaybackData; } }

        private readonly StreamProcessor StreamProcessor = new StreamProcessor();

        private MovieStreamHelper()
        {
            StreamProcessor.JpegRetrieved += StreamProcessor_JpegRetrieved;
            StreamProcessor.PlaybackInfoRetrieved += StreamProcessor_PlaybackInfoRetrieved;
            StreamProcessor.Closed += StreamProcessor_Closed;
        }

        public async Task<bool> SetContentAndStart(AvContentApiClient api, PlaybackContent content, string name)
        {
            if (IsProcessing)
            {
                throw new InvalidOperationException("Already processing");
            }
            AvContent = api;

            try
            {
                var location = await api.SetStreamingContentAsync(content).ConfigureAwait(false);
                await api.StartStreamingAsync().ConfigureAwait(false);
                RunLoop(false);

                var success = await StreamProcessor.OpenConnection(new Uri(location.Url)).ConfigureAwait(false);
                if (!success)
                {
                    AvContent = null;
                }

                await SystemUtil.GetCurrentDispatcher()?.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    MoviePlaybackData.FileName = name;
                });

                return success;
            }
            catch (Exception e)
            {
                DebugUtil.Log(() => e.StackTrace);
                AvContent = null;
                return false;
            }
        }

        public async Task<bool> Start(AvContentApiClient api)
        {
            try
            {
                await api.StartStreamingAsync().ConfigureAwait(false);
            }
            catch (RemoteApiException e)
            {
                DebugUtil.Log(() => { return e.StackTrace; });
                return false;
            }
            return true;
        }

        public async Task<bool> Pause(AvContentApiClient api)
        {
            try
            {
                await api.PauseStreamingAsync().ConfigureAwait(false);
            }
            catch (RemoteApiException e)
            {
                DebugUtil.Log(() => { return e.StackTrace; });
                return false;
            }
            return true;
        }

        public async void Finish()
        {
            StreamProcessor.CloseConnection();

            await SystemUtil.GetCurrentDispatcher()?.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                MoviePlaybackData.Image = null;
            });

            if (AvContent == null)
            {
                return;
            }

            try
            {
                await AvContent.StopStreamingAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                DebugUtil.Log(() => "Failed to stop movie stream");
            }
            finally
            {
                AvContent = null;
            }
        }

        private async void RunLoop(bool polling = true)
        {
            if (AvContent != null)
            {
                try
                {
                    var status = await AvContent.RequestToNotifyStreamingStatusAsync(new LongPollingFlag { ForLongPolling = polling }).ConfigureAwait(false);
                    OnStatusChanged(status);
                    RunLoop();
                }
                catch (RemoteApiException e)
                {
                    switch (e.StatusCode)
                    {
                        case StatusCode.Timeout:
                            DebugUtil.Log(() => "RequestToNotifyStreamingStatus timeout without any event. Retry for the next event");
                            RunLoop();
                            return;
                        default:
                            DebugUtil.Log(() => "RequestToNotifyStreamingStatus finished with unexpected error: " + e.StatusCode);
                            // Finish();
                            break;
                    }
                }
            }
        }

        public event EventHandler StreamClosed;
        public event EventHandler<StreamingStatusEventArgs> StatusChanged;

        protected void OnStatusChanged(StreamingStatus status)
        {
            StatusChanged?.Invoke(this, new StreamingStatusEventArgs { Status = status });
        }

        void StreamProcessor_Closed(object sender, EventArgs e)
        {
            DebugUtil.Log(() => "StreamClosed. Finish MovieStreamHelper");
            Finish();
            StreamClosed?.Invoke(sender, e);
        }

        async void StreamProcessor_PlaybackInfoRetrieved(object sender, PlaybackInfoEventArgs e)
        {
            // DebugUtil.Log(() => "playback info: " + MoviePlaybackData.FileName + " " + e.Packet.CurrentPosition.TotalMilliseconds);
            await SystemUtil.GetCurrentDispatcher()?.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                MoviePlaybackData.CurrentPosition = e.Packet.CurrentPosition;
                MoviePlaybackData.Duration = e.Packet.Duration;
            });
        }

        private bool IsRendering = false;

        BitmapImage ImageSource = new BitmapImage()
        {
            CreateOptions = BitmapCreateOptions.None,
        };

        async void StreamProcessor_JpegRetrieved(object sender, JpegEventArgs e)
        {
            if (IsRendering) { return; }

            IsRendering = true;
            await LiveviewUtil.SetAsBitmap(e.Packet.ImageData, MoviePlaybackData);
            IsRendering = false;
        }
    }

    public class StreamingStatusEventArgs : EventArgs
    {
        public StreamingStatus Status { get; internal set; }
    }

}
