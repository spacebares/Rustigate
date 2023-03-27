# (! WORK IN PROGRESS, NEEDS TESTING, NO RELEASES YET !)

 ![](https://img.shields.io/github/release/Rustigate/editor.md.svg) 
## Why Rustigate?
Rustigate **automatically** starts [demo-recording](https://wiki.facepunch.com/rust/Demos "demo-recording") players who attack other players, keeping track of who this player has attacked and the location of the demo files. This builds an archive of demofootage for later review.

Whenever this player is reported, the demofiles related to their attacks on the player sending in the report is sent to a [Discord Webhook](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks "Discord Webhook"). 
*Only victims or their teammates of this event will trigger a Discord message. This saves on bogus reports from unrelated players, eg. mass reports from calling them out in chat.*

Now instead of spending hours spectating a player to figure out if they are hacking, **you can just review the exact demo-footage that caused this player to be reported**. No more do you log into a server to spectate a hacker whos just afk in their base. Now you can bear witness in the demo, them wiping out an entire team in pitch black.


## Features
- In-game browser listing all recorded demo events on the server. (/showevents)
- Automatic demo recording of players when they attack others.
- Automatic pruning of old events to limit diskspace usage.
- Discord *(Webhook)* report with zipped demofiles as attachment(s) when a player is reported.
![Discord_AurOWJ62Si](https://user-images.githubusercontent.com/57186372/224603776-077521e4-549b-486a-a479-69fc0f0312f0.png)

#### Planned Features
- Search functions for the in-game Events browser
- Transition to an always-On demo recording system
  - Events would record timestamps of player actions, shot playerName @ 1:33, looted stash @ 1:55, teamed with playerName @ 2:03 etc.

#### Limitations:
- Most of the demos are going to start off with a player already getting shot at. Some of these kinds of demos may not reveal much. However against say a team of 2, since the demo has already started recording for the 1st dead player, it will show much more information of what happened to the 2nd player.
- Rust demos are written to disk once they complete, this means **Disk performance is very important if there are a large amount of active players fighting in your server**.

- This plugin is incompatible with other plugins that record demos of players. Rust only allows a single active recording per player. *(To clarify, 300 players fighting = 300 demos being recorded at once, Two recording plugins would be 600 demos at once, which is not possible with the rust demo system)*

# Requirements:
- 2GB diskspace for demofiles *(Configurable)*

# Installation:
1. Extract zip to your rust server's main directory, Or -  
  1a. Copy Rustigate.cs to your servers */oxide/plugins/* directory.  
  1b. Copy Oxide.Rustigate.dll to */RustDedicated_Data/Managed* directory.  
3. Run the server once to generate config file. *(If already running, it should just autoload)*
4. Open the newly created config file that is */oxide/config/Rustigate.json*
5. Paste in your [Discord Webhook](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks "Discord Webhook") URL inside the quotes.
6. run `oxide.reload Rustigate` or just Restart the server.

# Configuration
*Default settings should be ok*

**MinEventSeconds and MaxEventSeconds:**
Once a player attacks another player, a PlayerEvent is created and a demo starts recording the attacker's POV. The event ends after `MinEventSeconds`, however if the player keeps attacking players they extend the event for another `MinEventSeconds`. If the event takes too long, after `MaxEventSeconds` the event is completed. 
If `MinEventSeconds` is too small, you can miss alot of information after a player is attacked. And if `MaxEventSeconds` is too large then demofiles may contain alot of useless information like them running back to base and afking.

**MaxDemoFolderSizeMB**: is used to limit the size of the demo folder. If over this limit, old demos are deleted to maintain a folder size below this.

**DiscordServerBoostTier**: Controls the maximum size of zipped files sent to discord. Discord offers boost teirs 0-3 which is 8/8/50/100MiB file attachment sizes.  
 - If a report contains a collection of demos that is too large for discord, they will be split into multiple smaller zips for upload automatically.  
   *Consider boosting your discord server if there is too many file attachments cluttering your view.*  
 - If a single demo file (while zipped), is larger then what your discord server supports, the upload will fail.  
   *Consider adjusting MaxEventSeconds to reduce file size or boost your discord server.*  
 - If the wrong Tier is chosen, or the server's current tier expires, all uploaded file attachments for the next report will fail. The plugin will reset back to DiscordServerBoostTier 0 to prevent future errors. When this happens an error message will be sent to discord. 
   
 **UploadDemosToDiscord**: if set to `false`, prevents demo files from being uploaded to discord. Although this occurs on a seperate thread, disabling this behavior can improve server performance.

# Usage
1. Check out the discord message from a report
   - Or use chat command `/showevents`, pick an event and hit _Send to Discord_
2. Download the file(s) in the discord message to your computer.  
   - Or access the server's File system to grab these files
3. **Disconnect from the server or demo playback wont work.**
4. Click on the demo button in your Rust Game's main menu.
[![demobutton](https://i.imgur.com/dF3cknZ.png "demobutton")](https://i.imgur.com/dF3cknZ.png "demobutton")
5. Click the button to open up the demos folder:
[![demosfolder](https://i.imgur.com/hV6siWg.png "demosfolder")](https://i.imgur.com/hV6siWg.png "demosfolder")
6. Extract the demos you downloaded into the folder that the button opens
7. Click on the refresh button  
[![refreshbutton](https://i.imgur.com/WEmYfQz.png "refreshbutton")](https://i.imgur.com/WEmYfQz.png "refreshbutton")
8. Sort by date so the most recent demos are up top
[![recentbutton](https://i.imgur.com/l5fmvLG.png "recentbutton")](https://i.imgur.com/l5fmvLG.png "recentbutton")
9. Pick a demo and hit play
10. Make sure you type in `disconnect` in console if you want to watch a different demo, current bug in demo system at the moment...

# Contributing
- make code, make pull request, report bugs. If you want to steal or remake plugin somewhere else thats fine, we need to arm our admins with strong tools to defeat all hacker. Which means **this plugin will remain free forever.**
