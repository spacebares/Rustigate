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
using Oxide.Game.Rust.Cui;
using System.Media;
using System.Runtime.CompilerServices;
using static Oxide.Plugins.Rustigate;
using static CombatLog;
using static Oxide.Core.RemoteLogger;
using System.Text.RegularExpressions;

namespace Oxide.Plugins
{
    [Info("Rustigate", "https://github.com/spacebares", "0.0.3")]
    [Description("Automatic demo recording of players when they attack others, with discord notifications for related player reports and an ingame event browser.")]
    class Rustigate : CovalencePlugin
    {
        #region GlobalFields

        Core.SQLite.Libraries.SQLite sqlLibrary = Interface.Oxide.GetLibrary<Core.SQLite.Libraries.SQLite>();
        Connection sqlConnection;

        private Int32 NextEventID = 0;
        private Int32 NextReportID = 0;

        ///we keep these in memory to avoid having to waste cpu talking with sqlite
        private List<PlayerEvent> PlayerEvents = new List<PlayerEvent>();
        private Dictionary<ulong, Int32> PlayerActiveEventID = new Dictionary<ulong, Int32>();

        ///again is this neccessary? sql select magic can do this as well, these are only here for caching / speedup
        //this contains all the events a player has been involved with
        private Dictionary<ulong, List<Int32>> EventPlayers = new Dictionary<ulong, List<Int32>>();

        //events that have been reported by players (F7 Report)
        private Dictionary<Int32, EventReport> EventReports = new Dictionary<Int32, EventReport>();

        //events that are waiting to complete before being reported
        private HashSet<Int32> DelayedReportEvents = new HashSet<Int32>();

#if DEBUGMODE
        private List<BasePlayer> Bots = new List<BasePlayer>();
        private Int32 NumBots = 5;
#endif

        #endregion

        #region Config

        private class PluginConfig
        {
            /*events start the moment a player attacks someone, after MinEventSeconds the event is over and a demo is saved
            however each time the player attacks someone this timer is reset.*/
            public Int32 MinEventSeconds;

            /*regardless of how many times a player attacks others during an event, it will always end after MaxEventSeconds
            this prevents a malicious player from just attacking someone every <5min to delay the saving of a demo for review*/
            public Int32 MaxEventSeconds;

            //if all of our events are larger then this, then we start deleting the oldest events until we are back under
            public Int32 MaxDemoFolderSizeMB;

            //see https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks for info
            public string DiscordWebhookURL;

            /*set this to the discord server's current boost tier; higher boost teir offers less file attachment clutter.
            with the default setting, the plugin will have to do multiple attachment messages if there are lots of demos to upload at once
            if a single demo zipped up is larger then 8MiB it will not upload at all,
            if your Rust server is producing these large demos then consider boosting your discord server
            you can always just download the demo directly from the server threw its file management panel or FTP

            @note: if your server's tier does not match, then this will reset and stay locked at 0 until server or plugin is restarted*/
            public Int32 DiscordServerBoostTier;

            //demofiles are zipped then uploaded to discord. although this happens on a seperate thread, disabling this behavior could improve server performance.
            public bool UploadDemosToDiscord;
        }


        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                MinEventSeconds = 30,
                MaxEventSeconds = 300,
                MaxDemoFolderSizeMB = 2048,
                DiscordWebhookURL = "",
                DiscordServerBoostTier = 0,
                UploadDemosToDiscord = true
            };
        }

        private PluginConfig config;

        #endregion

        #region Classes

        internal class EventVictimInfo
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

        internal class PlayerEvent
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

            //this is the F7 report related to this event
            public Int32 ReportID;

            public PlayerEvent()
            {
                this.ReportID = -1;
            }
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

                this.ReportID = -1;
            }

            public PlayerEvent(Int32 EventID, DateTime EventTime, string AttackerPlayerName, ulong AttackerPlayerID, string DemoFilename)
            {
                this.EventID = EventID;

                this.AttackerID = AttackerPlayerID;
                this.AttackerName = AttackerPlayerName;

                this.EventTime = EventTime;
                this.DemoFilename = DemoFilename;

                this.ReportID = -1;
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

            public string GetEventVictimNames()
            {
                string VictimNames = "";

                int num = 0;
                foreach (var EventVictim in EventVictims)
                {
                    num++;
                    EventVictimInfo eventVictimInfo = EventVictim.Value;
                    VictimNames += eventVictimInfo.PlayerName;

                    if (num < EventVictims.Count)
                        VictimNames += ", ";
                }

                return VictimNames;
            }
        }

        internal class EventReport
        {
            public Int32 ReportID;
            public string TargetName;
            public ulong TargetID;
            public string ReporterName;
            public ulong ReporterID;
            public string ReportSubject;
            public string ReportMessage;
            public List<Int32> EventIDs = new List<Int32>();
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

            //need to save what events got reported with who and why they did it for the ingame event browser 
            sqlLibrary.ExecuteNonQuery(Sql.Builder.Append(
                @"CREATE TABLE IF NOT EXISTS `EventReports` (
	            `ReportID`	INTEGER,
                `TargetName`    TEXT,
                `TargetID`      INTEGER,
	            `ReporterName`	TEXT,
	            `ReporterID`	INTEGER,
	            `ReportSubject`	TEXT,
	            `ReportMessage`	TEXT,
                `ReportEvents`  TEXT,
	            PRIMARY KEY(`ReportID`)
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

                        //RustigateExtension keeps track of the demo folder size, but it needs our help
                        RustigateExtension.RustigateDemoExt.NotifyNewDemoCreated(DemoFilename);
                    }
                });

                Puts("finished LoadDBEvents!");
            }
        }

        private void LoadDBEventVictims()
        {
            //victims for the event is stored in its own table
            {
                string victimsqlQuery = "SELECT * FROM Victims"; //0,1,2,3 is faster for Find().. i think
                Sql victimselectCommand = Oxide.Core.Database.Sql.Builder.Append(victimsqlQuery);

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
                        else
                        {
                            RaiseError($"!!! COULD NOT FIND EVENTID: {VictimEventID} FOR VICTIMS !!!");
                        }
                    }
                });

                Puts("finished LoadDBEventVictims!");
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

        private void LoadDBEventReports()
        {
            //todo: load into memory for speedup
            {
                string eventsqlQuery = "SELECT * FROM EventReports ORDER by `ReportID` ASC";
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
                        Int32 ReportID = Convert.ToInt32(evententry["ReportID"]);
                        string TargetName = Convert.ToString(evententry["TargetName"]);
                        ulong TargetID = Convert.ToUInt64(evententry["TargetID"]);
                        string ReporterName = Convert.ToString(evententry["ReporterName"]);
                        ulong ReporterID = Convert.ToUInt64(evententry["ReporterID"]);
                        string ReportSubject = Convert.ToString(evententry["ReportSubject"]);
                        string ReportMessage = Convert.ToString(evententry["ReportMessage"]);
                        string ReportEventIDs = Convert.ToString(evententry["ReportEvents"]);

                        EventReport NewEventReport = new EventReport
                        {
                            ReportID = ReportID,
                            TargetName = TargetName,
                            TargetID = TargetID,
                            ReporterName = ReporterName,
                            ReporterID = ReporterID,
                            ReportSubject = ReportSubject,
                            ReportMessage = ReportMessage
                        };

                        string[] FoundEventIDs = ReportEventIDs.Split(' ');
                        foreach (var FoundEventID in FoundEventIDs)
                        {
                            Int32 EventID = Convert.ToInt32(FoundEventID);
                            NewEventReport.EventIDs.Add(EventID);

                            PlayerEvent FoundPlayerEvent;
                            if(FindPlayerEvent(EventID, out FoundPlayerEvent))
                            {
                                FoundPlayerEvent.ReportID = ReportID;
                            }
                            else
                            {
                                RaiseError($"!!! COULD NOT FIND EVENTID: {EventID} FOR REPORTID: {ReportID} !!!");
                            }
                        }

                        EventReports.Add(ReportID, NewEventReport);

                        //the sql query above should have kept the ReportIDs in order...
                        NextReportID = ReportID + 1;
                    }
                });

                Puts("finished LoadDBReportedEvents!");
            }
        }

        private void InsertEventReportIntoDB(EventReport eventReport)
        {
            string EventIDsAsString = string.Join("!", eventReport.EventIDs);
            Puts(EventIDsAsString);
            string sqlQuery = "INSERT INTO EventReports (`ReportID`, `TargetName`, `TargetID`, `ReporterName`, `ReporterID`, `ReportSubject`, `ReportMessage`, `ReportEvents`) VALUES (@0, @1, @2, @3, @4, @5, @6, @7);";
            Sql insertCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, eventReport.ReportID, eventReport.TargetName, eventReport.TargetID, eventReport.ReporterName, eventReport.ReporterID, eventReport.ReportSubject, eventReport.ReportMessage, EventIDsAsString);
            sqlLibrary.Insert(insertCommand, sqlConnection, rowsAffected =>
            {
                if (rowsAffected == 0)
                {
                    RaiseError("Could not insert record into DB!");
                }
            });
        }

        private void InsertEventIntoDB(PlayerEvent playerEvent)
        {
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

        //direct test of a big demo file that can never be uploaded to discord (make sure its larger then 8, 50, or 100 MiB depending on discord boost level)
        [Command("testbigbaddemo")]
        private void TestBigBadDemo(IPlayer player, string command, string[] args)
        {
            RustigateExtension.RustigateDiscordPost.UploadDiscordReportAsync(
                new List<string> { "demos/massivedemo.dem" },
                new RustigateDiscordPost.DiscordReportInfo("ass", "ass", "ass", "ass", "huge demo test", "this should generate error message"),
                new List<string> { "DolphinsTestChannel" },
                DiscordPostCallback);
            player.Reply("sent");
        }

#endif
        #endregion

        #region DiscordAPI

        private void PrepareEventReport(ulong ReporterID, string ReporterName, string TargetName, string TargetID, string ReportSubject, string ReportMessage)
        {
            /* For this plugin's reports, the admin only wants to know two things: 
               - is there a demofile related to a report
               - and where is that demofile located.
            our discord report should contain these, and a convenient download link
            there are plenty of other report plugins that will handle normal reports threw discord. we only care about demos.
            */

            ulong _TargetID = Convert.ToUInt64(TargetID);

            if (_TargetID == 0)
            {
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
                        Timer _timer = timer.Once(MaxEventTime, () => PrepareEventReport(ReporterID, ReporterName, TargetName, TargetID, ReportSubject, ReportMessage));

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

            List<string> VictimNames = new List<string>();
            List<string> DemoFilenames = new List<string>();

            EventReport NewEventReport = new EventReport()
            {
                ReportID = NextReportID,
                TargetName = TargetName,
                TargetID = Convert.ToUInt64(TargetID),
                ReporterName = ReporterName,
                ReporterID = ReporterID,
                ReportSubject = ReportSubject,
                ReportMessage = ReportMessage
                ///EventIDs = x //related events are added below
            };
            ///we dont know if this report is valid yet... so dont add it     
            ///ReportedEvents.Add(NextReportID, NewEventReport);         

            for (int i = PlayerEvents.Count - 1; i >= 0; i--)
            {
                PlayerEvent playerEvent = PlayerEvents[i];

                //events that are active are already going to be reported via a delay
                if (PlayerActiveEventID.ContainsKey(playerEvent.AttackerID))
                {
                    Puts($"{playerEvent.EventID} was active");
                    continue;
                }

                //dont spam discord with already reported events
                if (EventReports.ContainsKey(playerEvent.ReportID))
                {
                    Puts($"{playerEvent.EventID} was already reported");
                    continue;
                }

                /* any events up to an hour ago might be of interest in relation to the victim doing the report
                 * usually when a player dies, they F7 report... they dont wait more then an hour */
                if (playerEvent.EventTime < OneHourAgo)
                {
                    Puts($"{playerEvent.EventID} > an hour ago");
                    break;
                }

                //this event's attacker is the player who is being reported
                Puts($"{playerEvent.AttackerID} == {_TargetID} ?");
                if (playerEvent.AttackerID == _TargetID)
                {
                    //the one doing the reporting needs to be, or be teamed, with one of the victims
                    bool bIsReporterTeamedWithVictims = IsPlayerTeamedWith(ReporterID, playerEvent.EventVictims.Keys.ToList());
                    Puts($"{ReporterID} teamed with {playerEvent.GetEventVictimNames()} == {bIsReporterTeamedWithVictims}");
                    if (bIsReporterTeamedWithVictims)
                    {
                        DemoFilenames.Add(playerEvent.DemoFilename);

                        foreach (var EventVictimInfo in playerEvent.EventVictims.Values)
                        {
                            string EventVictimName = EventVictimInfo.PlayerName;
                            if (!VictimNames.Contains(EventVictimName))
                            {
                                VictimNames.Add(EventVictimName);
                            }
                        }

                        NewEventReport.EventIDs.Add(playerEvent.EventID);
                        playerEvent.ReportID = NewEventReport.ReportID;
                        Puts($"added {playerEvent.EventID} with reportid: {NewEventReport.ReportID}!");
                    }
                }
            }

            if (NewEventReport.EventIDs.Count > 0)
            {
                //we have a valid report, move the ID up for the next one
                EventReports.Add(NextReportID, NewEventReport);
                NextReportID++;

                if (config.DiscordWebhookURL != "")
                {
                    //todo, can NewEventReport be used for this as well?
                    RustigateExtension.RustigateDiscordPost.UploadDiscordReportAsync(
                        DemoFilenames,
                        new RustigateDiscordPost.DiscordReportInfo(TargetID, TargetName, ReporterName, ReporterID.ToString(), ReportSubject, ReportMessage),
                        VictimNames,
                        DiscordPostCallback);
                }

                InsertEventReportIntoDB(NewEventReport);
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
            config = Config.ReadObject<PluginConfig>();

            RustigateExtension.RustigateDemoExt.DemoFolderSize = 0; //fixes bug with plugin restarts
            RustigateExtension.RustigateDiscordPost.LoadDiscordConfig(
                config.DiscordServerBoostTier,
                config.DiscordWebhookURL,
                config.UploadDemosToDiscord,
                server.LocalAddress.MapToIPv4().ToString()); //todo: is this the way to transfer config ???

            ///it looks like in code, SQLITE runs queries in sequence on its own thread
            ///so as long as these are in order things should work fine
            InitializeDB();
            LoadDBEvents();
            LoadDBEventVictims();
            LoadDBEventReports();
        }

        private void Unload()
        {
#if DEBUGMODE
            if (Bots.Count > 0)
            {
                Bots[0].Team?.Disband();
            }

            foreach (var Bot in Bots)
            {
                if (Bot != null)
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
                CuiHelper.DestroyUi(player, "RTMainPanel");
                CuiHelper.DestroyUi(player, "RTMainSearchPanel");

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
            for (int i = 0; i < NumBots; i++)
            {
                BaseEntity baseEntity = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", new Vector3(78.3f + (i * 1.25f), 15.0f + (i * 1.25f), -187.8f + (i * 1.25f)));
                if (baseEntity != null)
                {
                    baseEntity.Spawn();
                    BasePlayer botplayer = baseEntity.ToPlayer();
                    Bots.Add(botplayer);
                    BotTeam.AddPlayer(botplayer);
                }
            }

            {
                var collectorplayer = Oxide.Game.Rust.RustCore.FindPlayerByName("collector");
                if(collectorplayer != null)
                {
                    timer.Once(1.0f, () =>
                    {
                        ShowEventsUI(collectorplayer.IPlayer, "", null);
                    });
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
                    if (!IsPlayerTeamedWith(attacker, VictimPlayer))
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

            PrepareEventReport(ReporterID, ReporterName, targetName, targetId, subject, message);
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
            if (!FindPlayerEvent(EventID, out playerEvent))
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
                InsertEventIntoDB(playerEvent);
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

        #region UI

        [Flags]
        private enum UISortOptions
        {
            EEventID = 0,
            EReported = 1,
            EAttackerName = 2,
            EDate = 4
        }

        //remember what the admin has sorted for previously, allows faster activity resume when menu is re-opened
        private Dictionary<ulong, UISortOptions> PlayerUISortOptions = new Dictionary<ulong, UISortOptions>();

        //remember the page for the results the admin is currently viewing, for faster activity resume
        private Dictionary<ulong, Int32> PlayerUIPage = new Dictionary<ulong, Int32>();

        //return only a maximum events for any search queries, this is a UI limitation... theres no srolling in CUI
        private static byte MaxResultsPerPage = 20;

        #region SendUI
        private void SendUIFramePanels(BasePlayer player)
        {
            CuiElementContainer EventsUI = new CuiElementContainer();
            EventsUI.Add(new CuiPanel
            {
                Image = { Color = "0.20 0.1 0 1" },
                RectTransform = {
                    AnchorMin = "0.3 0.025",
                    AnchorMax = "0.843 0.990"
                },
                CursorEnabled = true,
                KeyboardEnabled = true
            }, "Overlay", "RTMainPanel", "RTMainPanel");
            EventsUI.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.08" },
                RectTransform = {
                    AnchorMin = "0 0.955",
                    AnchorMax = "1 1"
                },
            }, "RTMainPanel", "RTTitlePanel", "RTTitlePanel");
            EventsUI.Add(new CuiLabel
            {
                Text =
                {
                    Text = "Recorded Events",
                    Color = "1 0.71 0 1",
                    FontSize = 20,
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1"
                }
            }, "RTTitlePanel", "RTTitleText", "RTTitleText");
            EventsUI.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.39 0.18 0 1",
                    Command = "hideevents",
                },
                Text =
                {
                    Text = "X",
                    FontSize = 16,
                    Color = "1 0.75 0 1",
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0.956 0.956",
                    AnchorMax = "0.9965 0.9965"
                }
            }, "RTMainPanel", "RTClose", "RTClose");
            EventsUI.Add(new CuiPanel
            {
                Image =
                {
                    Color = "1 0.75 0 0.2"
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.891",
                    AnchorMax = "0.975 0.951"
                }
            }, "RTMainPanel", "RTColumnPanel", "RTColumnPanel");
            EventsUI.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0 0 0 0.5"
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.11",
                    AnchorMax = "0.975 0.891"
                }
            }, "RTMainPanel", "RTResultsPanel", "RTResultsPanel");
            CuiHelper.AddUi(player, EventsUI);
        }

        private void SendUISearchFramePanels(BasePlayer player)
        {
            CuiElementContainer EventsUI = new CuiElementContainer();
            EventsUI.Add(new CuiPanel
            {
                Image = { Color = "0.20 0.1 0 1" },
                RectTransform = {
                    AnchorMin = "0.022 0.685",
                    AnchorMax = "0.178 0.963"
                },
                CursorEnabled = true,
                KeyboardEnabled = true
            }, "Overlay", "RTMainSearchPanel", "RTMainSearchPanel");
            EventsUI.Add(new CuiLabel
            {
                Text =
                {
                    Text = "search box stuff one day",
                    FontSize = 16,
                    Color = "1 1 1 1",
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1"
                }
            }, "RTMainSearchPanel", "RTSearchPanelTemp1", "RTSearchPanelTemp1");
            CuiHelper.AddUi(player, EventsUI);
        }

        private CuiButton CreateSortButton(string ButtonCommand, string ButtonText, bool bUseAltColor, bool bIsSelected, string ButtonXMinAnchor, string ButtonXMaxAnchor)
        {
            string ButtonColor = bIsSelected ? "0.78 0.39 0 1" : (bUseAltColor ? "0.43 0.22 0 1" : "0.39 0.18 0 1");
            CuiButton NewButton = new CuiButton
            {
                Button =
                {
                    Color = ButtonColor,
                    Command = ButtonCommand != "" ? $"testcommand {ButtonCommand}" : "",
                },
                Text =
                {
                    Text = ButtonText,
                    FontSize = 16,
                    Color = "1 0.75 0 1",
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = $"{ButtonXMinAnchor} 0",
                    AnchorMax = $"{ButtonXMaxAnchor} 1"
                }
            };

            return NewButton;
        }

        private void SendUISortButtons(BasePlayer player)
        {
            UISortOptions SortOptions = UISortOptions.EEventID;
            PlayerUISortOptions.TryGetValue(player.userID, out SortOptions);

            CuiElementContainer EventsUI = new CuiElementContainer();

            bool b1 = (SortOptions & UISortOptions.EReported) == UISortOptions.EReported;
            EventsUI.Add(CreateSortButton("reportsort", "F7 ?", false, b1, "0", "0.088"), "RTColumnPanel", "RTReportedSortButton", "RTReportedSortButton");

            bool b2 = (SortOptions & UISortOptions.EEventID) == UISortOptions.EEventID;
            EventsUI.Add(CreateSortButton("EventIDSort", "ID", true, b2, "0.088", "0.176"), "RTColumnPanel", "RTEventIDSortButton", "RTEventIDSortButton");

            bool b3 = (SortOptions & UISortOptions.EAttackerName) == UISortOptions.EAttackerName;
            EventsUI.Add(CreateSortButton("AttackerSort", "Attacker Name", false, b3, "0.176", "0.427"), "RTColumnPanel", "RTAttackerNameSortButton", "RTAttackerNameSortButton");

            bool b4 = (SortOptions & UISortOptions.EDate) == UISortOptions.EDate;
            EventsUI.Add(CreateSortButton("DateSort", "Date", true, b4, "0.427", "0.69"), "RTColumnPanel", "RTDateSortButton", "RTDateSortButton");

            //cant sort by victims
            EventsUI.Add(CreateSortButton("", "Victims", false, false, "0.69", "1"), "RTColumnPanel", "RTVictimsSortButton", "RTVictimsSortButton");

            CuiHelper.AddUi(player, EventsUI);
        }

        //todo: this function should contain search parameters ?
        private void SendUIResults(BasePlayer player, Int32 Page)
        {
            //todo: this might not belong here yet...
            UISortOptions SortOptions = UISortOptions.EEventID;
            PlayerUISortOptions.TryGetValue(player.userID, out SortOptions);

            int iter = -1; //used to visually align rows properly
            bool bHitMaxResults = false;
            CuiElementContainer EventsUI = new CuiElementContainer();

            EventsUI.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0 0 0 0.5"
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.11",
                    AnchorMax = "0.975 0.891"
                }
            }, "RTMainPanel", "RTResultsPanel", "RTResultsPanel");
            CuiHelper.AddUi(player, EventsUI);

            //todo: for now just look threw all the events, in the future need to pass a "Results" list to this
            int StartIDX = Math.Max(0, PlayerEvents.Count - (Page * (MaxResultsPerPage+1)));
            //Puts(StartIDX.ToString());
            for (int i = StartIDX - 1; i >= 0; i--)
            {
                iter++;
                PlayerEvent playerEvent = PlayerEvents[i];
                Int32 EventID = playerEvent.EventID;

                bool bUseAltColor = i % 2 == 1;

                EventsUI.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = bUseAltColor ? "0.41 0.15 0 1" : "0.31 0.1 0 1",
                    },
                    RectTransform =
                    {
                        AnchorMin = $"0 {0.953-(iter*0.047)}",
                        AnchorMax = $"1 {1-(iter*0.047)}"
                    }
                }, "RTResultsPanel", $"RTResultID{EventID}", $"RTResultID{EventID}");
                bool bIsEventReported = EventReports.ContainsKey(playerEvent.ReportID);
                EventsUI.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = bIsEventReported ? "X" : "",
                        FontSize = 16,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleCenter
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0.08 1"
                    }
                }, $"RTResultID{EventID}", $"RTResultID{EventID}Field1", $"RTResultID{EventID}Field1");
                EventsUI.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"{playerEvent.EventID}",
                        FontSize = 16,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleCenter
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.08 0",
                        AnchorMax = "0.176 1"
                    }
                }, $"RTResultID{EventID}", $"RTResultID{EventID}Field2", $"RTResultID{EventID}Field2");
                EventsUI.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"{playerEvent.AttackerName}",
                        FontSize = 16,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleCenter
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.176 0",
                        AnchorMax = "0.427 1"
                    }
                }, $"RTResultID{EventID}", $"RTResultID{EventID}Field3", $"RTResultID{EventID}Field3");
                EventsUI.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"{playerEvent.EventTime.ToShortDateString()} {playerEvent.EventTime.ToShortTimeString()}",
                        FontSize = 16,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleCenter
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.427 0",
                        AnchorMax = "0.69 1"
                    }
                }, $"RTResultID{EventID}", $"RTResultID{EventID}Field4", $"RTResultID{EventID}Field4");
                EventsUI.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = playerEvent.GetEventVictimNames(),
                        FontSize = 16,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleCenter
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.69 0",
                        AnchorMax = "1 1"
                    }
                }, $"RTResultID{EventID}", $"RTResultID{EventID}Field5", $"RTResultID{EventID}Field5");
                EventsUI.Add(new CuiButton
                {
                    Button =
                    {
                        Command = $"RTUIShowEventInfo {EventID}",
                        Color = "0 0 0 0"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }, $"RTResultID{EventID}", $"RTResultID{EventID}Click", $"RTResultID{EventID}Click");

                if (iter == MaxResultsPerPage)
                {
                    bHitMaxResults = true;
                    break;
                }
            }

            EventsUI.Add(new CuiPanel
            {
                Image =
                    {
                        Color = "0 0 0 0"
                    },
                RectTransform =
                    {
                        AnchorMin = "0.025 0.02",
                        AnchorMax = "0.975 0.1"
                    }
            }, "RTMainPanel", "RTResultsPageInfoPanel", "RTResultsPageInfoPanel");
            EventsUI.Add(new CuiLabel
            {
                Text =
                    {
                        Text = $"{Page+1}",
                        FontSize = 24,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleCenter
                    },
                RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
            }, "RTResultsPageInfoPanel", "RTResultsPageText", "RTResultsPageText");

            //had some strange behavior with buttons not being sent/destroyed,
            //so ganna keep them alive and just change anchor to "disable" them when needed
            EventsUI.Add(new CuiButton
            {
                Button =
                {
                    Command = "RTUIResultsBack",
                    Color = "0.58 0.19 0 1"
                },
                Text =
                {
                    Text = "<",
                    Color = "0.78 0.39 0 1",
                    FontSize = 24,
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = Page > 0 ? "0.35 0" : "0 0",
                    AnchorMax = Page > 0 ? "0.45 1" : "0 0"
                }
            }, "RTResultsPageInfoPanel", "RTResultsBackButton", "RTResultsBackButton");

            //had some strange behavior with buttons not being sent/destroyed,
            //so ganna keep them alive and just change anchor to "disable" them when needed
            EventsUI.Add(new CuiButton
            {
                Button =
                {
                    Command = "RTUIResultsForward",
                    Color = "0.58 0.19 0 1"
                },
                Text =
                {
                    Text = ">",
                    Color = "0.78 0.39 0 1",
                    FontSize = 24,
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = bHitMaxResults ? "0.55 0" : "0 0",
                    AnchorMax = bHitMaxResults ? "0.65 1" : "0 0"
                }
            }, "RTResultsPageInfoPanel", "RTResultsForwardButton", "RTResultsForwardButton");

            CuiHelper.AddUi(player, EventsUI);
        }

        private void SendUIEventInfo(BasePlayer player, Int32 EventID)
        {
            PlayerEvent FoundPlayerEvent;
            if (!FindPlayerEvent(EventID, out FoundPlayerEvent))
            {
                return;
            }
            CuiElementContainer EventsUI = new CuiElementContainer();

            EventsUI.Add(new CuiPanel
            {
                Image =
                {
                    Color = "1 0.5 0 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.15 0.25",
                    AnchorMax = "0.85 0.75"
                }
            }, "RTMainPanel", "RTEventInfoPanel", "RTEventInfoPanel");
            EventsUI.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0.25 0.1 0 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.005 0.005",
                    AnchorMax = "0.995 0.995"
                }
            }, "RTEventInfoPanel", "RTEventInfoOutline", "RTEventInfoOutline");
            EventsUI.Add(new CuiButton
            {
                Button =
                {
                    Color = "0.39 0.18 0 1",
                    Command = "RTUIHideEventInfo",
                },
                Text =
                {
                    Text = "X",
                    FontSize = 16,
                    Color = "1 0.75 0 1",
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0.94 0.94",
                    AnchorMax = "1 1"
                }
            }, "RTEventInfoPanel", "RTEventInfoClose", "RTEventInfoClose");
            EventsUI.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"EventID: {FoundPlayerEvent.EventID}",
                    Color = "1 0.5 0 1",
                    FontSize = 16
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.025",
                    AnchorMax = "0.925 0.95"
                }
            }, "RTEventInfoPanel", "RTEventInfoID", "RTEventInfoID");
            EventsUI.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"{FoundPlayerEvent.AttackerName} [{FoundPlayerEvent.AttackerID}]",
                    Color = "1 0.5 0 1",
                    FontSize = 16
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.075",
                    AnchorMax = "0.925 0.90"
                }
            }, "RTEventInfoPanel", "RTEventInfoAttacker", "RTEventInfoAttacker");
            EventsUI.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"{FoundPlayerEvent.EventTime.ToShortDateString()} [{FoundPlayerEvent.EventTime.ToShortTimeString()}]",
                    Color = "1 0.5 0 1",
                    FontSize = 16,
                    Align = TextAnchor.UpperRight
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.025",
                    AnchorMax = "0.925 0.95"
                }
            }, "RTEventInfoPanel", "RTEventInfoDate", "RTEventInfoDate");
            EventsUI.Add(new CuiButton
            {
                Button =
                {
                    Command = $"RTUISendEventToDiscord {FoundPlayerEvent.EventID}",
                    Color = "0.39 0.18 0 1"
                },
                Text =
                {
                    Text = $"Demo: {FoundPlayerEvent.DemoFilename}\n[Click to Send to Discord]",
                    Color = "1 0.5 0 1",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.700",
                    AnchorMax = "0.925 0.825"
                }
            }, "RTEventInfoPanel", "RTEventInfoDemo", "RTEventInfoDemo");
            EventsUI.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"Victims: {FoundPlayerEvent.GetEventVictimNames()}",
                    Color = "1 0.5 0 1",
                    FontSize = 16
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.275",
                    AnchorMax = "0.925 0.65"
                }
            }, "RTEventInfoPanel", "RTEventInfoVictims", "RTEventInfoVictims");

            //show report info if it exists
            if(EventReports.ContainsKey(FoundPlayerEvent.ReportID))
            {
                EventReport eventReport = EventReports[FoundPlayerEvent.ReportID];
                string CleanSubjectMessage = Regex.Replace(eventReport.ReportSubject, @"\t|\n|\r", " ");
                string CleanReportMessage = Regex.Replace(eventReport.ReportMessage, @"\t|\n|\r", " ");
                EventsUI.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = $"Reported by: {eventReport.ReporterName} [{eventReport.ReporterID}]\n\n" +
                               $"{CleanSubjectMessage}\n" +
                               $"{CleanReportMessage}",
                        Color = "1 0.5 0 1",
                        FontSize = 16
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.025 0.005",
                        AnchorMax = "0.925 0.4"
                    }
                }, "RTEventInfoPanel", "RTEventInfoReportText", "RTEventInfoReportText");
            }

            CuiHelper.AddUi(player, EventsUI);
        }

        #endregion

        #region UIChatCommands

        [Command("showevents")]
        private void ShowEventsUI(IPlayer player, string command, string[] args)
        {
            //allow non admins with the "viewdemoevents" permission to use this command
            if (!player.HasPermission("viewdemoevents"))
                if (!player.IsAdmin)
                    return;

            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            SendUIFramePanels(FoundPlayer);
            SendUISearchFramePanels(FoundPlayer);
            SendUISortButtons(FoundPlayer);

            Int32 FoundPage = 0;
            PlayerUIPage.TryGetValue(FoundPlayer.userID, out FoundPage);
            SendUIResults(FoundPlayer, FoundPage);
        }

        [Command("hideevents")]
        private void HideEventsUI(IPlayer player, string command, string[] args)
        {
            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            CuiHelper.DestroyUi(FoundPlayer, "RTMainPanel");
            CuiHelper.DestroyUi(FoundPlayer, "RTMainSearchPanel");
        }

        [Command("testcommand")]
        private void TestCommand(IPlayer player, string command, string[] args)
        {
            player.Message($"{command} {args[0]}");
        }

        [Command("RTUIResultsBack")]
        private void RTUIResultsBack(IPlayer player, string command, string[] args)
        {
            //allow non admins with the "viewdemoevents" permission to use this command
            if (!player.HasPermission("viewdemoevents"))
                if (!player.IsAdmin)
                    return;

            //todo: atm theres a problem with this, we dont have any results to send
            //for now this works...
            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            Int32 FoundPage = 0;
            if (!PlayerUIPage.ContainsKey(FoundPlayer.userID))
            {
                PlayerUIPage.Add(FoundPlayer.userID, 0);
            }

            FoundPage = Math.Max(0, PlayerUIPage[FoundPlayer.userID] - 1);
            PlayerUIPage[FoundPlayer.userID] = FoundPage;

            SendUIResults(FoundPlayer, FoundPage);
        }

        [Command("RTUIResultsForward")]
        private void RTUIResultsForward(IPlayer player, string command, string[] args)
        {
            //allow non admins with the "viewdemoevents" permission to use this command
            if (!player.HasPermission("viewdemoevents"))
                if (!player.IsAdmin)
                    return;

            //todo: atm theres a problem with this, we dont have any results to send
            //for now this works...
            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            Int32 FoundPage = 0;
            if (!PlayerUIPage.ContainsKey(FoundPlayer.userID))
            {
                PlayerUIPage.Add(FoundPlayer.userID, 0);
            }

            FoundPage = Math.Max(0, PlayerUIPage[FoundPlayer.userID] + 1);
            PlayerUIPage[FoundPlayer.userID] = FoundPage;

            SendUIResults(FoundPlayer, FoundPage);
        }

        [Command("RTUIShowEventInfo")]
        private void RTUIShowEventInfo(IPlayer player, string command, string[] args)
        {
            //allow non admins with the "viewdemoevents" permission to use this command
            if (!player.HasPermission("viewdemoevents"))
                if (!player.IsAdmin)
                    return;

            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            if (args.Count() > 0)
            {
                Int32 EventID = Convert.ToInt32(args[0]);
                SendUIEventInfo(FoundPlayer, EventID);
            }
        }

        [Command("RTUIHideEventInfo")]
        private void RTUIHideEventInfo(IPlayer player, string command, string[] args)
        {
            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            CuiHelper.DestroyUi(FoundPlayer, "RTEventInfoPanel");
        }

        [Command("RTUISendEventToDiscord")]
        private void RTUISendEventToDiscord(IPlayer player, string command, string[] args)
        {
            //allow non admins with the "viewdemoevents" permission to use this command
            if (!player.HasPermission("viewdemoevents"))
                if (!player.IsAdmin)
                    return;

            Int32 EventID = Convert.ToInt32(args[0]);
            PlayerEvent FoundPlayerEvent;
            if (!FindPlayerEvent(EventID, out FoundPlayerEvent))
            {
                player.Message($"Invalid event:{EventID}");
                return;
            }

            //this basically replaces the button with a SENT thing
            //todo: this is ugly and requires this and the actual button to be visually equal 
            CuiElementContainer EventsUI = new CuiElementContainer();
            EventsUI.Add(new CuiButton
            {
                Button =
                {
                    Command = "",
                    Color = "0.39 0.18 0 1"
                },
                Text =
                {
                    Text = $"Demo: {FoundPlayerEvent.DemoFilename}\n[!!! SENT !!!]",
                    Color = "1 0.5 0 1",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0.025 0.700",
                    AnchorMax = "0.925 0.825"
                }
            }, "RTEventInfoPanel", "RTEventInfoDemo", "RTEventInfoDemo");
            BasePlayer FoundPlayer = Oxide.Game.Rust.RustCore.FindPlayerByIdString(player.Id); //this is dumb as shtt
            CuiHelper.AddUi(FoundPlayer, EventsUI);

            if (config.DiscordWebhookURL != "")
            {
                //todo, can NewEventReport be used for this as well?
                RustigateExtension.RustigateDiscordPost.UploadSimpleFileMessageAsync(new List<string>{ FoundPlayerEvent.DemoFilename }, FoundPlayerEvent.AttackerName, $"**Demofile requested ingame by {player.Name} [{player.Id}] for EventID: {FoundPlayerEvent.EventID}**", DiscordPostCallback);
            }
        }

        #endregion

        #endregion
    }
}
