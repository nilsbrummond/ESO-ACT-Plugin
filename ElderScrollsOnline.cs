using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;


[assembly: AssemblyTitle("Elder Scrolls Online Parsing Plugin")]
[assembly: AssemblyDescription("A basic parser that reads the combat logs in ESO.")]
[assembly: AssemblyCopyright("Nils Brummond (nils.brummond@gmail.com) 2014")]
[assembly: AssemblyVersion("0.0.0.1")]


namespace ESOParsing_Plugin
{
    public class ESO_Parser : ACTPluginBase, IActPluginV1
    {
        private Label lblStatus = null;
        private ESOUserControl userControl = null;

        private enum LogEntryTypes
        {
            logEntryType_UNKNOWN,

            logEntryType_PLAYER,
            logEntryType_COMBAT,
            logEntryType_EFFECT
        };

        private static readonly Dictionary<string, LogEntryTypes> logEntryTypes = new Dictionary<string, LogEntryTypes>()
        {
           { "PLYR", LogEntryTypes.logEntryType_PLAYER },
           { "CMBT", LogEntryTypes.logEntryType_COMBAT },
           { "EFFT", LogEntryTypes.logEntryType_EFFECT }
        };

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            // Setting this Regex will allow ACT to extract the character's name from the file name as the first capture group
            // when opening new log files. We'll say the log file name may look like "20080706-Player.log"
            ActGlobals.oFormActMain.LogPathHasCharName = false;

            // A windows file system filter to search updated log files with.
            ActGlobals.oFormActMain.LogFileFilter = "ChatLog.log";

            // If all log files are in a single folder, this isn't an issue. If log files are split into different folders,
            // enter the parent folder name here. This way ACT will monitor that base folder and all sub-folders for updated files.
            ActGlobals.oFormActMain.LogFileParentFolderName = "Logs";

            InitializeACTTables();


            // Setup timestamp parsing.
            ActGlobals.oFormActMain.TimeStampLen = 30;
            ActGlobals.oFormActMain.GetDateTimeFromLog = new FormActMain.DateTimeLogParser(ParseDateTime);

            // This Regex is only used by a quick parsing method to find the current zone name based on a file position
            // If you do not define this correctly, the quick parser will fail and take a while to do so.
            // You still need to implement a zone change parser in your engine regardless of this
            ActGlobals.oFormActMain.ZoneChangeRegex = new Regex(@"ZONE:[^:]+:([^:]+):", RegexOptions.Compiled);

            // All of your parsing engine will be based off of this event
            // You should not use Before/AfterCombatAction as you may enter infinite loops. AfterLogLineRead is okay, but not recommended
            ActGlobals.oFormActMain.BeforeLogLineRead += new LogLineEventDelegate(OnBeforeLogLineRead);

            // Hooks for extra info
            ActGlobals.oFormActMain.OnCombatEnd += new CombatToggleEventDelegate(OnCombatEnd);
            ActGlobals.oFormActMain.LogFileChanged += new LogFileChangedDelegate(OnLogFileChanged);
            // Set status text to successfully loaded
            lblStatus = pluginStatusText;
            lblStatus.Text = "Elder Scrolls Online ACT plugin loaded";
        }

        public void DeInitPlugin()
        {
            lblStatus.Text = "Elder Scrolls Online ACT plugin unloaded";
        }

        
        private void InitializeACTTables()
        {

        }

        private DateTime ParseDateTime(string FullLogLine)
        {
            // Timestamp format:
            // 2014-03-02T18:53:36.755-08:00 

            if (FullLogLine.Length >= 30 && FullLogLine[10] == 'T' && FullLogLine[19] == '.')
            {
                return DateTime.ParseExact(
                    FullLogLine.Substring(0, 29),
                    "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffzzz",
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return ActGlobals.oFormActMain.LastKnownTime;
        }

        private void OnLogFileChanged(bool IsImport, string NewLogFileName)
        {

        }

        private void OnCombatEnd(bool isImport, CombatToggleEventArgs encounterInfo)
        {

        }

        void OnBeforeLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            logInfo.detectedType = Color.Gray.ToArgb();

            string meat = logInfo.logLine.Substring(30);
            string[] segments = meat.Split(':');

            if (segments.Length < 2)
            {
                logInfo.detectedType = Color.DarkGray.ToArgb();
                return;
            }

            LogEntryTypes code = LogEntryTypes.logEntryType_UNKNOWN;
            if (logEntryTypes.TryGetValue(segments[0], out code))
            {
                switch (code)
                {
                    case LogEntryTypes.logEntryType_PLAYER:
                        ProcessPlayer(isImport, logInfo, segments);
                        break;
                    case LogEntryTypes.logEntryType_COMBAT:
                        ProcessCombat(logInfo, segments);
                        break;
                    case LogEntryTypes.logEntryType_EFFECT:
                        ProcessEffect(logInfo, segments);
                        break;
                }
            }
        }

        void ProcessPlayer(bool isImport, LogLineEventArgs logInfo, string[] segments)
        {
            if (segments.Length < 3) return;

            logInfo.detectedType = Color.Purple.ToArgb();

            if (segments[1].Length > 0) ActGlobals.charName = segments[1];

            if (!isImport)
            {
                if (segments[2].Length > 0)
                ActGlobals.oFormActMain.ChangeZone(segments[2]);
            }
        }

        void ProcessCombat(LogLineEventArgs logInfo, string[] segments)
        {
            if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, segments[7], segments[9]))
            {
                logInfo.detectedType = Color.Red.ToArgb();

                MasterSwing ms = new MasterSwing(
                                    (int)SwingTypeEnum.NonMelee,
                                    false,
                                    "",
                                    new Dnum(int.Parse(segments[10])),
                                    logInfo.detectedTime,
                                    ++ActGlobals.oFormActMain.GlobalTimeSorter,
                                    segments[3],
                                    segments[7],
                                    segments[12],
                                    segments[9]);


                ActGlobals.oFormActMain.AddCombatAction(ms);
            }  
        }

        void ProcessEffect(LogLineEventArgs logInfo, string[] segments)
        {
            logInfo.detectedType = Color.Blue.ToArgb();
        }
    }

    internal class ESOUserControl : UserControl
    {
    }

    // Stuff from the EQ2 plugin that is needed for clean setup.
    public class ACTPluginBase
    {
        protected string GetIntCommas()
        {
            return ActGlobals.mainTableShowCommas ? "#,0" : "0";
        }

        protected string GetFloatCommas()
        {
            return ActGlobals.mainTableShowCommas ? "#,0.00" : "0.00";
        }
    }
}