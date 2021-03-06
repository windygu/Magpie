﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Magpie.Interfaces;
using Magpie.Models;
using Magpie.ViewModels;
using Magpie.Views;

namespace Magpie.Services
{
    public class MagpieUpdater : IMagpieService
    {
        private readonly AppInfo _appInfo;
        private readonly IDebuggingInfoLogger _logger;
        private readonly IAnalyticsLogger _analyticsLogger;
        internal UpdateDecider UpdateDecider { get; set; }
        internal IRemoteContentDownloader RemoteContentDownloader { get; set; }
        public event EventHandler<SingleEventArgs<RemoteAppcast>> RemoteAppcastAvailableEvent;
        public event EventHandler<SingleEventArgs<string>> ArtifactDownloadedEvent;

        public MagpieUpdater(AppInfo appInfo, IDebuggingInfoLogger debuggingInfoLogger = null, IAnalyticsLogger analyticsLogger = null)
        {
            _appInfo = appInfo;
            _logger = debuggingInfoLogger ?? new DebuggingWindowViewModel();
            _analyticsLogger = analyticsLogger ?? new AnalyticsLogger();
            RemoteContentDownloader = new DefaultRemoteContentDownloader();
            UpdateDecider = new UpdateDecider(_logger);
        }

        public async void CheckInBackground(string appcastUrl = null, bool showDebuggingWindow = false)
        {
            await Check(appcastUrl ?? _appInfo.AppCastUrl, showDebuggingWindow).ConfigureAwait(false);
        }

        public async void ForceCheckInBackground(string appcastUrl = null, bool showDebuggingWindow = false)
        {
            await Check(appcastUrl ?? _appInfo.AppCastUrl, showDebuggingWindow, true).ConfigureAwait(false);
        }

        private async Task Check(string appcastUrl, bool showDebuggingWindow = false, bool forceCheck = false)
        {
            _logger.Log(string.Format("Starting fetching remote appcast content from address: {0}", appcastUrl));
            try
            {
                var data = await RemoteContentDownloader.DownloadStringContent(appcastUrl).ConfigureAwait(true);
                var appcast = ParseAppcast(data);
                OnRemoteAppcastAvailableEvent(new SingleEventArgs<RemoteAppcast>(appcast));
                if (UpdateDecider.ShouldUpdate(appcast, forceCheck))
                {
                    ShowUpdateWindow(appcast);
                }
                else if (forceCheck)
                {
                    ShowNoUpdatesWindow();
                }
            }
            catch (Exception ex)
            {
                _logger.Log(string.Format("Error parsing remote appcast: {0}", ex.Message));
            }
            finally
            {
                _logger.Log("Finished fetching remote appcast content");
            }
        }

        protected virtual async void ShowUpdateWindow(RemoteAppcast appcast)
        {
            var viewModel = new MainWindowViewModel(_appInfo, _logger, RemoteContentDownloader, _analyticsLogger);
            await viewModel.StartAsync(appcast).ConfigureAwait(true);
            var window = new MainWindow { ViewModel = viewModel };
            viewModel.DownloadNowCommand = new DelegateCommand(e =>
            {
                _analyticsLogger.LogDownloadNow();
                _logger.Log("Continuing with downloading the artifact");
                window.Close();
                ShowDownloadWindow(appcast);
            });
            SetOwner(window);
            window.ShowDialog();
        }

        protected virtual void ShowNoUpdatesWindow()
        {
            var window = new NoUpdatesWindow();
            SetOwner(window);
            window.ShowDialog();
        }

        private static string CreateTempPath(string url)
        {
            var uri = new Uri(url);
            var path = Path.GetTempPath();
            var fileName = string.Format(Guid.NewGuid() + Path.GetFileName(uri.LocalPath));
            return Path.Combine(path, fileName);
        }

        protected virtual void ShowDownloadWindow(RemoteAppcast appcast)
        {
            var viewModel = new DownloadWindowViewModel(_appInfo, _logger, RemoteContentDownloader);
            var artifactPath = CreateTempPath(appcast.ArtifactUrl);
            var window = new DownloadWindow { DataContext = viewModel };
            viewModel.ContinueWithInstallationCommand = new DelegateCommand(e =>
            {
                _logger.Log("Continue after downloading artifact");
                _analyticsLogger.LogContinueWithInstallation();
                OnArtifactDownloadedEvent(new SingleEventArgs<string>(artifactPath));
                window.Close();
                if (ShouldOpenArtifact(appcast, artifactPath))
                {
                    OpenArtifact(artifactPath);
                    _logger.Log("Opened artifact");
                }
            });
            SetOwner(window);
            viewModel.StartAsync(appcast, artifactPath);
            window.ShowDialog();
        }

        private bool ShouldOpenArtifact(RemoteAppcast appcast, string artifactPath)
        {
            if (string.IsNullOrEmpty(appcast.DSASignature))
            {
                _logger.Log("No DSASignature provided. Skipping signature verification");
                return true;
            }
            _logger.Log("DSASignature provided. Verifying artifact's signature");
            if (VerifyArtifact(appcast, artifactPath))
            {
                _logger.Log("Successfully verified artifact's signature");
                return true;
            }
            _logger.Log("Couldn't verify artifact's signature. The artifact will now be deleted.");
            var signatureWindowViewModel = new SignatureVerificationWindowViewModel(_appInfo, appcast);
            var signatureWindow = new SignatureVerificationWindow {DataContext = signatureWindowViewModel};
            signatureWindowViewModel.ContinueCommand = new DelegateCommand(e=> {signatureWindow.Close();});
            SetOwner(signatureWindow);
            signatureWindow.ShowDialog();
            return false;
        }

        protected virtual bool VerifyArtifact(RemoteAppcast appcast, string artifactPath)
        {
            var verifer = new SignatureVerifier(_appInfo.PublicSignatureFilename);
            return verifer.VerifyDSASignature(appcast.DSASignature, artifactPath);
        }

        protected virtual void OpenArtifact(string artifactPath)
        {
            Process.Start(artifactPath);
        }

        protected virtual void SetOwner(Window window)
        {
            if (Application.Current != null && !Application.Current.MainWindow.Equals(window))
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Owner = Application.Current.MainWindow;
            }
        }

        private RemoteAppcast ParseAppcast(string content)
        {
            _logger.Log("Started deserializing remote appcast content");
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
            {
                var settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
                var serializer = new DataContractJsonSerializer(typeof(RemoteAppcast), settings);
                var appcast = (RemoteAppcast)serializer.ReadObject(ms);

                ms.Seek(0, SeekOrigin.Begin);
                serializer = new DataContractJsonSerializer(typeof(Dictionary<string, object>), settings);
                appcast.RawDictionary = (Dictionary<string, object>)serializer.ReadObject(ms);
                _logger.Log("Finished deserializing remote appcast content");
                return appcast;
            }
        }

        protected virtual void OnRemoteAppcastAvailableEvent(SingleEventArgs<RemoteAppcast> args)
        {
            var handler = RemoteAppcastAvailableEvent;
            if (handler != null) handler(this, args);
        }

        protected virtual void OnArtifactDownloadedEvent(SingleEventArgs<string> args)
        {
            var handler = ArtifactDownloadedEvent;
            if (handler != null) handler(this, args);
        }
    }
}