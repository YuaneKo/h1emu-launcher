﻿using SteamKit2;
using SteamKit2.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using System.Windows.Forms;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Threading;

namespace H1EMU_Launcher
{
    class Steam3Session
    {
        public static string twoauth { get; set; }

        public class Credentials
        {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }

            public bool IsValid
            {
                get { return LoggedOn; }
            }
        }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, byte[]> AppTickets { get; private set; }
        public Dictionary<uint, ulong> AppTokens { get; private set; }
        public Dictionary<uint, ulong> PackageTokens { get; private set; }
        public Dictionary<uint, byte[]> DepotKeys { get; private set; }
        public ConcurrentDictionary<string, TaskCompletionSource<SteamApps.CDNAuthTokenCallback>> CDNAuthTokens { get; private set; }
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; private set; }
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; private set; }
        public Dictionary<string, byte[]> AppBetaPasswords { get; private set; }

        public SteamClient steamClient;
        public SteamUser steamUser;
        SteamApps steamApps;
        SteamCloud steamCloud;
        SteamUnifiedMessages.UnifiedService<IPublishedFile> steamPublishedFile;

        CallbackManager callbacks;

        bool authenticatedUser;
        bool bConnected;
        bool bConnecting;
        bool bAborted;
        bool bExpectingDisconnectRemote;
        bool bDidDisconnect;
        bool bDidReceiveLoginKey;
        int connectionBackoff;
        int seq; // more hack fixes
        DateTime connectTime;

        // input
        SteamUser.LogOnDetails logonDetails;

        // output
        Credentials credentials;

        public static CancellationTokenSource tokenSource = new CancellationTokenSource();
        CancellationToken token;

        static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds( 30 );


        public Steam3Session( SteamUser.LogOnDetails details )
        {
            this.logonDetails = details;

            this.authenticatedUser = details.Username != null;
            this.credentials = new Credentials();
            this.bConnected = false;
            this.bConnecting = false;
            this.bAborted = false;
            this.bExpectingDisconnectRemote = false;
            this.bDidDisconnect = false;
            this.bDidReceiveLoginKey = false;
            this.seq = 0;

            this.AppTickets = new Dictionary<uint, byte[]>();
            this.AppTokens = new Dictionary<uint, ulong>();
            this.PackageTokens = new Dictionary<uint, ulong>();
            this.DepotKeys = new Dictionary<uint, byte[]>();
            this.CDNAuthTokens = new ConcurrentDictionary<string, TaskCompletionSource<SteamApps.CDNAuthTokenCallback>>();
            this.AppInfo = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            this.PackageInfo = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            this.AppBetaPasswords = new Dictionary<string, byte[]>();

            this.steamClient = new SteamClient();

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();
            this.steamCloud = this.steamClient.GetHandler<SteamCloud>();
            var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>();
            this.steamPublishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();

            this.callbacks = new CallbackManager( this.steamClient );

            this.callbacks.Subscribe<SteamClient.ConnectedCallback>( ConnectedCallback );
            this.callbacks.Subscribe<SteamClient.DisconnectedCallback>( DisconnectedCallback );
            this.callbacks.Subscribe<SteamUser.LoggedOnCallback>( LogOnCallback );
            this.callbacks.Subscribe<SteamUser.SessionTokenCallback>( SessionTokenCallback );
            this.callbacks.Subscribe<SteamApps.LicenseListCallback>( LicenseListCallback );
            this.callbacks.Subscribe<SteamUser.UpdateMachineAuthCallback>( UpdateMachineAuthCallback );
            this.callbacks.Subscribe<SteamUser.LoginKeyCallback>( LoginKeyCallback );

            Debug.WriteLine("Connecting to Steam3...");

            if ( authenticatedUser )
            {
                FileInfo fi = new FileInfo( String.Format( "{0}.sentryFile", logonDetails.Username ) );
                if (AccountSettingsStore.Instance.SentryData != null && AccountSettingsStore.Instance.SentryData.ContainsKey(logonDetails.Username))
                {
                    logonDetails.SentryFileHash = Util.SHAHash( AccountSettingsStore.Instance.SentryData[ logonDetails.Username ] );
                }
                else if ( fi.Exists && fi.Length > 0 )
                {
                    var sentryData = File.ReadAllBytes( fi.FullName );
                    logonDetails.SentryFileHash = Util.SHAHash( sentryData );
                    AccountSettingsStore.Instance.SentryData[ logonDetails.Username ] = sentryData;
                    AccountSettingsStore.Save();
                }
            }

            Connect();
        }

        public delegate bool WaitCondition();
        private object steamLock = new object();

        public bool WaitUntilCallback( Action submitter, WaitCondition waiter )
        {
            while ( !bAborted && !waiter() )
            {
                lock (steamLock)
                {
                    submitter();
                }

                int seq = this.seq;
                do
                {
                    lock (steamLock)
                    {
                        WaitForCallbacks();
                    }
                }
                while ( !bAborted && this.seq == seq && !waiter() );
            }

            return bAborted;
        }

        public Credentials WaitForCredentials()
        {
            if ( credentials.IsValid || bAborted )
                return credentials;

            WaitUntilCallback( () => { }, () => { return credentials.IsValid; } );

            return credentials;
        }

        public void RequestAppInfo( uint appId, bool bForce = false )
        {
            if ( ( AppInfo.ContainsKey( appId ) && !bForce ) || bAborted )
                return;

            bool completed = false;
            Action<SteamApps.PICSTokensCallback> cbMethodTokens = ( appTokens ) =>
            {
                completed = true;
                if ( appTokens.AppTokensDenied.Contains( appId ) )
                {
                    Debug.WriteLine(string.Format("Insufficient privileges to get access token for app {0}",appId));
                }

                foreach ( var token_dict in appTokens.AppTokens )
                {
                    this.AppTokens[ token_dict.Key ] = token_dict.Value;
                }
            };

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.PICSGetAccessTokens( new List<uint>() { appId }, new List<uint>() { } ), cbMethodTokens );
            }, () => { return completed; } );

            completed = false;
            Action<SteamApps.PICSProductInfoCallback> cbMethod = ( appInfo ) =>
            {
                completed = !appInfo.ResponsePending;

                foreach ( var app_value in appInfo.Apps )
                {
                    var app = app_value.Value;

                    Debug.WriteLine(string.Format("Got AppInfo for{0}"),app.ID);
                    AppInfo[ app.ID ] = app;
                }

                foreach ( var app in appInfo.UnknownApps )
                {
                    AppInfo[ app ] = null;
                }
            };

            SteamApps.PICSRequest request = new SteamApps.PICSRequest( appId );
            if ( AppTokens.ContainsKey( appId ) )
            {
                request.AccessToken = AppTokens[ appId ];
                request.Public = false;
            }

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.PICSGetProductInfo( new List<SteamApps.PICSRequest>() { request }, new List<SteamApps.PICSRequest>() { } ), cbMethod );
            }, () => { return completed; } );
        }

        public void RequestPackageInfo( IEnumerable<uint> packageIds )
        {
            List<uint> packages = packageIds.ToList();
            packages.RemoveAll( pid => PackageInfo.ContainsKey( pid ) );

            if ( packages.Count == 0 || bAborted )
                return;

            bool completed = false;
            Action<SteamApps.PICSProductInfoCallback> cbMethod = ( packageInfo ) =>
            {
                completed = !packageInfo.ResponsePending;

                foreach ( var package_value in packageInfo.Packages )
                {
                    var package = package_value.Value;
                    PackageInfo[ package.ID ] = package;
                }

                foreach ( var package in packageInfo.UnknownPackages )
                {
                    PackageInfo[package] = null;
                }
            };

            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach ( var package in packages )
            {
                var request = new SteamApps.PICSRequest( package );

                if ( PackageTokens.TryGetValue( package, out var token ) )
                {
                    request.AccessToken = token;
                    request.Public = false;
                }

                packageRequests.Add( request );
            }

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.PICSGetProductInfo( new List<SteamApps.PICSRequest>(), packageRequests ), cbMethod );
            }, () => { return completed; } );
        }

        public bool RequestFreeAppLicense( uint appId )
        {
            bool success = false;
            bool completed = false;
            Action<SteamApps.FreeLicenseCallback> cbMethod = ( resultInfo ) =>
            {
                completed = true;
                success = resultInfo.GrantedApps.Contains( appId );
            };

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.RequestFreeLicense( appId ), cbMethod );
            }, () => { return completed; } );

            return success;
        }

        public void RequestAppTicket( uint appId )
        {
            if ( AppTickets.ContainsKey( appId ) || bAborted )
                return;


            if ( !authenticatedUser )
            {
                AppTickets[ appId ] = null;
                return;
            }

            bool completed = false;
            Action<SteamApps.AppOwnershipTicketCallback> cbMethod = ( appTicket ) =>
            {
                completed = true;

                if ( appTicket.Result != EResult.OK )
                {
                    Debug.WriteLine(string.Format("Unable to get appticket for{0} {1}"),appTicket.AppID,appTicket.Result);
                    Abort();
                }
                else
                {
                    Debug.WriteLine($"Got appticket for {appTicket.AppID}!");
                    AppTickets[ appTicket.AppID ] = appTicket.Ticket;
                }
            };

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.GetAppOwnershipTicket( appId ), cbMethod );
            }, () => { return completed; } );
        }

        public void RequestDepotKey( uint depotId, uint appid = 0 )
        {
            if ( DepotKeys.ContainsKey( depotId ) || bAborted )
                return;

            bool completed = false;

            Action<SteamApps.DepotKeyCallback> cbMethod = ( depotKey ) =>
            {
                completed = true;

                System.Windows.Application.Current.Dispatcher.Invoke((MethodInvoker)delegate
                {
                    DownloadStatus.downStatus.downloadProgressText.Text = string.Format(LanCtrler.GetWords("Got depot key for {0} result:{1}..."), depotKey.DepotID, depotKey.Result);
                });

                Debug.WriteLine($"Got depot key for {depotKey.DepotID} result: {depotKey.Result}");

                if ( depotKey.Result != EResult.OK )
                {
                    Abort();
                    return;
                }

                DepotKeys[ depotKey.DepotID ] = depotKey.DepotKey;
            };

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.GetDepotDecryptionKey( depotId, appid ), cbMethod );
            }, () => { return completed; } );
        }

        public string ResolveCDNTopLevelHost(string host)
        {
            // SteamPipe CDN shares tokens with all hosts
            if (host.EndsWith( ".steampipe.steamcontent.com" ) )
            {
                return "steampipe.steamcontent.com";
            }
            else if (host.EndsWith(".steamcontent.com"))
            {
                return "steamcontent.com";
            }

            return host;
        }

        public void RequestCDNAuthToken( uint appid, uint depotid, string host, string cdnKey )
        {
            if ( CDNAuthTokens.ContainsKey( cdnKey ) || bAborted )
                return;

            if ( !CDNAuthTokens.TryAdd( cdnKey, new TaskCompletionSource<SteamApps.CDNAuthTokenCallback>() ) )
                return;

            bool completed = false;
            var timeoutDate = DateTime.Now.AddSeconds( 10 );
            Action<SteamApps.CDNAuthTokenCallback> cbMethod = ( cdnAuth ) =>
            {
                completed = true;

                System.Windows.Application.Current.Dispatcher.Invoke((MethodInvoker)delegate
                {
                    DownloadStatus.downStatus.downloadProgressText.Text = string.Format(LanCtrler.GetWords("Got CDN auth token for{0}result:{1}"),host, cdnAuth.Result);
                });

                Debug.WriteLine($"Got CDN auth token for {host} result: {cdnAuth.Result} (expires {cdnAuth.Expiration})");

                if ( cdnAuth.Result != EResult.OK )
                {
                    Abort();
                    return;
                }

                CDNAuthTokens[cdnKey].TrySetResult( cdnAuth );
            };

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.GetCDNAuthToken( appid, depotid, host ), cbMethod );
            }, () => { return completed || DateTime.Now >= timeoutDate; } );
        }

        public void CheckAppBetaPassword( uint appid, string password )
        {
            bool completed = false;
            Action<SteamApps.CheckAppBetaPasswordCallback> cbMethod = ( appPassword ) =>
            {
                completed = true;

                Debug.WriteLine($"Retrieved {appPassword.BetaPasswords.Count} beta keys with result: {appPassword.Result}");

                foreach ( var entry in appPassword.BetaPasswords )
                {
                    AppBetaPasswords[ entry.Key ] = entry.Value;
                }
            };

            WaitUntilCallback( () =>
            {
                callbacks.Subscribe( steamApps.CheckAppBetaPassword( appid, password ), cbMethod );
            }, () => { return completed; } );
        }

        public CPublishedFile_GetItemInfo_Response.WorkshopItemInfo GetPubfileItemInfo( uint appId, PublishedFileID pubFile )
        {
            var pubFileRequest = new CPublishedFile_GetItemInfo_Request() { app_id = appId };
            pubFileRequest.workshop_items.Add( new CPublishedFile_GetItemInfo_Request.WorkshopItem() { published_file_id = pubFile } );

            bool completed = false;
            CPublishedFile_GetItemInfo_Response.WorkshopItemInfo details = null;

            Action<SteamUnifiedMessages.ServiceMethodResponse> cbMethod = callback =>
            {
                completed = true;
                if ( callback.Result == EResult.OK )
                {
                    var response = callback.GetDeserializedResponse<CPublishedFile_GetItemInfo_Response>();
                    details = response.workshop_items.FirstOrDefault();
                }
                else
                {
                    throw new Exception( $"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC id for pubfile {pubFile}.");
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe( steamPublishedFile.SendMessage( api => api.GetItemInfo( pubFileRequest ) ), cbMethod );
            }, () => { return completed; });

            return details;
        }

        public PublishedFileDetails GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
        {
            var pubFileRequest = new CPublishedFile_GetDetails_Request() { appid = appId };
            pubFileRequest.publishedfileids.Add( pubFile );

            bool completed = false;
            PublishedFileDetails details = null;

            Action<SteamUnifiedMessages.ServiceMethodResponse> cbMethod = callback =>
            {
                completed = true;
                if (callback.Result == EResult.OK)
                {
                    var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
                    details = response.publishedfiledetails.FirstOrDefault();
                }
                else
                {
                    throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving file details for pubfile {pubFile}.");
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamPublishedFile.SendMessage(api => api.GetDetails(pubFileRequest)), cbMethod);
            }, () => { return completed; });

            return details;
        }


        public SteamCloud.UGCDetailsCallback GetUGCDetails(UGCHandle ugcHandle)
        {
            bool completed = false;
            SteamCloud.UGCDetailsCallback details = null;

            Action<SteamCloud.UGCDetailsCallback> cbMethod = callback =>
            {
                completed = true;
                if (callback.Result == EResult.OK)
                {
                    details = callback;
                }
                else if (callback.Result == EResult.FileNotFound)
                {
                    details = null;
                }
                else
                {
                    throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC details for {ugcHandle}.");
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamCloud.RequestUGCDetails(ugcHandle), cbMethod);
            }, () => { return completed; });

            return details;
        }

        void Connect()
        {
            bAborted = false;
            bConnected = false;
            bConnecting = true;
            connectionBackoff = 0;
            bExpectingDisconnectRemote = false;
            bDidDisconnect = false;
            bDidReceiveLoginKey = false;
            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort( bool sendLogOff = true )
        {
            Disconnect( sendLogOff );
        }
        public void Disconnect( bool sendLogOff = true )
        {
            if ( sendLogOff )
            {
                steamUser.LogOff();
            }

            steamClient.Disconnect();
            bConnected = false;
            bConnecting = false;
            bAborted = true;

            // flush callbacks until our disconnected event
            while ( !bDidDisconnect )
            {
                callbacks.RunWaitAllCallbacks( TimeSpan.FromMilliseconds( 100 ) );
            }
        }

        public void TryWaitForLoginKey()
        {
            if ( logonDetails.Username == null || !credentials.LoggedOn || !ContentDownloader.Config.RememberPassword ) return;

            var totalWaitPeriod = DateTime.Now.AddSeconds( 3 );

            while ( true )
            {
                DateTime now = DateTime.Now;
                if ( now >= totalWaitPeriod ) break;

                if ( bDidReceiveLoginKey ) break;

                callbacks.RunWaitAllCallbacks( TimeSpan.FromMilliseconds( 100 ) );
            }
        }

        private void WaitForCallbacks()
        {
            callbacks.RunWaitCallbacks( TimeSpan.FromSeconds( 1 ) );

            TimeSpan diff = DateTime.Now - connectTime;

            if ( diff > STEAM3_TIMEOUT && !bConnected )
            {
                Debug.WriteLine("Timeout connecting to Steam3.");
                Abort();

                return;
            }
        }

        private void ConnectedCallback( SteamClient.ConnectedCallback connected )
        {
            Debug.WriteLine("Done!");
            bConnecting = false;
            bConnected = true;
            if ( !authenticatedUser )
            {
                Debug.WriteLine("Logging anonymously into Steam3...");
                steamUser.LogOnAnonymous();
            }
            else
            {
                Debug.WriteLine($"Logging '{logonDetails.Username}' into Steam3...");
                steamUser.LogOn( logonDetails );
            }
        }

        private void DisconnectedCallback( SteamClient.DisconnectedCallback disconnected )
        {
            bDidDisconnect = true;

            if ( disconnected.UserInitiated || bExpectingDisconnectRemote )
            {
                Debug.WriteLine("Disconnected from Steam");
            }
            else if ( connectionBackoff >= 10 )
            {
                Debug.WriteLine("Could not connect to Steam after 10 tries");
                Abort( false );
            }
            else if ( !bAborted )
            {
                if ( bConnecting )
                {
                    Debug.WriteLine("Connection to Steam failed. Trying again");
                }
                else
                {
                    Debug.WriteLine("Lost connection to Steam. Reconnecting");
                }

                Thread.Sleep( 1000 * ++connectionBackoff );
                steamClient.Connect();
            }
        }

        private void LogOnCallback( SteamUser.LoggedOnCallback loggedOn )
        {
            tokenSource.Dispose();
            tokenSource = new CancellationTokenSource();
            token = tokenSource.Token;

            bool isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
            bool is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            bool isLoginKey = ContentDownloader.Config.RememberPassword && logonDetails.LoginKey != null && loggedOn.Result == EResult.InvalidPassword;

            if (isSteamGuard || is2FA || isLoginKey)
            {
                bExpectingDisconnectRemote = true;
                Abort(false);

                if (!isLoginKey)
                {
                    Debug.WriteLine("This account is protected by Steam Guard.");
                }

                if (is2FA)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke((MethodInvoker)delegate
                    {
                        Launcher.lncher.SteamFrame.Navigate(new Uri("2FA.xaml", UriKind.Relative));
                    });

                    token.WaitHandle.WaitOne();

                    logonDetails.TwoFactorCode = twoauth;
                }
                else if (isLoginKey)
                {
                    AccountSettingsStore.Instance.LoginKeys.Remove(logonDetails.Username);
                    AccountSettingsStore.Save();

                    logonDetails.LoginKey = null;

                    if (ContentDownloader.Config.SuppliedPassword != null)
                    {
                        Debug.WriteLine("Login key was expired. Connecting with supplied password.");
                        logonDetails.Password = ContentDownloader.Config.SuppliedPassword;
                    }
                    else
                    {
                        Console.Write("Login key was expired. Please enter your password: ");
                        logonDetails.Password = Util.ReadPassword();
                    }
                }
                else
                {
                    System.Windows.Application.Current.Dispatcher.Invoke((System.Windows.Forms.MethodInvoker)delegate
                    {
                        Launcher.lncher.SteamFrame.Navigate(new Uri("2FA.xaml", UriKind.Relative));
                    });

                    token.WaitHandle.WaitOne();

                    logonDetails.AuthCode = twoauth;
                }

                Debug.WriteLine("Retrying Steam3 connection...");
                Connect();

                return;
            }
            else if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                System.Windows.Application.Current.Dispatcher.Invoke((MethodInvoker)delegate
                {
                    Launcher.lncher.SteamFrame.Navigate(new Uri("Login.xaml", UriKind.Relative));
                    CustomMessageBox.Show(string.Format(LanCtrler.GetWords("Unable to login to Steam:{0}") ,loggedOn.Result));
                });

                Abort(false);

                return;
            }
            else if (loggedOn.Result != EResult.OK)
            {
                System.Windows.Application.Current.Dispatcher.Invoke((MethodInvoker)delegate
                {
                    Launcher.lncher.SteamFrame.Navigate(new Uri("Login.xaml", UriKind.Relative));
                    CustomMessageBox.Show(string.Format(LanCtrler.GetWords("Unable to login to Steam:{0}"),loggedOn.Result));
                });

                Abort();

                return;
            }

            Debug.WriteLine("Done!");

            this.seq++;
            credentials.LoggedOn = true;

            if (ContentDownloader.Config.CellID == 0)
            {
                Debug.WriteLine($"Using Steam3 suggested CellID: {loggedOn.CellID}");
                ContentDownloader.Config.CellID = (int)loggedOn.CellID;
            }
        }

        private void SessionTokenCallback( SteamUser.SessionTokenCallback sessionToken )
        {
            Debug.WriteLine("Got session token!");
            credentials.SessionToken = sessionToken.SessionToken;
        }

        private void LicenseListCallback( SteamApps.LicenseListCallback licenseList )
        {
            if ( licenseList.Result != EResult.OK )
            {
                Debug.WriteLine($"Unable to get license list: {licenseList.Result}");
                Abort();

                return;
            }

            Debug.WriteLine($"Got {licenseList.LicenseList.Count} licenses for account!");
            Licenses = licenseList.LicenseList;

            foreach ( var license in licenseList.LicenseList )
            {
                if ( license.AccessToken > 0 )
                {
                    PackageTokens.Add( license.PackageID, license.AccessToken );
                }
            }
        }

        private void UpdateMachineAuthCallback( SteamUser.UpdateMachineAuthCallback machineAuth )
        {
            byte[] hash = Util.SHAHash( machineAuth.Data );
            Debug.WriteLine($"Got Machine Auth: {machineAuth.FileName} {machineAuth.Offset} {machineAuth.BytesToWrite} {machineAuth.Data.Length} Hash: {hash}");

            AccountSettingsStore.Instance.SentryData[ logonDetails.Username ] = machineAuth.Data;
            AccountSettingsStore.Save();

            var authResponse = new SteamUser.MachineAuthDetails
            {
                BytesWritten = machineAuth.BytesToWrite,
                FileName = machineAuth.FileName,
                FileSize = machineAuth.BytesToWrite,
                Offset = machineAuth.Offset,

                SentryFileHash = hash, // should be the sha1 hash of the sentry file we just wrote

                OneTimePassword = machineAuth.OneTimePassword, // not sure on this one yet, since we've had no examples of steam using OTPs

                LastError = 0, // result from win32 GetLastError
                Result = EResult.OK, // if everything went okay, otherwise ~who knows~

                JobID = machineAuth.JobID, // so we respond to the correct server job
            };

            // send off our response
            steamUser.SendMachineAuthResponse( authResponse );
        }

        private void LoginKeyCallback( SteamUser.LoginKeyCallback loginKey )
        {
            Debug.WriteLine($"Accepted new login key for account {logonDetails.Username}");

            AccountSettingsStore.Instance.LoginKeys[ logonDetails.Username ] = loginKey.LoginKey;
            AccountSettingsStore.Save();

            steamUser.AcceptNewLoginKey( loginKey );

            bDidReceiveLoginKey = true;
        }


    }
}
