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

namespace Oxide.Plugins
{
    [Info("Rustigate", "https://github.com/spacebares", "0.0.1")]
    [Description("Automatic demo recording of players when they attack others, with discord notifications and ingame event browser.")]
    class Rustigate : CovalencePlugin
    {
        Core.SQLite.Libraries.SQLite sqlLibrary = Interface.Oxide.GetLibrary<Core.SQLite.Libraries.SQLite>();
        Connection sqlConnection;

        private float MinEventSeconds = 5;
        private float MaxEventSeconds = 300;

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
            public Timer RecordTimer;

            public PlayerEvent() { }
            public PlayerEvent(Int32 EventID, BasePlayer AttackerPlayer, BasePlayer VictimPlayer, string DemoFilename, Timer RecordTimer)
            {
                this.EventID = EventID;

                this.AttackerID = AttackerPlayer.userID;
                this.AttackerName = AttackerPlayer.displayName;
                this.AttackerPlayer = AttackerPlayer;
                this.AttackerInitialPosition = AttackerPlayer.ServerPosition;

                EventVictims.Add(VictimPlayer.userID, new EventVictimInfo(VictimPlayer));

                this.EventTime = DateTime.UtcNow;
                this.DemoFilename = DemoFilename;
                this.RecordTimer = RecordTimer;
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
                RecordTimer.Reset();
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
	            `VictimID`	INTEGER
            )"), sqlConnection);
        }

        private void LoadDBEvents()
        {
            //by loading DB events into memory we avoid having to interface with DB for every little thing
            //todo: again, is this neccesary ? is this any faster ?
            {
                string sqlQuery = "SELECT * FROM Events";
                Sql selectCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery);

                sqlLibrary.Query(selectCommand, sqlConnection, list =>
                {
                    if (list == null)
                    {
                        return; // Empty result or no records found
                    }

                    // Iterate through resulting records
                    foreach (var entry in list)
                    {
                        int EventID = Convert.ToInt32(entry["EventID"]);
                        DateTime EventTime = DateTime.Parse(Convert.ToString(entry["EventTime"]));
                        string AttackerPlayerName = Convert.ToString(entry["AttackerPlayerName"]);
                        ulong AttackerPlayerID = Convert.ToUInt64(entry["AttackerPlayerID"].ToString());
                        string DemoFilename = Convert.ToString(entry["DemoFilename"]);

                        PlayerEvent NewEvent = new PlayerEvent(EventID, EventTime, AttackerPlayerName, AttackerPlayerID, DemoFilename);
                        PlayerEvents.Insert(EventID, NewEvent);
                    }
                });
            }
        }

        #endregion

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
                player.IPlayer.Teleport(new GenericPosition(78.3f, 15.0f, -187.8f));

                //we do this again here cause if the plugin crashed, this will stop zombie recordings
                if (player.Connection.IsRecording)
                {
                    player.Connection.StopRecording();
                    Puts("stopping recording for " + player.displayName);
                }
            }

            sqlLibrary.CloseDb(sqlConnection);
        }

        private void Loaded()
        {
            BaseEntity baseEntity = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", new Vector3(78.3f, 15.0f, -187.8f));
            if (baseEntity != null)
            {
                baseEntity.Spawn();
            } 

            DebugSay("loaded");
        }

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
                if(PlayerEvents.Count > ActiveEventID)
                {
                    PlayerEvent PlayerEvent = PlayerEvents[ActiveEventID];
                    PlayerEvent.AddEventVictim(VictimPlayer);
                }

                return;
            }

            Int32 EventID = PlayerEvents.Count;

            //a player can only record one demo at a time, keep track of the event they have active
            PlayerActiveEventID.Add(AttackerPlayer.userID, EventID);

            //as of 3-2-2023 default filename for the demo is: "demos/{UserIDString}/{DateTime.Now:yyyy-MM-dd-hhmmss}.dem"
            AttackerPlayer.StartDemoRecording();
            string DemoFileName = AttackerPlayer.Connection.RecordFilename;

            //use a timer to turn the recording off automatically
            Timer EventTimer = timer.Once(MinEventSeconds, () => EndPlayerEvent(EventID));

            PlayerEvent NewEvent = new PlayerEvent(EventID, AttackerPlayer, VictimPlayer, DemoFileName, EventTimer);
            PlayerEvents.Add(NewEvent);

            DebugSay("new event for " + AttackerPlayer.displayName);
        }

        private void EndPlayerEvent(Int32 EventID)
        {
            ///let this error out for now if invalid, need to catch bugs
            //if(PlayerEvents.Count > EventID)
            {
                PlayerEvent playerEvent = PlayerEvents[EventID];
                playerEvent.AttackerPlayer.Connection.StopRecording();
                playerEvent.RecordTimer.Destroy();

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
                        string sqlQuery = "INSERT INTO Victims (`EventID`, `VictimName`, `VictimID`) VALUES (@0, @1, @2);";
                        Sql insertCommand = Oxide.Core.Database.Sql.Builder.Append(sqlQuery, playerEvent.EventID, VictimName, VictimID);
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
    }
}