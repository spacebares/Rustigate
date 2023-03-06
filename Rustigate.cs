#define DEBUGMODE

using Oxide.Core.Libraries.Covalence;
using System.ComponentModel;
using System.Runtime.InteropServices;
using UnityEngine.UIElements;
using System;
using System.Collections.Generic;
using ProtoBuf;
using Oxide.Core.Plugins;
using Oxide.Core;
using System.Linq;
using UnityEngine;
using Oxide.Core.Database;
using System.Text;
using Facepunch.Extend;
using MySql.Data.MySqlClient;
using static Facepunch.Tick.Entry;
using Newtonsoft.Json;

//todo: this plugin is going to run into disk space issues due to all the demo files:
//1. oxide currently has no way of knowing how much diskspace is free on the host
//2. even if somehow i can get the demos to be within oxides data folder, oxide has no way of deleting files
//therfor at somepoint the demofiles need to be in a YYYY/MM/DD folder structure
//this way admin has an easier time pruning old data by just using host filesystem.
//afterwards its up to the plugin during startup to recognize these files are missing and remove events from DB

namespace Oxide.Plugins
{
    [Info("Rustigate", "https://github.com/spacebares", "0.0.1")]
    [Description("Automatic demo recording of players when they attack others, with discord notifications for related player reports and an ingame event browser.")]
    class Rustigate : CovalencePlugin
    {
        Core.SQLite.Libraries.SQLite sqlLibrary = Interface.Oxide.GetLibrary<Core.SQLite.Libraries.SQLite>();
        Connection sqlConnection;

        private Int32 NextEventID = 0;

        #region Config

        //todo: actually make these config 

        /*events start the moment a player attacks someone, after MinEventSeconds the event is over and a demo is saved
        however each time the player attacks someone this timer is reset.*/
        private float MinEventSeconds = 2;
        /*regardless of how many times a player attacks others during an event, it will always end after MaxEventSeconds
         this prevents a malicious player from just attacking someone every <5min to delay the saving of a demo for review*/
        private float MaxEventSeconds = 10;

        private string DiscordWebhookURL = "REDACTED";

        #endregion

        ///we keep these in memory to avoid having to waste cpu talking with sqlite
        ///todo: this is for sure faster, but is it neccessary? its for sure more memory intensive
        private List<PlayerEvent> PlayerEvents = new List<PlayerEvent>();
        private Dictionary<ulong, Int32> PlayerActiveEventID = new Dictionary<ulong, Int32>();

        ///again is this neccessary? sql select magic can do this as well, these are only here for caching / speedup
        //this contains all the events a player has been involved with
        private Dictionary<ulong, List<Int32>> EventPlayers = new Dictionary<ulong, List<Int32>>();

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
             * we track all of them here to make searching for related player vs player events easier
             */
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

            public List<ulong> GetInvolvedPlayers()
            {
                List<ulong> InvolvedPlayers = new List<ulong>();
                InvolvedPlayers.Add(AttackerID);
                InvolvedPlayers.AddRange(EventVictims.Keys);

                return InvolvedPlayers;
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
            //todo: again, is this neccesary ? is this any faster ?
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

                        //the sql query above should have kept the eventids in order...
                        PlayerEvent NewEvent = new PlayerEvent(EventID, EventTime, AttackerPlayerName, AttackerPlayerID, DemoFilename);
                        PlayerEvents.Add(NewEvent);
                        NextEventID = EventID + 1;

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

        #endregion

        #region ChatCommands
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
        #endregion

        #region DiscordAPI

        class DiscordMessage
        {
            [JsonProperty("content")]
            public string MessageText { get; set; }
        }

        private void PostDiscordJson(string payloadJson)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers.Add("Content-Type", "application/json");

            webrequest.Enqueue(DiscordWebhookURL, payloadJson, (code, response) => DiscordPostCallBack(code, response), this, Core.Libraries.RequestMethod.POST, headers);
        }
        private void DiscordPostCallBack(int code, string response)
        {
            if (code != 200 || code != 204)
            {
                PrintWarning(String.Format("Discord Api responded with {0}: {1}", code, response));
            }
        }

        private void SendDiscordMessage(string TextMessage)
        {
            string payloadJson = String.Format("{'content'}:{'{0}'}", TextMessage);
            PostDiscordJson(payloadJson);
        }

        private void SendFancyDiscordMessage(string message)
        {
            string payloadJson = JsonConvert.SerializeObject(new DiscordMessage()
            {
                MessageText = message
            });

            PostDiscordJson(payloadJson);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            InitializeDB();
            LoadDBEvents();
        }

        private void Unload()
        {
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
                    Puts("stopping recording for " + player.displayName);
                }
            }

            sqlLibrary.CloseDb(sqlConnection);
        }

        void OnServerInitialized(bool initial)
        {
            for (int i = 0; i < 5; i++)
            {
                BaseEntity baseEntity = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", new Vector3(78.3f, 15.0f, -187.8f));
                if (baseEntity != null)
                {
                    baseEntity.Spawn();
                }
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                player.IPlayer.Teleport(new GenericPosition(78.3f, 15.0f, -187.8f));
            }

            DebugSay("loaded");
        }

        private void Loaded()
        {

        }

        object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker != null || info != null)
            {
                bool bIsSelfDamage = attacker == info.HitEntity?.ToPlayer();
                bool bHitEntityIsPlayer = info.HitEntity?.ToPlayer() != null;

                /*
                 * players that attack other players must be recorded
                 * this way if this player is ever reported by the victim player or their team -
                 * we know to raise an event in discord
                 */
                if (!bIsSelfDamage && bHitEntityIsPlayer)
                {
                    BasePlayer VictimPlayer = info.HitEntity.ToPlayer();
                    RecordPlayerEvent(attacker, VictimPlayer);

                    //as for the victim being the troublemaker:
                    //the victim can speed hack away and we would see it on the attacker's POV
                    //the victim can return fire and it will just run this function again with the roles reversed
                }
            }

            return null;
        }

        #endregion

        #region Rustigate
        private void DebugSay(string message)
        {
#if DEBUGMODE
            server.Command("say", (message));
#endif
        }

        private void RecordPlayerEvent(BasePlayer AttackerPlayer, BasePlayer VictimPlayer)
        {
            if (AttackerPlayer == null || VictimPlayer == null)
            {
                Puts("error generating event");
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
            Timer MinEventTimer = timer.Once(MinEventSeconds, () => EndPlayerEvent(EventID));
            Timer NaxEventTimer = timer.Once(MaxEventSeconds, () => EndPlayerEvent(EventID));

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

        private void EndPlayerEvent(Int32 EventID)
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

            //we save it to DB when the event ends to avoid too many updates during combat
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

            PlayerActiveEventID.Remove(playerEvent.AttackerID);
            DebugSay("event " + EventID + " for " + playerEvent.AttackerName + " has ended");
        }

        private void DeletePlayerEvent(Int32 EventID)
        {
            //i dont like this Find here but the events list can have holes in it as old ones get pruned
            //cant rely on eventID being the array index... this also means dictionaries are out of the question
            int FoundIDX = PlayerEvents.FindIndex(x => x.EventID == EventID);
            if (FoundIDX == -1)
                return;

            string sqlQuery = "DELETE FROM Events WHERE `EventID` = @0;";
            Sql deleteCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, EventID);
            sqlLibrary.ExecuteNonQuery(deleteCommand, sqlConnection);

            PlayerEvents.RemoveAt(FoundIDX);
        }

        #endregion
    }
}