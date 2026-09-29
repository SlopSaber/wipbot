using HMUI;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraUtil.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        private static string GetFolderName(InfoDat songDat, DateTimeOffset dateTime)
        {
            var sb = new StringBuilder();
            sb.Append("wipbot_(");
            if (!string.IsNullOrEmpty(songDat.SongName)) sb.Append(songDat.SongName).Append(" - ");
            if (!string.IsNullOrEmpty(songDat.SongSubName)) sb.Append(songDat.SongSubName).Append(" - ");
            if (!string.IsNullOrEmpty(songDat.SongAuthorName)) sb.Append(songDat.SongAuthorName).Append(" - ");
            if (!string.IsNullOrEmpty(songDat.LevelAuthorName)) sb.Append(songDat.LevelAuthorName).Append(" - ");
            sb.Remove(sb.Length - 3, 3).Append(")_").Append($"({dateTime:MMM dd, yyyy - HH:mm:ss})");
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
            try
            {
                int renamedFolders = 0;
                var wipDirectory = Path.Combine(Environment.CurrentDirectory, "Beat Saber_Data", "CustomWIPLevels");
                // A fresh game instance has no WIP folders to migrate yet.
                if (!Directory.Exists(wipDirectory))
                    return;

                Directory.GetDirectories(wipDirectory).ToList().ForEach(wipFolder =>
                {
                    if (Path.GetFileName(wipFolder).StartsWith("wipbot_") && !Path.GetFileName(wipFolder).StartsWith("wipbot_("))
                    {
                        var infoDat = JsonConvert.DeserializeObject<InfoDat>(File.ReadAllText(Path.Combine(wipFolder, "info.dat")));
                        var newFolderPath = Path.Combine(wipDirectory, GetFolderName(infoDat, DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Path.GetFileName(wipFolder).Remove(0, 7), 16))));
                        Directory.Move(wipFolder, newFolderPath);
                        renamedFolders++;
                        Logger.Info($"Renamed {Path.GetFileName(wipFolder)} to {Path.GetFileName(newFolderPath)}");
                    }
                });

                if (renamedFolders > 0)
                {
                    Logger.Info($"Renamed {renamedFolders} old wip song folders to new schema");

                    // Trigger a full song reload if legacy named maps are renamed
                    SongCore.Loader.OnLevelPacksRefreshed += RefreshSongs;
                    void RefreshSongs()
                    {
                        SongCore.Loader.OnLevelPacksRefreshed -= RefreshSongs;
                        SongCore.Loader.Instance.RefreshSongs(true);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }
        }
    }
}
