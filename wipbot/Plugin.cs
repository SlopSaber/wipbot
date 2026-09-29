using IPA;
using IPA.Config.Stores;
using SiraUtil.Zenject;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using wipbot.Interfaces;
using wipbot.Interop;
using wipbot.UI;
using IPALogger = IPA.Logging.Logger;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]
namespace wipbot
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    [NoEnableDisable]
    public class Plugin
    {
        [Init]
        public Plugin(IPALogger logger, IPA.Config.Config config, Zenjector zenject)
        {
            zenject.UseLogger(logger);
            var wbConfig = config.Generated<WBConfig>();
            MigrateConfig(wbConfig);
            var chat = InitializeChat(logger);

            zenject.Install(Location.App, Container =>
            {
                Container.BindInstance(wbConfig).AsSingle();
                if (chat != null) Container.BindInstance(chat).AsSingle();
            });
            zenject.Install(Location.Menu, Container =>
            {
                Container.BindInterfacesAndSelfTo<WipbotButtonController>().AsSingle().When((x) => Container.HasBinding<IChatIntegration>());
                Container.BindInterfacesAndSelfTo<WipbotManager>().AsSingle().When((x) => Container.HasBinding<IChatIntegration>());
            });
        }

        private static void MigrateConfig(WBConfig config)
        {
            const string oldRequestUrl = "http://catse.net/wips/%s.zip";
            const string requestUrl = "https://wipbot.com/wips/%s.zip";

            if (config.RequestCodeDownloadUrl == oldRequestUrl)
                config.RequestCodeDownloadUrl = requestUrl;
            if (config.MessageInvalidRequest?.Contains("http://catse.net/wip") == true)
                config.MessageInvalidRequest = config.MessageInvalidRequest.Replace("http://catse.net/wip", "https://wipbot.com");

            var prefixes = config.RequestCodePrefixDownloadUrlPairs ?? new List<string>();
            var migrated = new List<string>(prefixes);
            for (var i = 1; i < migrated.Count; i += 2)
                if (migrated[i] == oldRequestUrl)
                    migrated[i] = requestUrl;

            if (migrated.Count == 0)
                migrated.AddRange(new[] { "0", config.RequestCodeDownloadUrl });
            if (migrated.Count >= 2 && migrated[0] == "0" && migrated[1] == requestUrl && config.RequestCodeDownloadUrl != requestUrl)
                migrated[1] = config.RequestCodeDownloadUrl;
            if (migrated.Count == 2 && migrated[0] == "0")
                migrated.AddRange(new[] { "8", "https://wip.hawk.quest/upload/%s.zip", "9", "https://thnght.pro/upload/%s.zip" });
            if (!migrated.SequenceEqual(prefixes))
                config.RequestCodePrefixDownloadUrlPairs = migrated;

            var oldExtensions = new[] { "png", "jpg", "jpeg", "dat", "json", "ogg", "egg" };
            if (config.FileExtensionWhitelist?.SequenceEqual(oldExtensions) == true)
            {
                var extensions = new List<string>(config.FileExtensionWhitelist) { "wav", "vivify", "" };
                config.FileExtensionWhitelist = extensions;
            }
        }

        private IChatIntegration InitializeChat(IPALogger logger)
        {
            if (IPA.Loader.PluginManager.EnabledPlugins.Any(x => x.Id == "ChatPlexSDK_BS"))
            {
                logger.Info("Using ChatPlexSDK for chat");
                return InitChatPlexSDKInterop();
            }
            else
            {
                logger.Error("Wipbot failed to initialize chat. ChatPlexSDK (BeatSaberPlus) or CatCore have to be installed for wipbot to work");
                return null;
            }
        }

        private static IChatIntegration InitChatPlexSDKInterop()
        {
            return new ChatPlexSDKInterop();
        }

    }
}
