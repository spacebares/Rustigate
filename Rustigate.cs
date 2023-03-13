#define DEBUGMODE //this should always remain commented unless you are working on the plugin

using Oxide.Core.Libraries.Covalence;
using Oxide.Core;
using Oxide.Core.Database;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.Linq;
using UnityEngine;
using Oxide.Ext.Rustigate;

//todo: this plugin is going to run into disk space issues due to all the demo files:
//1. oxide currently has no way of knowing how much diskspace is free on the host
//2. even if somehow i can get the demos to be within oxides data folder, oxide has no way of deleting files
//therfor at somepoint the demofiles need to be in a YYYY/MM/DD folder structure
//this way admin has an easier time pruning old data by just using host filesystem.
//afterwards its up to the plugin during startup to recognize these files are missing and remove events from DB
//  - it seems like automatic event pruning and discord file upload can be done threw an oxide extension?

namespace Oxide.Plugins
{
    [Info("Rustigate", "https://github.com/spacebares", "0.0.2")]
    [Description("Automatic demo recording of players when they attack others, with discord notifications for related player reports and an ingame event browser.")]
    class Rustigate : CovalencePlugin
    {
        Core.SQLite.Libraries.SQLite sqlLibrary = Interface.Oxide.GetLibrary<Core.SQLite.Libraries.SQLite>();
        Connection sqlConnection;

        private Int32 NextEventID = 0;

        ///we keep these in memory to avoid having to waste cpu talking with sqlite
        ///todo: this is for sure faster, but is it neccessary? its for sure more memory intensive
        private List<PlayerEvent> PlayerEvents = new List<PlayerEvent>();
        private Dictionary<ulong, Int32> PlayerActiveEventID = new Dictionary<ulong, Int32>();

        ///again is this neccessary? sql select magic can do this as well, these are only here for caching / speedup
        //this contains all the events a player has been involved with
        private Dictionary<ulong, List<Int32>> EventPlayers = new Dictionary<ulong, List<Int32>>();

        //events that have been reported by players (F7 Report), ignored for future discord reports
        private HashSet<Int32> ReportedEvents = new HashSet<Int32>();

        //events that are waiting to complete before being reported
        private HashSet<Int32> DelayedReportEvents = new HashSet<Int32>();

#if DEBUGMODE
        private List<BasePlayer> Bots = new List<BasePlayer>();
#endif

        #region Config

        private class PluginConfig
        {
            /*events start the moment a player attacks someone, after MinEventSeconds the event is over and a demo is saved
            however each time the player attacks someone this timer is reset.*/
            public Int32 MinEventSeconds;
            /*regardless of how many times a player attacks others during an event, it will always end after MaxEventSeconds
            this prevents a malicious player from just attacking someone every <5min to delay the saving of a demo for review*/
            public Int32 MaxEventSeconds;
            //see https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks for info
            public string DiscordWebhookURL;
            //if all of our events are larger then this, then we start deleting the oldest events until we are back under
            public Int32 MaxDemoFolderSizeMB;
        }


        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                MinEventSeconds = 30,
                MaxEventSeconds = 300,
                DiscordWebhookURL = "",
                MaxDemoFolderSizeMB = 2048
            };
        }

        private PluginConfig config;

        #endregion

        #region Classes

        public class EventVictimInfo
        {
            public ulong PlayerID;
            public string PlayerName;
            public BasePlayer Player;
            public DateTime EventTime;
            public Vector3 InitialEventPosition;

            public EventVictimInfo() { }

            public EventVictimInfo(BasePlayer VictimPlayer)
            {
                PlayerID = VictimPlayer.userID;
                PlayerName = VictimPlayer.displayName;
                EventTime = DateTime.UtcNow;
                Player = VictimPlayer;
                InitialEventPosition = VictimPlayer.ServerPosition;
            }

            public EventVictimInfo(ulong playerID, string playerName, DateTime eventTime)
            {
                PlayerID = playerID;
                PlayerName = playerName;
                EventTime = eventTime;
            }
        }

        public class PlayerEvent
        {
            public Int32 EventID;

            public ulong AttackerID;
            public string AttackerName;
            public BasePlayer AttackerPlayer;
            public Vector3 AttackerInitialPosition;

            /*there can be a number of different victims within one demo recording
             * we track all of them here to make searching for related player vs player events easier */
            public Dictionary<ulong, EventVictimInfo> EventVictims = new Dictionary<ulong, EventVictimInfo>();

            public DateTime EventTime;
            public string DemoFilename;
            public Timer MinEventTimer;
            public Timer MaxEventTimer;

            public PlayerEvent() { }
            public PlayerEvent(Int32 EventID, BasePlayer AttackerPlayer, BasePlayer VictimPlayer, string DemoFilename, Timer MinEventTimer, Timer MaxEventTimer)
            {
                this.EventID = EventID;

                this.AttackerID = AttackerPlayer.userID;
                this.AttackerName = AttackerPlayer.displayName;
                this.AttackerPlayer = AttackerPlayer;
                this.AttackerInitialPosition = AttackerPlayer.ServerPosition;

                EventVictims.Add(VictimPlayer.userID, new EventVictimInfo(VictimPlayer));

                this.EventTime = DateTime.UtcNow;
                this.DemoFilename = DemoFilename;
                this.MinEventTimer = MinEventTimer;
                this.MaxEventTimer = MaxEventTimer;
            }

            public PlayerEvent(Int32 EventID, DateTime EventTime, string AttackerPlayerName, ulong AttackerPlayerID, string DemoFilename)
            {
                this.EventID = EventID;

                this.AttackerID = AttackerPlayerID;
                this.AttackerName = AttackerPlayerName;

                this.EventTime = EventTime;
                this.DemoFilename = DemoFilename;
            }

            public void RefreshEventTimer()
            {
                MinEventTimer.Reset();
            }

            public void AddEventVictim(BasePlayer VictimPlayer)
            {
                if (!EventVictims.ContainsKey(VictimPlayer.userID))
                {
                    EventVictimInfo eventVictimInfo = new EventVictimInfo(VictimPlayer);
                    EventVictims.Add(VictimPlayer.userID, eventVictimInfo);
                }

                //player is actively hitting ppl so keep refreshing timer
                RefreshEventTimer();
            }
        }
        #endregion

        #region DB

        private void InitializeDB()
        {
            sqlConnection = sqlLibrary.OpenDb("RustigateEvents.db", this, true);

            //with the demofiles on disk, we only know two things: Who the demofile belongs to and the time it took place
            //what we need to help the admin narrow down reports from players: who are the victims in the demofiles
            //this db keeps track of this so the admin can load up a specific demofile -
            //- that has the victim player(s) who sent the report in
            sqlLibrary.ExecuteNonQuery(Sql.Builder.Append(
                @"CREATE TABLE IF NOT EXISTS `Events` (
                `EventID`	INTEGER,
                `EventTime`	TEXT,
                `AttackerPlayerName`	TEXT,
                `AttackerPlayerID`	INTEGER,
                `DemoFilename`	TEXT,
                PRIMARY KEY(`EventID`)
            )"), sqlConnection);

            //instead of using string delimeters we store victims like this to save on space & cpu
            sqlLibrary.ExecuteNonQuery(Sql.Builder.Append(
                @"CREATE TABLE IF NOT EXISTS `Victims` (
                `EventID`	INTEGER,
                `VictimName`	TEXT,
                `VictimID`	INTEGER,
                `EventTime` TEXT
            )"), sqlConnection);
        }

        private void LoadDBEvents()
        {
            //by loading DB events into memory we avoid having to interface with DB for every little thing
            //todo: again, is this neccesary ? it is faster, but should it use sqlite directly for simplicity ?
            {
                string eventsqlQuery = "SELECT * FROM Events ORDER by `EventID` ASC";
                Sql eventselectCommand = Oxide.Core.Database.Sql.Builder.Append(eventsqlQuery);

                sqlLibrary.Query(eventselectCommand, sqlConnection, eventlist =>
                {
                    if (eventlist == null)
                    {
                        return; // Empty result or no records found
                    }

                    // Iterate through resulting records
                    foreach (var evententry in eventlist)
                    {
                        Int32 EventID = Convert.ToInt32(evententry["EventID"]);
                        DateTime EventTime = DateTime.Parse(Convert.ToString(evententry["EventTime"]));
                        string AttackerPlayerName = Convert.ToString(evententry["AttackerPlayerName"]);
                        ulong AttackerPlayerID = Convert.ToUInt64(evententry["AttackerPlayerID"].ToString());
                        string DemoFilename = Convert.ToString(evententry["DemoFilename"]);

                        //events without demo file are orphaned and should be removed
                        if (!RustigateExtension.RustigateDemoExt.IsDemoOnDisk(DemoFilename))
                        {
                            Puts($"{DemoFilename} is missing, deleting orphaned event #{EventID}");

                            DeleteEventFromDB(EventID);
                            continue;
                        }

                        //the sql query above should have kept the eventids in order...
                        PlayerEvent NewEvent = new PlayerEvent(EventID, EventTime, AttackerPlayerName, AttackerPlayerID, DemoFilename);
                        PlayerEvents.Add(NewEvent);
                        NextEventID = EventID + 1;

                        //RustigateExtension keeps track of the demo folder size, but it needs help
                        RustigateExtension.RustigateDemoExt.NotifyNewDemoCreated(DemoFilename);

                        //victims for the event is stored in a different table,
                        //check them now because at this time we can garrente the parent query has been completed
                        ///its also worth noting that the local variables above like NewEvent are inaccessable 
                        ///since the following code below inside the query runs async at a later time
                        {
                            string victimsqlQuery = "SELECT * FROM Victims WHERE `EventID` is @0;";
                            Sql victimselectCommand = Oxide.Core.Database.Sql.Builder.Append(victimsqlQuery, EventID);

                            sqlLibrary.Query(victimselectCommand, sqlConnection, victimlist =>
                            {
                                if (victimlist == null)
                                {
                                    return; // Empty result or no records found
                                }

                                // Iterate through resulting records
                                foreach (var victimentry in victimlist)
                                {
                                    Int32 VictimEventID = Convert.ToInt32(victimentry["EventID"]);

                                    PlayerEvent foundPlayerEvent;
                                    if (FindPlayerEvent(VictimEventID, out foundPlayerEvent))
                                    {
                                        string VictimName = Convert.ToString(victimentry["VictimName"]);
                                        ulong VictimID = Convert.ToUInt64(victimentry["VictimID"]);
                                        DateTime VictimEventTime = DateTime.Parse(Convert.ToString(victimentry["EventTime"]));

                                        foundPlayerEvent.EventVictims.Add(VictimID, new EventVictimInfo(VictimID, VictimName, VictimEventTime));
                                    }
                                }
                            });
                        }
                    }
                });

                Puts("finished LoadDBEvents!");
            }
        }

        private void DeleteEventFromDB(Int32 EventID)
        {
            {
                string sqlQuery = "DELETE FROM Events WHERE `EventID` = @0;";
                Sql sqlCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, EventID);
                sqlLibrary.ExecuteNonQuery(sqlCommand, sqlConnection);
            }
            {
                string sqlQuery = "DELETE FROM Victims WHERE `EventID` = @0;";
                Sql sqlCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, EventID);
                sqlLibrary.ExecuteNonQuery(sqlCommand, sqlConnection);
            }
        }

        #endregion

        #region ChatCommands
#if DEBUGMODE
        [Command("printevents")]
        private void PrintEvents(IPlayer player, string command, string[] args)
        {
            foreach (var PlayerEvent in PlayerEvents)
            {
                Puts("-------------------------------");
                String s = String.Format("Event :{0} {1}", PlayerEvent.EventID, PlayerEvent.AttackerName);
                Puts(s);
                Puts("Victims:");
                foreach (var EventVictim in PlayerEvent.EventVictims)
                {
                    Puts(EventVictim.Value.PlayerName);
                }
            }

            player.Reply("Events printed to serbur console");
        }

        //acts as if one of the bots report u
        [Command("testselfreport")]
        private void TestReport(IPlayer player, string command, string[] args)
        {
            //all of the bots should be in a team, pick one at random to be the reporter
            int randomNumber = Oxide.Core.Random.Range(Bots.Count);
            BasePlayer BotReporter = Bots[randomNumber];

            //first local player is the one who will be reported for their FUKING HAK
            BasePlayer LocalPlayer = null;
            foreach (var localplayer in BasePlayer.activePlayerList)
            {
                LocalPlayer = localplayer;
                break;
            }

            OnPlayerReported(BotReporter, LocalPlayer.displayName, LocalPlayer.UserIDString, "hak", "hes just fuking hak from /testselfreport", "cheat");
            player.Reply("sent");
        }

#endif
        #endregion

        #region DiscordAPI

        private void PrepareDiscordReport(ulong ReporterID, string ReporterName, string TargetName, string TargetID, string ReportSubject, string ReportMessage)
        {
            /* For this plugin's reports, the admin only wants to know two things: 
               - is there a demofile related to a report
               - and where is that demofile located.
            our discord report should contain these, and hopefully todo: a convenient download link as well (this requires an oxide.Ext)
            there are plenty of other report plugins that will handle normal reports threw discord. we only care about demos.
            */

            ulong _TargetID = Convert.ToUInt64(TargetID);

            string ReportMSG = "";
            string EmbedTitle = $"**{TargetName}**[_{_TargetID}_] was reported by _{ReporterName}_ for {ReportSubject}: {ReportMessage}\n";
            string EmbedDescription = "";


            if (_TargetID == 0)
            {
                //bots dont seem to have a targetId
                return;
            }

            //active events dont have a usuable demofile yet.. delay the report of it
            if (PlayerActiveEventID.ContainsKey(_TargetID))
            {
                PlayerEvent FoundPlayerEvent;
                FindPlayerEvent(PlayerActiveEventID[_TargetID], out FoundPlayerEvent); ///this should always succeed..

                if (IsPlayerTeamedWith(ReporterID, FoundPlayerEvent.EventVictims.Keys.ToList()))
                {
                    if (DelayedReportEvents.Add(FoundPlayerEvent.EventID))
                    {
                        float MaxEventTime = FoundPlayerEvent.MaxEventTimer.Delay + 1.0f;
                        Timer _timer = timer.Once(MaxEventTime, () => PrepareDiscordReport(ReporterID, ReporterName, TargetName, TargetID, ReportSubject, ReportMessage));

                        DebugSay($"delaying {FoundPlayerEvent.EventID} for {MaxEventTime}seconds");
                        ///dont return, otherwise if this player is constantly fighting we will never report something
                        //return;
                    }
                }
            }

            /*there is a chance the victim(s) have won the battle or are retreating 
             * and do not have time to send in a report for an active event.
             *
             * sadly this means once a report comes in, we need to check all events (within reasonable timespan) 
             * that contain the target as the Attacker, and make sure the reporter is one of the victims
             * we should return _all_ events that have not been posted to discord related to attacker / victims 
             * depending on a number of factors this could be expensive on CPU, im prob wrong on how expensive it might be*/

            /*the events towards the end would be the most recent.. 
             * lets stop at say 1 hour timespan? what player would wait that long to report someone? lmao*/
            DateTime CurrentTime = DateTime.Now;
            DateTime OneHourAgo = CurrentTime.AddHours(-1);
            bool bFoundValidEvent = false;

            List<string> VictimNames = new List<string>();
            List<string> DemoFilenames = new List<string>();

            for (int i = PlayerEvents.Count - 1; i >= 0; i--)
            {
                PlayerEvent playerEvent = PlayerEvents[i];

                if(PlayerActiveEventID.ContainsKey(playerEvent.AttackerID))
                {
                    continue;
                }

                if (ReportedEvents.Contains(playerEvent.EventID))
                {
                    continue;
                }

                if (playerEvent.EventTime < OneHourAgo)
                {
                    break;
                }

                //this event's attacker is the player who is being reported
                if (playerEvent.AttackerID == _TargetID)
                {
                    //the one doing the reporting needs to be, or be teamed, with one of the victims
                    if (IsPlayerTeamedWith(ReporterID, playerEvent.EventVictims.Keys.ToList()))
                    {
                        DemoFilenames.Add(playerEvent.DemoFilename);
                        ReportedEvents.Add(playerEvent.EventID);

                        foreach (var EventVictimInfo in playerEvent.EventVictims.Values)
                        {
                            string EventVictimName = EventVictimInfo.PlayerName;
                            if (!VictimNames.Contains(EventVictimName))
                            {
                                VictimNames.Add(EventVictimName);
                            }
                        }

                        bFoundValidEvent = true;
                    }
                }
            }

            if (bFoundValidEvent)
            {
                RustigateExtension.RustigateDiscordPost.UploadDiscordReportAsync(
                    config.DiscordWebhookURL,
                    DemoFilenames,
                    new RustigateDiscordPost.DiscordReportInfo(TargetID, TargetName, ReporterName, ReportSubject, ReportMessage),
                    VictimNames,
                    DiscordPostCallback);
            }
        }

        private void DiscordPostCallback(string Message)
        {
            Interface.Oxide.LogWarning($"DiscordPost: {Message}");
        }

        #endregion

            #region Hooks

        private void Init()
        {
            //for plugin restarts
            RustigateExtension.RustigateDemoExt.DemoFolderSize = 0;

            config = Config.ReadObject<PluginConfig>();

            InitializeDB();
            LoadDBEvents();
        }

        private void Unload()
        {
#if DEBUGMODE
            Bots[0].Team.Disband();
            foreach (var Bot in Bots)
            {
                if(Bot != null)
                {
                    Bot.Kill(BaseNetworkable.DestroyMode.Gib);
                }
            }
#endif

            DebugSay("unloaded");

            for (Int32 i = 0; i < PlayerEvents.Count; i++)
            {
                EndPlayerEvent(i);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                //we do this again here cause if the plugin crashed, this will stop zombie recordings
                if (player.Connection.IsRecording)
                {
                    player.Connection.StopRecording();
                    PrintError($"stopping recording for {player.displayName}, EndPlayerEvent did not catch this?");
                }
            }

            sqlLibrary.CloseDb(sqlConnection);
        }

        void OnServerInitialized(bool initial)
        {
#if DEBUGMODE
            var BotTeam = RelationshipManager.ServerInstance.CreateTeam();
            for (int i = 0; i < 5; i++)
            {
                BaseEntity baseEntity = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", new Vector3(78.3f + (i*1.25f), 15.0f + (i * 1.25f), -187.8f + (i*1.25f)));
                if (baseEntity != null)
                {
                    baseEntity.Spawn();
                    BasePlayer botplayer = baseEntity.ToPlayer();
                    Bots.Add(botplayer);
                    BotTeam.AddPlayer(botplayer);
                }
            }
#endif

            /* dev note: the SQL DB loads happen as soon as the server starts up
             * by the time the server finishes loading the map, all queries would have finished
             * this means plugin hot-reloads which happen very fast dont give the sql queries time to finish
             * and this function fails to run properly as a result 
             * this is ok during normal use, but can make testing development of different pruning techniques difficult*/
            PruneOldEvents();
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            //todo: currently nades, rockets, molatov, and similar weapons do not trigger this
            if (attacker != null || info != null)
            {
                bool bIsSelfDamage = attacker == info.HitEntity?.ToPlayer();
                bool bHitEntityIsPlayer = info.HitEntity?.ToPlayer() != null;

                /*
                 * players that attack other players must be recorded
                 * this way if this player is ever reported by the victim or players on their team -
                 * - we know to raise an event in discord
                 */
                if (!bIsSelfDamage && bHitEntityIsPlayer)
                {
                    /* there is a case where a player could jokenly report the attacker whos on the same team
                     only allow enemies to report attackers */
                    BasePlayer VictimPlayer = info.HitEntity.ToPlayer();
                    if(!IsPlayerTeamedWith(attacker, VictimPlayer))
                    {
                        RecordPlayerEvent(attacker, VictimPlayer);

                        //as for the victim being the troublemaker:
                        //the victim can speed hack away and we would see it on the attacker's POV
                        //the victim can return fire and it will just run this function again with the victim being POV
                    }
                }
            }

            return null;
        }

        void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type)
        {
            //baseplayer Reporter can disconnect from server at anytime, this is why we grab name&id
            ulong ReporterID = reporter.userID;
            string ReporterName = reporter.displayName;

            PrepareDiscordReport(ReporterID, ReporterName, targetName, targetId, subject, message);
        }

        #endregion

        #region Rustigate

        private void DebugSay(string message)
        {
#if DEBUGMODE
            server.Command("say", (message));
#endif
        }

        private BasePlayer FindPlayerById(ulong PlayerID)
        {
            BasePlayer InstigatorPlayer = Oxide.Game.Rust.RustCore.FindPlayerById(PlayerID);
#if DEBUGMODE
            if (InstigatorPlayer == null)
            {
                foreach (var Bot in Bots)
                {
                    if (Bot.userID == PlayerID)
                    {
                        InstigatorPlayer = Bot;
                        break;
                    }
                }
            }
#endif
            return InstigatorPlayer;
        }

        private bool IsPlayerTeamedWith(BasePlayer InstigatorPlayer, BasePlayer TargetPlayer)
        {
            return InstigatorPlayer == TargetPlayer
                || InstigatorPlayer.Team != null ? InstigatorPlayer.Team.members.Contains(TargetPlayer.userID) : false;
        }

        private bool IsPlayerTeamedWith(BasePlayer InstigatorPlayer, ulong TargetPlayerID)
        {
            return InstigatorPlayer.userID == TargetPlayerID 
                || InstigatorPlayer.Team != null ? InstigatorPlayer.Team.members.Contains(TargetPlayerID) : false;
        }

        private bool IsPlayerTeamedWith(ulong InstigatorPlayerID, List<ulong> TargetPlayerIDs)
        {
            foreach (var TargetPlayerID in TargetPlayerIDs)
            {
                if (InstigatorPlayerID == TargetPlayerID)
                {
                    return true;
                }

                BasePlayer InstigatorPlayer = FindPlayerById(InstigatorPlayerID);
                if (InstigatorPlayer?.Team != null && InstigatorPlayer.Team.members.Contains(TargetPlayerID))
                {
                    return true;
                }
            }

            return false;
        }

        private void RecordPlayerEvent(BasePlayer AttackerPlayer, BasePlayer VictimPlayer)
        {
            if (AttackerPlayer == null || VictimPlayer == null)
            {
                PrintWarning("error generating event, attacker or victim player was invalid!");
                return;
            }

            //an event is already running, update victims and refresh timer
            if (PlayerActiveEventID.ContainsKey(AttackerPlayer.userID))
            {
                Int32 ActiveEventID = PlayerActiveEventID[AttackerPlayer.userID];
                PlayerEvent playerEvent;
                if (FindPlayerEvent(ActiveEventID, out playerEvent))
                {
                    playerEvent.AddEventVictim(VictimPlayer);
                }

                return;
            }

            Int32 EventID = NextEventID;
            NextEventID++;

            //a player can only record one demo at a time, keep track of the event they have active
            PlayerActiveEventID.Add(AttackerPlayer.userID, EventID);

            //as of 3-2-2023 default filename for the demo is: "demos/{UserIDString}/{DateTime.Now:yyyy-MM-dd-hhmmss}.dem"
            AttackerPlayer.StartDemoRecording();
            string DemoFileName = AttackerPlayer.Connection.RecordFilename;

            //use a timer to turn the recording off automatically
            Timer MinEventTimer = timer.Once(config.MinEventSeconds, () => EndPlayerEvent(EventID));
            Timer NaxEventTimer = timer.Once(config.MaxEventSeconds, () => EndPlayerEvent(EventID));

            PlayerEvent NewEvent = new PlayerEvent(EventID, AttackerPlayer, VictimPlayer, DemoFileName, MinEventTimer, NaxEventTimer);
            PlayerEvents.Add(NewEvent);

            DebugSay("new event for " + AttackerPlayer.displayName);
        }

        private bool FindPlayerEvent(Int32 EventID, out PlayerEvent FoundPlayerEvent)
        {
            //i dont like this Find here but the events list can have holes in it as old ones get pruned
            //cant rely on eventID being the array index... this also means dictionaries are out of the question
            int FoundIDX = PlayerEvents.FindIndex(x => x.EventID == EventID);
            if (FoundIDX == -1)
            {
                FoundPlayerEvent = null;
                return false;
            }

            FoundPlayerEvent = PlayerEvents[FoundIDX];
            return true;
        }

        private void EndPlayerEvent(Int32 EventID, bool bSkipDBSave = false)
        {
            PlayerEvent playerEvent;
            if(!FindPlayerEvent(EventID, out playerEvent))
                return;

            //can only end an event thats active or there wil bee trouble
            bool bIsActiveEvent = PlayerActiveEventID.ContainsKey(playerEvent.AttackerID) ? PlayerActiveEventID[playerEvent.AttackerID] == playerEvent.EventID : false;
            if (!bIsActiveEvent)
                return;

            playerEvent.AttackerPlayer.Connection.StopRecording();
            playerEvent.MinEventTimer.Destroy();
            playerEvent.MaxEventTimer.Destroy();

            RustigateExtension.RustigateDemoExt.NotifyNewDemoCreated(playerEvent.DemoFilename);

            //we save it to DB when the event ends to avoid too many updates during combat
            if (!bSkipDBSave)
            {
                //todo: i want all db stuff in the DB region, make these into functions at some point...
                {
                    string sqlQuery = "INSERT INTO Events (`EventID`, `EventTime`, `AttackerPlayerName`, `AttackerPlayerID`, `DemoFilename`) VALUES (@0, @1, @2, @3, @4);";
                    Sql insertCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, playerEvent.EventID, playerEvent.EventTime, playerEvent.AttackerName, playerEvent.AttackerID, playerEvent.DemoFilename);
                    sqlLibrary.Insert(insertCommand, sqlConnection, rowsAffected =>
                    {
                        if (rowsAffected == 0)
                        {
                            RaiseError("Could not insert record into DB!");
                        }
                    });
                }
                {
                    foreach (var EventVictim in playerEvent.EventVictims)
                    {
                        string VictimName = EventVictim.Value.PlayerName;
                        ulong VictimID = EventVictim.Value.PlayerID;
                        DateTime EventTime = EventVictim.Value.EventTime;
                        string sqlQuery = "INSERT INTO Victims (`EventID`, `VictimName`, `VictimID`, `EventTime`) VALUES (@0, @1, @2, @3);";
                        Sql insertCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, playerEvent.EventID, VictimName, VictimID, EventTime);
                        sqlLibrary.Insert(insertCommand, sqlConnection, rowsAffected =>
                        {
                            if (rowsAffected == 0)
                            {
                                RaiseError("Could not insert record into DB!");
                            }
                        });
                    }
                }
            }

            PlayerActiveEventID.Remove(playerEvent.AttackerID);
            DelayedReportEvents.Remove(EventID);
            DebugSay("event " + EventID + " for " + playerEvent.AttackerName + " has ended");

            //we just finished writing a new demo to disk
            //make sure to prune old events to honor of MaxDemoFolderSizeMB
            PruneOldEvents();
        }

        private void PruneOldEvents()
        {
            //if our demo folder is too big, we need to start deleting events starting with the oldest ones
            long DemoFolderSize = RustigateExtension.RustigateDemoExt.DemoFolderSize;
            long MaxDemoFolderSize = config.MaxDemoFolderSizeMB * 1000000;

            if (DemoFolderSize > MaxDemoFolderSize)
            {
                List<int> IdxToDelete = new List<int>();
                long BytesToDelete = DemoFolderSize - MaxDemoFolderSize;
                long BytesDeleted = 0;
                for (int i = 0; i < PlayerEvents.Count; i++) //begining of the list is the oldest event
                {
                    string DemoFilename = PlayerEvents[i].DemoFilename;
                    long DemoSize = RustigateExtension.RustigateDemoExt.GetDemoSize(DemoFilename);
                    BytesDeleted += DemoSize;
                    IdxToDelete.Add(i);
                    Puts($"pruning old event [{PlayerEvents[i].EventID}]{DemoFilename}, due to MaxDemoFolderSizeMB");

                    if (BytesDeleted > BytesToDelete)
                    {
                        break;
                    }
                }

                //dont take chances with deleting items in list while iterating threw it above...
                for (int i = IdxToDelete.Count - 1; i >= 0; i--)
                {
                    int EventIDX = IdxToDelete[i];
                    DeletePlayerEvent(PlayerEvents[EventIDX].EventID);
                }
            }
        }

        private long DeletePlayerEvent(Int32 EventID)
        {
            //todo: i dont like this Find here
            int FoundIDX = PlayerEvents.FindIndex(x => x.EventID == EventID);
            if (FoundIDX == -1)
                return 0;

            //just in case we are trying to delete an active event make sure the demofile handle is closed properly...
            EndPlayerEvent(PlayerEvents[FoundIDX].EventID, true);

            DeleteEventFromDB(EventID);
            long BytesDeleted = RustigateExtension.RustigateDemoExt.DeleteDemoFromDisk(PlayerEvents[FoundIDX].DemoFilename);
            PlayerEvents.RemoveAt(FoundIDX);

            return BytesDeleted;
        }

        #endregion
    }
}
