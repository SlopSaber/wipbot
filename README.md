# Wipbot for Beat Saber streamers

Channel moderation must allow links for viewers to request WIPs by URL. Request codes also work.

## Twitch commands

- `!wip` shows help.
- `!wip e5a5bb` requests a code from [wipbot.com](https://wipbot.com). Codes starting with `8` or `9` use Hawk services.
- `!wip https://cdn.discordapp.com/attachments/9106712553161/9165053429928/Example_Wip.zip` requests a Discord ZIP.
- `!wip https://drive.google.com/file/d/1rcs9V_aq1kBjNhAQUdFz7f7kvWR/view?usp=sharing` requests a Google Drive ZIP.
- `!wip oops` removes the user's latest queued request.

Press the WIP button in the song selection title bar to download the next request. Press it again to cancel the active download.

# Installation

## Prerequisites

This branch targets Beat Saber 1.45.1. Install these dependencies first:

* Have your Beat Saber modded (latest depends on BSIPA `^4.0.5`).
[BeatMods](https://beatmods.com/mods/1), [GitHub](https://github.com/nike4613/BeatSaber-IPA-Reloaded)
* Have SongCore (latest depends on `^3.0.0`) installed.
[BeatMods](https://beatmods.com/mods/58), [GitHub](https://github.com/Kylemc1413/SongCore)
* Have BeatSaberMarkupLanguage (latest depends on `^1.8.0`) installed.
[BeatMods](https://beatmods.com/mods/96), [GitHub](https://github.com/monkeymanboy/BeatSaberMarkupLanguage)
* Have SiraUtil (`^3.1.6`) installed.
* Have Beat Saber Plus with ChatPlexSDK_BS for Twitch chat.
[GitHub](https://github.com/hardcpp/BeatSaberPlus)

## Installing the wipbot

1. Obtain a `wipbot.dll` built for Beat Saber 1.45.1.
2. Put it in the game's `Plugins` folder.

