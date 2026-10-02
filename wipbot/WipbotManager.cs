using HMUI;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraUtil.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities.Async;
using UnityEngine;
using wipbot.Interfaces;
using wipbot.Models;
using wipbot.UI;
using wipbot.Utils;
using Zenject;

namespace wipbot
{
    internal class WipbotManager : IInitializable, IDisposable
    {
        [Inject] private WipbotButtonController WipbotButtonController { get; set; }
        [Inject] private WBConfig Config { get; set; }
        [Inject] private SiraLog Logger { get; set; }
        [Inject] private IChatIntegration ChatIntegration { get; set; }

        private readonly ExtendedQueue<QueueItem> WipQueue = new ExtendedQueue<QueueItem>();
        private readonly object DownloadLock = new object();
        private CancellationTokenSource DownloadCancellation;
        private volatile bool IsDisposed;
        private static Task<MigrationResult> MigrationTask;
        private static bool MigrationNeedsRefresh;
        private bool WaitingForMigration;
        private bool WaitingForMigrationRefresh;

        public void Initialize()
        {
            WipbotButtonController.OnWipButtonPressed += OnWipButtonPressed;
            ChatIntegration.OnMessageReceived += OnMessageReceived;
            Application.quitting += Application_quitting;
            RenameOldSongFolders();
        }

        public void Dispose()
        {
            IsDisposed = true;
            WipbotButtonController.OnWipButtonPressed -= OnWipButtonPressed;
            ChatIntegration.OnMessageReceived -= OnMessageReceived;
            Application.quitting -= Application_quitting;
            SongCore.Loader.SongsLoadedEvent -= OnSongsReadyForMigration;
            SongCore.Loader.SongsLoadedEvent -= OnSongsReadyForMigrationRefresh;
            Application_quitting();
        }

        public void OnMessageReceived(ChatMessage ChatMessage)
        {
            if (string.IsNullOrEmpty(ChatMessage.Content) ||
                !ChatMessage.Content.StartsWith(Config.CommandRequestWip, StringComparison.OrdinalIgnoreCase))
                return;

            string[] msgSplit = ChatMessage.Content.Split(' ');
            if (msgSplit[0].StartsWith(Config.CommandRequestWip, StringComparison.OrdinalIgnoreCase))
            {
                int requestLimit = 
                    ChatMessage.IsBroadcaster ? 99 :
                    ChatMessage.IsModerator ? Config.QueueLimits.Moderator :
                    ChatMessage.IsVip ? Config.QueueLimits.Vip :
                    ChatMessage.IsSubscriber ? Config.QueueLimits.Subscriber :
                    Config.QueueLimits.User;
                int requestCount = WipQueue.Count(item => item.UserName == ChatMessage.UserName);
                if (msgSplit.Length > 1 && msgSplit[1].Equals(Config.KeywordUndoRequest, StringComparison.OrdinalIgnoreCase))
                {
                    WipQueue.Remove(WipQueue.Where(x => x.UserName == ChatMessage.UserName).FirstOrDefault());
                }
                else if (requestLimit == 0)
                {
                    ChatIntegration.SendChatMessage(Config.ErrorMessageNoPermission);
                }
                else if (requestCount >= requestLimit)
                {
                    ChatIntegration.SendChatMessage(Config.ErrorMessageUserMaxRequests);
                }
                else if (WipQueue.Count >= Config.QueueSize)
                {
                    ChatIntegration.SendChatMessage(Config.ErrorMessageQueueFull);
                }
                else if (msgSplit.Length > 1 && msgSplit[1] == "***")
                {
                    ChatIntegration.SendChatMessage(Config.ErrorMessageLinkBlocked);
                }
                else if (msgSplit.Length != 2 || (msgSplit[1].All(Config.RequestCodeCharacterWhitelist.Contains) == false && Config.UrlWhitelist.Any(msgSplit[1].Contains) == false))
                {
                    ChatIntegration.SendChatMessage(Config.MessageInvalidRequest);
                }
                else
                {
                    string wipUrl = msgSplit[1];

                    if (msgSplit[1].IndexOf(".") == -1)
                    {
                        wipUrl = Config.RequestCodeDownloadUrl.Replace("%s", msgSplit[1]);
                        var prefixes = Config.RequestCodePrefixDownloadUrlPairs;
                        for (int i = 0; prefixes != null && i + 1 < prefixes.Count; i += 2)
                        {
                            if (string.IsNullOrEmpty(prefixes[i]) || string.IsNullOrEmpty(prefixes[i + 1]) ||
                                !msgSplit[1].StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) continue;
                            wipUrl = prefixes[i + 1].Replace("%s", msgSplit[1]);
                            break;
                        }
                    }
                    for (int i = 0; i + 1 < Config.UrlFindReplace.Count; i += 2)
                        wipUrl = wipUrl.Replace(Config.UrlFindReplace[i], Config.UrlFindReplace[i + 1]);
                    WipQueue.Enqueue(new QueueItem() { UserName = ChatMessage.UserName, DownloadUrl = wipUrl });
                    ChatIntegration.SendChatMessage(Config.MessageWipRequested);
                    WipbotButtonController.UpdateButtonState(WipQueue.ToArray());
                }
            }
        }

        private void OnWipButtonPressed()
        {
            QueueItem item;
            CancellationTokenSource cancellation;
            lock (DownloadLock)
            {
                if (DownloadCancellation != null)
                {
                    DownloadCancellation.Cancel();
                    return;
                }

                if (WipQueue.Count == 0)
                    return;

                item = WipQueue.Dequeue();
                cancellation = new CancellationTokenSource();
                DownloadCancellation = cancellation;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await DownloadAndExtractZipAsync(item.DownloadUrl, "UserData\\wipbot", Path.Combine(UnityGame.InstallPath, Config.WipFolder), cancellation.Token);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
                finally
                {
                    lock (DownloadLock)
                    {
                        if (ReferenceEquals(DownloadCancellation, cancellation))
                            DownloadCancellation = null;
                    }
                    cancellation.Dispose();
                    if (!IsDisposed)
                        WipbotButtonController.UpdateButtonState(WipQueue.ToArray());
                }
            });
        }

        private void Application_quitting()
        {
            IsDisposed = true;
            SongCore.Loader.SongsLoadedEvent -= OnSongsReadyForMigration;
            SongCore.Loader.SongsLoadedEvent -= OnSongsReadyForMigrationRefresh;
            lock (DownloadLock)
                DownloadCancellation?.Cancel();
        }

        private async Task DownloadAndExtractZipAsync(string url, string downloadFolder, string extractFolder, CancellationToken token)
        {
            string tempFolderName = "wipbot_" + Guid.NewGuid().ToString("N");
            string zipPath = Path.Combine(downloadFolder, tempFolderName + ".zip");
            try
            {
                token.ThrowIfCancellationRequested();
                WipbotButtonController.WipButtonText = "skip (0%)";
                ChatIntegration.SendChatMessage(Config.MessageDownloadStarted);
                if (!Directory.Exists(downloadFolder)) Directory.CreateDirectory(downloadFolder);
                token.ThrowIfCancellationRequested();
                using (var webClient = new WebClient())
                {
                    int lastProgress = -1;
                    webClient.DownloadProgressChanged += (s, e) =>
                    {
                        if (e.ProgressPercentage == lastProgress || token.IsCancellationRequested) return;
                        lastProgress = e.ProgressPercentage;
                        WipbotButtonController.WipButtonText = "skip (" + e.ProgressPercentage + "%)";
                    };
                    webClient.Headers.Add(HttpRequestHeader.UserAgent, "Beat Saber wipbot v1.14.0");
                    using (token.Register(webClient.CancelAsync))
                        await webClient.DownloadFileTaskAsync(new Uri(url), zipPath);
                }
                token.ThrowIfCancellationRequested();

                if (Directory.Exists(Path.Combine(extractFolder, tempFolderName))) Directory.Delete(Path.Combine(extractFolder, tempFolderName), true);
                Directory.CreateDirectory(Path.Combine(extractFolder, tempFolderName));

                try
                {
                    using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                    {
                        if (archive.Entries.Count > Config.ZipMaxEntries)
                        {
                            ChatIntegration.SendChatMessage(Config.ErrorMessageTooManyEntries.Replace("%i", "" + Config.ZipMaxEntries));
                            return;
                        }

                        // We can do this here because we dont need to extract the zip to check this
                        if (!archive.Entries.Any(entry => Path.GetFileName(entry.FullName) == "Info.dat" ))
                        {
                            ChatIntegration.SendChatMessage(Config.ErrorMessageMissingInfoDat);
                            return;
                        }

                        // If an entry is a folder, dont extract it, could be a zip bomb
                        if (archive.Entries.Any(entry => entry.FullName.EndsWith("/")))
                        {
                            ChatIntegration.SendChatMessage(Config.ErrorMessageZipContainsSubfolders);
                            return;
                        }

                        if (archive.Entries.Sum(entry => entry.Length) > (Config.ZipMaxUncompressedSizeMB * 1000000))
                        {
                            ChatIntegration.SendChatMessage(Config.ErrorMessageMaxLength.Replace("%i", "" + Config.ZipMaxUncompressedSizeMB));
                            return;
                        }

                        if (archive.Entries.All(entry => Config.FileExtensionWhitelist.Any(allowed =>
                            string.Equals(allowed, Path.GetExtension(entry.FullName).TrimStart('.'), StringComparison.OrdinalIgnoreCase))))
                        {
                            archive.ExtractToDirectory(Path.Combine(extractFolder, tempFolderName));
                            token.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            int badFileTypesFound = 0;

                            foreach (ZipArchiveEntry entry in archive.Entries)
                            {
                                token.ThrowIfCancellationRequested();
                                if (Config.FileExtensionWhitelist.Contains(Path.GetExtension(entry.FullName).Remove(0, 1))) entry.ExtractToFile(Path.Combine(extractFolder, tempFolderName, entry.FullName));
                                else badFileTypesFound++;
                            }

                            if (badFileTypesFound > 0)
                                ChatIntegration.SendChatMessage(Config.ErrorMessageBadExtension.Replace("%i", "" + badFileTypesFound));
                        }
                    }

                    // Rename the folder to the song name and date
                    var songDat = JsonConvert.DeserializeObject<InfoDat>(File.ReadAllText(Path.Combine(extractFolder, tempFolderName, "Info.dat")));
                    var wipFolderPath = Path.Combine(extractFolder, GetFolderName(songDat, DateTimeOffset.Now));
                    Logger.Info($"Renaming {Path.Combine(extractFolder, tempFolderName)} to {wipFolderPath}");
                    Directory.Move(Path.Combine(extractFolder, tempFolderName), wipFolderPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    ChatIntegration.SendChatMessage(Config.ErrorMessageExtractionFailed);
                    Logger.Error(e);
                    return;
                }
                token.ThrowIfCancellationRequested();
                SongCore.Loader.Instance.RefreshSongs(false);

                ChatIntegration.SendChatMessage(Config.MessageDownloadSuccess);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                ChatIntegration.SendChatMessage(Config.MessageDownloadCancelled);
            }
            catch (Exception e)
            {
                if (e is WebException)
                {
                    if (IsRequestCodeUrl(url) && IsNotFound((WebException)e))
                    {
                        ChatIntegration.SendChatMessage(Config.ErrorMessageRequestCodeNotFound);
                        return;
                    }

                    ChatIntegration.SendChatMessage(Config.ErrorMessageDownloadFailed);
                }
                else
                    ChatIntegration.SendChatMessage(Config.ErrorMessageOther.Replace("%s", e.Message));
                Logger.Error(e);
            }
            finally
            {
                try
                {
                    File.Delete(zipPath);
                    var temporarySongPath = Path.Combine(extractFolder, tempFolderName);
                    if (Directory.Exists(temporarySongPath)) Directory.Delete(temporarySongPath, true);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }

        private bool IsRequestCodeUrl(string url)
        {
            string[] requestCodeUrlParts = Config.RequestCodeDownloadUrl.Split(new[] { "%s" }, StringSplitOptions.None);
            return requestCodeUrlParts.Length == 2
                && url.StartsWith(requestCodeUrlParts[0])
                && url.EndsWith(requestCodeUrlParts[1]);
        }

        private static bool IsNotFound(WebException exception)
        {
            return exception.Response is HttpWebResponse response
                && response.StatusCode == HttpStatusCode.NotFound;
        }

        private static string GetFolderName(InfoDat songDat, DateTimeOffset dateTime, CultureInfo culture = null)
        {
            var sb = new StringBuilder();
            sb.Append("wipbot_(");
            if (!string.IsNullOrEmpty(songDat.SongName)) sb.Append(songDat.SongName).Append(" - ");
            if (!string.IsNullOrEmpty(songDat.SongSubName)) sb.Append(songDat.SongSubName).Append(" - ");
            if (!string.IsNullOrEmpty(songDat.SongAuthorName)) sb.Append(songDat.SongAuthorName).Append(" - ");
            if (!string.IsNullOrEmpty(songDat.LevelAuthorName)) sb.Append(songDat.LevelAuthorName).Append(" - ");
            sb.Remove(sb.Length - 3, 3).Append(")_(").Append(dateTime.ToString("MMM dd, yyyy - HH:mm:ss", culture ?? CultureInfo.CurrentCulture)).Append(")");
            var newFolderName = sb.ToString();
            return SanitizePath(newFolderName);
        }

        private static string SanitizePath(string path)
        {
            string invalidChars = new string(System.IO.Path.GetInvalidFileNameChars()) + new string(System.IO.Path.GetInvalidPathChars());
            foreach (char c in invalidChars)
            {
                path = path.Replace(c.ToString(), "_");
            }
            return path;
        }

        internal void RenameOldSongFolders()
        {
            if (IsDisposed || WaitingForMigration) return;
            if (!SongCore.Loader.AreSongsLoaded || SongCore.Loader.AreSongsLoading)
            {
                WaitingForMigration = true;
                SongCore.Loader.SongsLoadedEvent += OnSongsReadyForMigration;
                return;
            }
            StartMigration();
        }

        private void OnSongsReadyForMigration(SongCore.Loader loader, ConcurrentDictionary<string, BeatmapLevel> levels)
        {
            SongCore.Loader.SongsLoadedEvent -= OnSongsReadyForMigration;
            WaitingForMigration = false;
            if (!IsDisposed) StartMigration();
        }

        private void StartMigration()
        {
            try
            {
                var request = new MigrationRequest(
                    Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Beat Saber_Data", "CustomWIPLevels")),
                    CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone()));
                MigrationTask = QueueMigration(request);
                _ = MigrationTask.ContinueWith(completed =>
                {
                    try
                    {
                        var result = completed.GetAwaiter().GetResult();
                        if (result.Error == null && result.Messages.Length > 0) MigrationNeedsRefresh = true;
                        if (IsDisposed) return;
                        foreach (var message in result.Messages) Logger.Info(message);
                        if (result.Error != null)
                        {
                            Logger.Error(result.Error);
                            return;
                        }
                        if (result.Messages.Length > 0)
                            Logger.Info($"Renamed {result.Messages.Length} old wip song folders to new schema");
                        RefreshMigratedSongs();
                    }
                    catch (Exception e) { if (!IsDisposed) Logger.Error(e); }
                }, CancellationToken.None, TaskContinuationOptions.None, UnityMainThreadTaskScheduler.Default);
            }
            catch (Exception e) { if (!IsDisposed) Logger.Error(e); }
        }

        private void RefreshMigratedSongs()
        {
            if (IsDisposed || !MigrationNeedsRefresh) return;
            if (!SongCore.Loader.AreSongsLoaded || SongCore.Loader.AreSongsLoading)
            {
                if (!WaitingForMigrationRefresh)
                {
                    WaitingForMigrationRefresh = true;
                    SongCore.Loader.SongsLoadedEvent += OnSongsReadyForMigrationRefresh;
                }
                return;
            }
            var loader = SongCore.Loader.Instance;
            if (loader == null) return;
            MigrationNeedsRefresh = false;
            loader.RefreshSongs(true);
        }

        private void OnSongsReadyForMigrationRefresh(SongCore.Loader loader, ConcurrentDictionary<string, BeatmapLevel> levels)
        {
            SongCore.Loader.SongsLoadedEvent -= OnSongsReadyForMigrationRefresh;
            WaitingForMigrationRefresh = false;
            RefreshMigratedSongs();
        }

        private sealed class MigrationRequest
        {
            internal readonly string Directory;
            internal readonly CultureInfo Culture;
            internal MigrationRequest(string directory, CultureInfo culture) { Directory = directory; Culture = culture; }
        }

        private static Task<MigrationResult> QueueMigration(MigrationRequest request)
        {
            var previous = MigrationTask;
            return previous == null
                ? Task.Factory.StartNew(MigrateFolders, request, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
                : previous.ContinueWith(completed =>
                {
                    if (completed.IsFaulted) _ = completed.Exception;
                    return MigrateFolders(request);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private sealed class MigrationResult
        {
            internal readonly string[] Messages;
            internal readonly Exception Error;
            internal MigrationResult(string[] messages, Exception error) { Messages = messages; Error = error; }
        }

        private static MigrationResult MigrateFolders(object state)
        {
            var request = (MigrationRequest)state;
            var messages = new List<string>();
            try
            {
                if (Directory.Exists(request.Directory))
                    foreach (var folder in Directory.GetDirectories(request.Directory))
                    {
                        var name = Path.GetFileName(folder);
                        if (!name.StartsWith("wipbot_") || name.StartsWith("wipbot_(")) continue;
                        InfoDat infoDat;
                        using (var text = new StringReader(File.ReadAllText(Path.Combine(folder, "info.dat"))))
                        using (var json = new JsonTextReader(text))
                            infoDat = JsonSerializer.Create(new JsonSerializerSettings()).Deserialize<InfoDat>(json);
                        var target = Path.Combine(request.Directory, GetFolderName(infoDat,
                            DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(name.Remove(0, 7), 16)), request.Culture));
                        Directory.Move(folder, target);
                        messages.Add($"Renamed {name} to {Path.GetFileName(target)}");
                    }
                return new MigrationResult(messages.ToArray(), null);
            }
            catch (Exception e) { return new MigrationResult(messages.ToArray(), e); }
        }
    }
}
