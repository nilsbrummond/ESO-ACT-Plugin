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
[assembly: AssemblyDescription("A basic parser that reads combat logs in ESO.")]
[assembly: AssemblyCopyright("Nils Brummond (nils.brummond@gmail.com) 2014")]
[assembly: AssemblyVersion("0.0.0.3")]


// TODO:
// - Need stratagy for miss / avoid.
// - Interrupts?
// - Block aggregate stats


namespace ESOParsing_Plugin
{

    public class ESO_Parser : ACTPluginBase, IActPluginV1
    {
        private Label lblStatus = null;
        private ESOUserControl userControl = null;

        // NOTE: The values of "Unknown" and "UNKNOWN" short-circuit the ally determination code.  Must use one of these two names.
        //       Information from EQAditu.
        internal static string unk = "UNKNOWN", unkAbility = "Unknown Ability";

        private ReflectionTracker reflectionTracker = new ReflectionTracker();

        private CombatEvent damageShielded = null;

        public enum SwingTypeEnum
        {
            Melee = 1,
            NonMelee = 2,
            Healing = 3,
            // PowerDrain = 10,
            MagickaHealing = 13,
            // Threat = 16,
            StaminaHealing = 17,
            CureDispel = 20,
        }

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
            ActGlobals.oFormActMain.LogFileFilter = "*.log";

            // If all log files are in a single folder, this isn't an issue. If log files are split into different folders,
            // enter the parent folder name here. This way ACT will monitor that base folder and all sub-folders for updated files.
            ActGlobals.oFormActMain.LogFileParentFolderName = "Logs";

            InitializeACTTables();


            // Setup timestamp parsing.
            ActGlobals.oFormActMain.TimeStampLen = /* 30; */ 9;
            ActGlobals.oFormActMain.GetDateTimeFromLog = new FormActMain.DateTimeLogParser(ParseDateTime);

            // This Regex is only used by a quick parsing method to find the current zone name based on a file position
            // If you do not define this correctly, the quick parser will fail and take a while to do so.
            // You still need to implement a zone change parser in your engine regardless of this
            ActGlobals.oFormActMain.ZoneChangeRegex = new Regex(@"\*PLYR:[^:]+:([^:]+):", RegexOptions.Compiled);

            // Main parser hook.
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

        #region ACT_Tables_Setup
        private void InitializeACTTables()
        {
			CultureInfo usCulture = new CultureInfo("en-US");	// This is for SQL syntax; do not change

			EncounterData.ColumnDefs.Clear();
			//                                                                                      Do not change the SqlDataName while doing localization
			EncounterData.ColumnDefs.Add("EncId", new EncounterData.ColumnDef("EncId", false, "CHAR(8)", "EncId", (Data) => { return string.Empty; }, (Data) => { return Data.EncId; }));
			EncounterData.ColumnDefs.Add("Title", new EncounterData.ColumnDef("Title", true, "VARCHAR(64)", "Title", (Data) => { return Data.Title; }, (Data) => { return Data.Title; }));
			EncounterData.ColumnDefs.Add("StartTime", new EncounterData.ColumnDef("StartTime", true, "TIMESTAMP", "StartTime", (Data) => { return Data.StartTime == DateTime.MaxValue ? "--:--:--" : String.Format("{0} {1}", Data.StartTime.ToShortDateString(), Data.StartTime.ToLongTimeString()); }, (Data) => { return Data.StartTime == DateTime.MaxValue ? "0000-00-00 00:00:00" : Data.StartTime.ToString("u").TrimEnd(new char[] { 'Z' }); }));
			EncounterData.ColumnDefs.Add("EndTime", new EncounterData.ColumnDef("EndTime", true, "TIMESTAMP", "EndTime", (Data) => { return Data.EndTime == DateTime.MinValue ? "--:--:--" : Data.EndTime.ToString("T"); }, (Data) => { return Data.EndTime == DateTime.MinValue ? "0000-00-00 00:00:00" : Data.EndTime.ToString("u").TrimEnd(new char[] { 'Z' }); }));
			EncounterData.ColumnDefs.Add("Duration", new EncounterData.ColumnDef("Duration", true, "INT", "Duration", (Data) => { return Data.DurationS; }, (Data) => { return Data.Duration.TotalSeconds.ToString("0"); }));
			EncounterData.ColumnDefs.Add("Damage", new EncounterData.ColumnDef("Damage", true, "BIGINT", "Damage", (Data) => { return Data.Damage.ToString(GetIntCommas()); }, (Data) => { return Data.Damage.ToString(); }));
			EncounterData.ColumnDefs.Add("EncDPS", new EncounterData.ColumnDef("EncDPS", true, "DOUBLE", "EncDPS", (Data) => { return Data.DPS.ToString(GetFloatCommas()); }, (Data) => { return Data.DPS.ToString(usCulture); }));
			EncounterData.ColumnDefs.Add("Zone", new EncounterData.ColumnDef("Zone", false, "VARCHAR(64)", "Zone", (Data) => { return Data.ZoneName; }, (Data) => { return Data.ZoneName; }));
			EncounterData.ColumnDefs.Add("Kills", new EncounterData.ColumnDef("Kills", true, "INT", "Kills", (Data) => { return Data.AlliedKills.ToString(GetIntCommas()); }, (Data) => { return Data.AlliedKills.ToString(); }));
			EncounterData.ColumnDefs.Add("Deaths", new EncounterData.ColumnDef("Deaths", true, "INT", "Deaths", (Data) => { return Data.AlliedDeaths.ToString(); }, (Data) => { return Data.AlliedDeaths.ToString(); }));

			EncounterData.ExportVariables.Clear();
			EncounterData.ExportVariables.Add("n", new EncounterData.TextExportFormatter("n", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-newline"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-newline"].DisplayedText, (Data, SelectiveAllies, Extra) => { return "\n"; }));
			EncounterData.ExportVariables.Add("t", new EncounterData.TextExportFormatter("t", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-tab"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-tab"].DisplayedText, (Data, SelectiveAllies, Extra) => { return "\t"; }));
			EncounterData.ExportVariables.Add("title", new EncounterData.TextExportFormatter("title", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-title"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-title"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "title", Extra); }));
			EncounterData.ExportVariables.Add("duration", new EncounterData.TextExportFormatter("duration", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-duration"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-duration"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "duration", Extra); }));
			EncounterData.ExportVariables.Add("DURATION", new EncounterData.TextExportFormatter("DURATION", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-DURATION"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-DURATION"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "DURATION", Extra); }));
			EncounterData.ExportVariables.Add("damage", new EncounterData.TextExportFormatter("damage", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-damage"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-damage"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "damage", Extra); }));
			EncounterData.ExportVariables.Add("damage-m", new EncounterData.TextExportFormatter("damage-m", "Damage M", "Damage divided by 1,000,000 (with two decimal places)", (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "damage-m", Extra); }));
			EncounterData.ExportVariables.Add("DAMAGE-k", new EncounterData.TextExportFormatter("DAMAGE-k", "Short Damage K", "Damage divided by 1,000 (with no decimal places)", (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "DAMAGE-k", Extra); }));
			EncounterData.ExportVariables.Add("DAMAGE-m", new EncounterData.TextExportFormatter("DAMAGE-m", "Short Damage M", "Damage divided by 1,000,000 (with no decimal places)", (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "DAMAGE-m", Extra); }));
			EncounterData.ExportVariables.Add("dps", new EncounterData.TextExportFormatter("dps", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-dps"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-dps"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "dps", Extra); }));
			EncounterData.ExportVariables.Add("DPS", new EncounterData.TextExportFormatter("DPS", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-DPS"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-DPS"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "DPS", Extra); }));
			EncounterData.ExportVariables.Add("DPS-k", new EncounterData.TextExportFormatter("DPS-k", "DPS K", "DPS divided by 1,000 (with no decimal places)", (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "DPS-k", Extra); }));
			EncounterData.ExportVariables.Add("encdps", new EncounterData.TextExportFormatter("encdps", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-extdps"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-extdps"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "encdps", Extra); }));
			EncounterData.ExportVariables.Add("ENCDPS", new EncounterData.TextExportFormatter("ENCDPS", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-EXTDPS"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-EXTDPS"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "ENCDPS", Extra); }));
			EncounterData.ExportVariables.Add("ENCDPS-k", new EncounterData.TextExportFormatter("ENCDPS-k", "Short DPS K", "ENCDPS divided by 1,000 (with no decimal places)", (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "ENCDPS-k", Extra); }));
			EncounterData.ExportVariables.Add("hits", new EncounterData.TextExportFormatter("hits", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-hits"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-hits"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "hits", Extra); }));
			EncounterData.ExportVariables.Add("crithits", new EncounterData.TextExportFormatter("crithits", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-crithits"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-crithits"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "crithits", Extra); }));
			EncounterData.ExportVariables.Add("crithit%", new EncounterData.TextExportFormatter("crithit%", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-crithit%"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-crithit%"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "crithit%", Extra); }));
			EncounterData.ExportVariables.Add("misses", new EncounterData.TextExportFormatter("misses", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-misses"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-misses"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "misses", Extra); }));
			EncounterData.ExportVariables.Add("hitfailed", new EncounterData.TextExportFormatter("hitfailed", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-hitfailed"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-hitfailed"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "hitfailed", Extra); }));
			EncounterData.ExportVariables.Add("swings", new EncounterData.TextExportFormatter("swings", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-swings"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-swings"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "swings", Extra); }));
			EncounterData.ExportVariables.Add("tohit", new EncounterData.TextExportFormatter("tohit", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-tohit"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-tohit"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "tohit", Extra); }));
			EncounterData.ExportVariables.Add("TOHIT", new EncounterData.TextExportFormatter("TOHIT", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-TOHIT"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-TOHIT"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "TOHIT", Extra); }));
			EncounterData.ExportVariables.Add("maxhit", new EncounterData.TextExportFormatter("maxhit", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-maxhit"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-maxhit"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "maxhit", Extra); }));
			EncounterData.ExportVariables.Add("MAXHIT", new EncounterData.TextExportFormatter("MAXHIT", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-MAXHIT"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-MAXHIT"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "MAXHIT", Extra); }));
			EncounterData.ExportVariables.Add("healed", new EncounterData.TextExportFormatter("healed", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-healed"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-healed"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "healed", Extra); }));
			EncounterData.ExportVariables.Add("enchps", new EncounterData.TextExportFormatter("enchps", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-exthps"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-exthps"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "enchps", Extra); }));
			EncounterData.ExportVariables.Add("ENCHPS", new EncounterData.TextExportFormatter("ENCHPS", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-EXTHPS"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-EXTHPS"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "ENCHPS", Extra); }));
			EncounterData.ExportVariables.Add("ENCHPS-k", new EncounterData.TextExportFormatter("ENCHPS", "Short ENCHPS K", "ENCHPS divided by 1,000 (with no decimal places)", (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "ENCHPS-k", Extra); }));
			EncounterData.ExportVariables.Add("critheals", new EncounterData.TextExportFormatter("critheals", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-critheals"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-critheals"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "critheals", Extra); }));
			EncounterData.ExportVariables.Add("critheal%", new EncounterData.TextExportFormatter("critheal%", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-critheal%"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-critheal%"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "critheal%", Extra); }));
			EncounterData.ExportVariables.Add("heals", new EncounterData.TextExportFormatter("heals", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-heals"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-heals"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "heals", Extra); }));
			EncounterData.ExportVariables.Add("cures", new EncounterData.TextExportFormatter("cures", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-cures"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-cures"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "cures", Extra); }));
			EncounterData.ExportVariables.Add("maxheal", new EncounterData.TextExportFormatter("maxheal", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-maxheal"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-maxheal"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "maxheal", Extra); }));
			EncounterData.ExportVariables.Add("MAXHEAL", new EncounterData.TextExportFormatter("MAXHEAL", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-MAXHEAL"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-MAXHEAL"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "MAXHEAL", Extra); }));
			EncounterData.ExportVariables.Add("maxhealward", new EncounterData.TextExportFormatter("maxhealward", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-maxhealward"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-maxhealward"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "maxhealward", Extra); }));
			EncounterData.ExportVariables.Add("MAXHEALWARD", new EncounterData.TextExportFormatter("MAXHEALWARD", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-MAXHEALWARD"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-MAXHEALWARD"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "MAXHEALWARD", Extra); }));
			EncounterData.ExportVariables.Add("damagetaken", new EncounterData.TextExportFormatter("damagetaken", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-damagetaken"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-damagetaken"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "damagetaken", Extra); }));
			EncounterData.ExportVariables.Add("healstaken", new EncounterData.TextExportFormatter("healstaken", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-healstaken"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-healstaken"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "healstaken", Extra); }));
//			EncounterData.ExportVariables.Add("powerdrain", new EncounterData.TextExportFormatter("powerdrain", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-powerdrain"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-powerdrain"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "powerdrain", Extra); }));
			EncounterData.ExportVariables.Add("powerheal", new EncounterData.TextExportFormatter("powerheal", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-powerheal"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-powerheal"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "powerheal", Extra); }));
			EncounterData.ExportVariables.Add("kills", new EncounterData.TextExportFormatter("kills", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-kills"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-kills"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "kills", Extra); }));
			EncounterData.ExportVariables.Add("deaths", new EncounterData.TextExportFormatter("deaths", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-deaths"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-deaths"].DisplayedText, (Data, SelectiveAllies, Extra) => { return EncounterFormatSwitch(Data, SelectiveAllies, "deaths", Extra); }));

			CombatantData.ColumnDefs.Clear();
			CombatantData.ColumnDefs.Add("EncId", new CombatantData.ColumnDef("EncId", false, "CHAR(8)", "EncId", (Data) => { return string.Empty; }, (Data) => { return Data.Parent.EncId; }, (Left, Right) => { return 0; }));
			CombatantData.ColumnDefs.Add("Ally", new CombatantData.ColumnDef("Ally", false, "CHAR(1)", "Ally", (Data) => { return Data.Parent.GetAllies().Contains(Data).ToString(); }, (Data) => { return Data.Parent.GetAllies().Contains(Data) ? "T" : "F"; }, (Left, Right) => { return Left.Parent.GetAllies().Contains(Left).CompareTo(Right.Parent.GetAllies().Contains(Right)); }));
			CombatantData.ColumnDefs.Add("Name", new CombatantData.ColumnDef("Name", true, "VARCHAR(64)", "Name", (Data) => { return Data.Name; }, (Data) => { return Data.Name; }, (Left, Right) => { return Left.Name.CompareTo(Right.Name); }));
			CombatantData.ColumnDefs.Add("StartTime", new CombatantData.ColumnDef("StartTime", true, "TIMESTAMP", "StartTime", (Data) => { return Data.StartTime == DateTime.MaxValue ? "--:--:--" : Data.StartTime.ToString("T"); }, (Data) => { return Data.StartTime == DateTime.MaxValue ? "0000-00-00 00:00:00" : Data.StartTime.ToString("u").TrimEnd(new char[] { 'Z' }); }, (Left, Right) => { return Left.StartTime.CompareTo(Right.StartTime); }));
			CombatantData.ColumnDefs.Add("EndTime", new CombatantData.ColumnDef("EndTime", false, "TIMESTAMP", "EndTime", (Data) => { return Data.EndTime == DateTime.MinValue ? "--:--:--" : Data.StartTime.ToString("T"); }, (Data) => { return Data.EndTime == DateTime.MinValue ? "0000-00-00 00:00:00" : Data.EndTime.ToString("u").TrimEnd(new char[] { 'Z' }); }, (Left, Right) => { return Left.EndTime.CompareTo(Right.EndTime); }));
			CombatantData.ColumnDefs.Add("Duration", new CombatantData.ColumnDef("Duration", true, "INT", "Duration", (Data) => { return Data.DurationS; }, (Data) => { return Data.Duration.TotalSeconds.ToString("0"); }, (Left, Right) => { return Left.Duration.CompareTo(Right.Duration); }));
			CombatantData.ColumnDefs.Add("Damage", new CombatantData.ColumnDef("Damage", true, "BIGINT", "Damage", (Data) => { return Data.Damage.ToString(GetIntCommas()); }, (Data) => { return Data.Damage.ToString(); }, (Left, Right) => { return Left.Damage.CompareTo(Right.Damage); }));
			CombatantData.ColumnDefs.Add("Damage%", new CombatantData.ColumnDef("Damage%", true, "VARCHAR(4)", "DamagePerc", (Data) => { return Data.DamagePercent; }, (Data) => { return Data.DamagePercent; }, (Left, Right) => { return Left.Damage.CompareTo(Right.Damage); }));
			CombatantData.ColumnDefs.Add("Kills", new CombatantData.ColumnDef("Kills", false, "INT", "Kills", (Data) => { return Data.Kills.ToString(GetIntCommas()); }, (Data) => { return Data.Kills.ToString(); }, (Left, Right) => { return Left.Kills.CompareTo(Right.Kills); }));
			CombatantData.ColumnDefs.Add("Healed", new CombatantData.ColumnDef("Healed", false, "BIGINT", "Healed", (Data) => { return Data.Healed.ToString(GetIntCommas()); }, (Data) => { return Data.Healed.ToString(); }, (Left, Right) => { return Left.Healed.CompareTo(Right.Healed); }));
			CombatantData.ColumnDefs.Add("Healed%", new CombatantData.ColumnDef("Healed%", false, "VARCHAR(4)", "HealedPerc", (Data) => { return Data.HealedPercent; }, (Data) => { return Data.HealedPercent; }, (Left, Right) => { return Left.Healed.CompareTo(Right.Healed); }));
			CombatantData.ColumnDefs.Add("CritHeals", new CombatantData.ColumnDef("CritHeals", false, "INT", "CritHeals", (Data) => { return Data.CritHeals.ToString(GetIntCommas()); }, (Data) => { return Data.CritHeals.ToString(); }, (Left, Right) => { return Left.CritHeals.CompareTo(Right.CritHeals); }));
			CombatantData.ColumnDefs.Add("Heals", new CombatantData.ColumnDef("Heals", false, "INT", "Heals", (Data) => { return Data.Heals.ToString(GetIntCommas()); }, (Data) => { return Data.Heals.ToString(); }, (Left, Right) => { return Left.Heals.CompareTo(Right.Heals); }));
			CombatantData.ColumnDefs.Add("Cures", new CombatantData.ColumnDef("Cures", false, "INT", "CureDispels", (Data) => { return Data.CureDispels.ToString(GetIntCommas()); }, (Data) => { return Data.CureDispels.ToString(); }, (Left, Right) => { return Left.CureDispels.CompareTo(Right.CureDispels); }));
//			CombatantData.ColumnDefs.Add("PowerDrain", new CombatantData.ColumnDef("PowerDrain", true, "BIGINT", "PowerDrain", (Data) => { return Data.PowerDamage.ToString(GetIntCommas()); }, (Data) => { return Data.PowerDamage.ToString(); }, (Left, Right) => { return Left.PowerDamage.CompareTo(Right.PowerDamage); }));
			CombatantData.ColumnDefs.Add("PowerReplenish", new CombatantData.ColumnDef("PowerReplenish", false, "BIGINT", "PowerReplenish", (Data) => { return Data.PowerReplenish.ToString(GetIntCommas()); }, (Data) => { return Data.PowerReplenish.ToString(); }, (Left, Right) => { return Left.PowerReplenish.CompareTo(Right.PowerReplenish); }));
			CombatantData.ColumnDefs.Add("DPS", new CombatantData.ColumnDef("DPS", false, "DOUBLE", "DPS", (Data) => { return Data.DPS.ToString(GetFloatCommas()); }, (Data) => { return Data.DPS.ToString(usCulture); }, (Left, Right) => { return Left.DPS.CompareTo(Right.DPS); }));
			CombatantData.ColumnDefs.Add("EncDPS", new CombatantData.ColumnDef("EncDPS", true, "DOUBLE", "EncDPS", (Data) => { return Data.EncDPS.ToString(GetFloatCommas()); }, (Data) => { return Data.EncDPS.ToString(usCulture); }, (Left, Right) => { return Left.Damage.CompareTo(Right.Damage); }));
			CombatantData.ColumnDefs.Add("EncHPS", new CombatantData.ColumnDef("EncHPS", true, "DOUBLE", "EncHPS", (Data) => { return Data.EncHPS.ToString(GetFloatCommas()); }, (Data) => { return Data.EncHPS.ToString(usCulture); }, (Left, Right) => { return Left.Healed.CompareTo(Right.Healed); }));
			CombatantData.ColumnDefs.Add("Hits", new CombatantData.ColumnDef("Hits", false, "INT", "Hits", (Data) => { return Data.Hits.ToString(GetIntCommas()); }, (Data) => { return Data.Hits.ToString(); }, (Left, Right) => { return Left.Hits.CompareTo(Right.Hits); }));
			CombatantData.ColumnDefs.Add("CritHits", new CombatantData.ColumnDef("CritHits", false, "INT", "CritHits", (Data) => { return Data.CritHits.ToString(GetIntCommas()); }, (Data) => { return Data.CritHits.ToString(); }, (Left, Right) => { return Left.CritHits.CompareTo(Right.CritHits); }));
			CombatantData.ColumnDefs.Add("Avoids", new CombatantData.ColumnDef("Avoids", false, "INT", "Blocked", (Data) => { return Data.Blocked.ToString(GetIntCommas()); }, (Data) => { return Data.Blocked.ToString(); }, (Left, Right) => { return Left.Blocked.CompareTo(Right.Blocked); }));
			CombatantData.ColumnDefs.Add("Misses", new CombatantData.ColumnDef("Misses", false, "INT", "Misses", (Data) => { return Data.Misses.ToString(GetIntCommas()); }, (Data) => { return Data.Misses.ToString(); }, (Left, Right) => { return Left.Misses.CompareTo(Right.Misses); }));
			CombatantData.ColumnDefs.Add("Swings", new CombatantData.ColumnDef("Swings", false, "INT", "Swings", (Data) => { return Data.Swings.ToString(GetIntCommas()); }, (Data) => { return Data.Swings.ToString(); }, (Left, Right) => { return Left.Swings.CompareTo(Right.Swings); }));
			CombatantData.ColumnDefs.Add("HealingTaken", new CombatantData.ColumnDef("HealingTaken", false, "BIGINT", "HealsTaken", (Data) => { return Data.HealsTaken.ToString(GetIntCommas()); }, (Data) => { return Data.HealsTaken.ToString(); }, (Left, Right) => { return Left.HealsTaken.CompareTo(Right.HealsTaken); }));
			CombatantData.ColumnDefs.Add("DamageTaken", new CombatantData.ColumnDef("DamageTaken", true, "BIGINT", "DamageTaken", (Data) => { return Data.DamageTaken.ToString(GetIntCommas()); }, (Data) => { return Data.DamageTaken.ToString(); }, (Left, Right) => { return Left.DamageTaken.CompareTo(Right.DamageTaken); }));
			CombatantData.ColumnDefs.Add("Deaths", new CombatantData.ColumnDef("Deaths", true, "INT", "Deaths", (Data) => { return Data.Deaths.ToString(GetIntCommas()); }, (Data) => { return Data.Deaths.ToString(); }, (Left, Right) => { return Left.Deaths.CompareTo(Right.Deaths); }));
			CombatantData.ColumnDefs.Add("ToHit%", new CombatantData.ColumnDef("ToHit%", false, "FLOAT", "ToHit", (Data) => { return Data.ToHit.ToString(GetFloatCommas()); }, (Data) => { return Data.ToHit.ToString(usCulture); }, (Left, Right) => { return Left.ToHit.CompareTo(Right.ToHit); }));
			CombatantData.ColumnDefs.Add("FCritHit%", new CombatantData.ColumnDef("FCritHit%", true, "VARCHAR(8)", "FCritHitPerc", (Data) => { return GetFilteredCritChance(Data).ToString("0'%"); }, (Data) => { return GetFilteredCritChance(Data).ToString("0'%"); }, (Left, Right) => { return GetFilteredCritChance(Left).CompareTo(GetFilteredCritChance(Right)); }));
			CombatantData.ColumnDefs.Add("CritDam%", new CombatantData.ColumnDef("CritDam%", false, "VARCHAR(8)", "CritDamPerc", (Data) => { return Data.CritDamPerc.ToString("0'%"); }, (Data) => { return Data.CritDamPerc.ToString("0'%"); }, (Left, Right) => { return Left.CritDamPerc.CompareTo(Right.CritDamPerc); }));
			CombatantData.ColumnDefs.Add("CritHeal%", new CombatantData.ColumnDef("CritHeal%", false, "VARCHAR(8)", "CritHealPerc", (Data) => { return Data.CritHealPerc.ToString("0'%"); }, (Data) => { return Data.CritHealPerc.ToString("0'%"); }, (Left, Right) => { return Left.CritHealPerc.CompareTo(Right.CritHealPerc); }));
//			CombatantData.ColumnDefs.Add("Threat +/-", new CombatantData.ColumnDef("Threat +/-", false, "VARCHAR(32)", "ThreatStr", (Data) => { return Data.GetThreatStr("Threat (Out)"); }, (Data) => { return Data.GetThreatStr("Threat (Out)"); }, (Left, Right) => { return Left.GetThreatDelta("Threat (Out)").CompareTo(Right.GetThreatDelta("Threat (Out)")); }));
//			CombatantData.ColumnDefs.Add("ThreatDelta", new CombatantData.ColumnDef("ThreatDelta", false, "INT", "ThreatDelta", (Data) => { return Data.GetThreatDelta("Threat (Out)").ToString(GetIntCommas()); }, (Data) => { return Data.GetThreatDelta("Threat (Out)").ToString(); }, (Left, Right) => { return Left.GetThreatDelta("Threat (Out)").CompareTo(Right.GetThreatDelta("Threat (Out)")); }));

			CombatantData.ColumnDefs["Damage"].GetCellForeColor = (Data) => { return Color.DarkRed; };
			CombatantData.ColumnDefs["Damage%"].GetCellForeColor = (Data) => { return Color.DarkRed; };
			CombatantData.ColumnDefs["Healed"].GetCellForeColor = (Data) => { return Color.DarkBlue; };
			CombatantData.ColumnDefs["Healed%"].GetCellForeColor = (Data) => { return Color.DarkBlue; };
//			CombatantData.ColumnDefs["PowerDrain"].GetCellForeColor = (Data) => { return Color.DarkMagenta; };
			CombatantData.ColumnDefs["DPS"].GetCellForeColor = (Data) => { return Color.DarkRed; };
			CombatantData.ColumnDefs["EncDPS"].GetCellForeColor = (Data) => { return Color.DarkRed; };
			CombatantData.ColumnDefs["EncHPS"].GetCellForeColor = (Data) => { return Color.DarkBlue; };
			CombatantData.ColumnDefs["DamageTaken"].GetCellForeColor = (Data) => { return Color.DarkOrange; };

			CombatantData.OutgoingDamageTypeDataObjects = new Dictionary<string, CombatantData.DamageTypeDef>
		{
//			{"Auto-Attack (Out)", new CombatantData.DamageTypeDef("Auto-Attack (Out)", -1, Color.DarkGoldenrod)},
//			{"Skill/Ability (Out)", new CombatantData.DamageTypeDef("Skill/Ability (Out)", -1, Color.DarkOrange)},
			{"Damage (Out)", new CombatantData.DamageTypeDef("Damage (Out)", 0, Color.Orange)},
			{"Healed (Out)", new CombatantData.DamageTypeDef("Healed (Out)", 1, Color.Blue)},
//			{"Magicka Drain (Out)", new CombatantData.DamageTypeDef("Power Drain (Out)", -1, Color.Purple)},
			{"Magicka Replenish (Out)", new CombatantData.DamageTypeDef("Power Replenish (Out)", 1, Color.Violet)},
//            {"Stamina Drain (Out)", new CombatantData.DamageTypeDef("Power Drain (Out)", -1, Color.Purple)},
			{"Stamina Replenish (Out)", new CombatantData.DamageTypeDef("Power Replenish (Out)", 1, Color.Violet)},
			{"Cure/Dispel (Out)", new CombatantData.DamageTypeDef("Cure/Dispel (Out)", 0, Color.Wheat)},
//			{"Threat (Out)", new CombatantData.DamageTypeDef("Threat (Out)", -1, Color.Yellow)},
			{"All Outgoing (Ref)", new CombatantData.DamageTypeDef("All Outgoing (Ref)", 0, Color.Black)}
		};
			CombatantData.IncomingDamageTypeDataObjects = new Dictionary<string, CombatantData.DamageTypeDef>
		{
			{"Damage (Inc)", new CombatantData.DamageTypeDef("Damage (Inc)", -1, Color.Red)},
			{"Healed (Inc)",new CombatantData.DamageTypeDef("Healed (Inc)", 1, Color.LimeGreen)},
//			{"Magicka Drain (Inc)",new CombatantData.DamageTypeDef("Power Drain (Inc)", -1, Color.Magenta)},
			{"Magicka Replenish (Inc)",new CombatantData.DamageTypeDef("Power Replenish (Inc)", 1, Color.MediumPurple)},
//          {"Stamina Drain (Inc)",new CombatantData.DamageTypeDef("Power Drain (Inc)", -1, Color.Magenta)},
			{"Stamina Replenish (Inc)",new CombatantData.DamageTypeDef("Power Replenish (Inc)", 1, Color.MediumPurple)},
			{"Cure/Dispel (Inc)", new CombatantData.DamageTypeDef("Cure/Dispel (Inc)", 0, Color.Wheat)},
//			{"Threat (Inc)",new CombatantData.DamageTypeDef("Threat (Inc)", -1, Color.Yellow)},
			{"All Incoming (Ref)",new CombatantData.DamageTypeDef("All Incoming (Ref)", 0, Color.Black)}
		};
			CombatantData.SwingTypeToDamageTypeDataLinksOutgoing = new SortedDictionary<int, List<string>>
		{
			{1, new List<string> { "Damage (Out)" } },
			{2, new List<string> { "Damage (Out)" } },
			{3, new List<string> { "Healed (Out)" } },
//			{10, new List<string> { "Magicka Drain (Out)" } },
			{13, new List<string> { "Magicka Replenish (Out)" } },
            {17, new List<string> { "Stamina Replenish (Out)" } },
			{20, new List<string> { "Cure/Dispel (Out)" } },
//			{16, new List<string> { "Threat (Out)" } }
		};
			CombatantData.SwingTypeToDamageTypeDataLinksIncoming = new SortedDictionary<int, List<string>>
		{
			{1, new List<string> { "Damage (Inc)" } },
			{2, new List<string> { "Damage (Inc)" } },
			{3, new List<string> { "Healed (Inc)" } },
//			{10, new List<string> { "Magicka Drain (Inc)" } },
			{13, new List<string> { "Magicka Replenish (Inc)" } },
            {17, new List<string> { "Stamina Replenish (Inc)" } },
			{20, new List<string> { "Cure/Dispel (Inc)" } },
//			{16, new List<string> { "Threat (Inc)" } }
		};

			CombatantData.DamageSwingTypes = new List<int> { 1, 2 };
			CombatantData.HealingSwingTypes = new List<int> { 3 };

			CombatantData.DamageTypeDataNonSkillDamage = "Damage (Out)";
			CombatantData.DamageTypeDataOutgoingDamage = "Damage (Out)";
			CombatantData.DamageTypeDataOutgoingHealing = "Healed (Out)";
			CombatantData.DamageTypeDataIncomingDamage = "Damage (Inc)";
			CombatantData.DamageTypeDataIncomingHealing = "Healed (Inc)";

			CombatantData.ExportVariables.Clear();
			CombatantData.ExportVariables.Add("n", new CombatantData.TextExportFormatter("n", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-newline"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-newline"].DisplayedText, (Data, Extra) => { return "\n"; }));
			CombatantData.ExportVariables.Add("t", new CombatantData.TextExportFormatter("t", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-tab"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-tab"].DisplayedText, (Data, Extra) => { return "\t"; }));
			CombatantData.ExportVariables.Add("name", new CombatantData.TextExportFormatter("name", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-name"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-name"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "name", Extra); }));
			CombatantData.ExportVariables.Add("NAME", new CombatantData.TextExportFormatter("NAME", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME", Extra); }));
			CombatantData.ExportVariables.Add("duration", new CombatantData.TextExportFormatter("duration", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-duration"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-duration"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "duration", Extra); }));
			CombatantData.ExportVariables.Add("DURATION", new CombatantData.TextExportFormatter("DURATION", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-DURATION"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-DURATION"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "DURATION", Extra); }));
			CombatantData.ExportVariables.Add("damage", new CombatantData.TextExportFormatter("damage", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-damage"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-damage"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "damage", Extra); }));
			CombatantData.ExportVariables.Add("damage-m", new CombatantData.TextExportFormatter("damage-m", "Damage M", "Damage divided by 1,000,000 (with two decimal places)", (Data, Extra) => { return CombatantFormatSwitch(Data, "damage-m", Extra); }));
			CombatantData.ExportVariables.Add("DAMAGE-k", new CombatantData.TextExportFormatter("DAMAGE-k", "Short Damage K", "Damage divided by 1,000 (with no decimal places)", (Data, Extra) => { return CombatantFormatSwitch(Data, "DAMAGE-k", Extra); }));
			CombatantData.ExportVariables.Add("DAMAGE-m", new CombatantData.TextExportFormatter("DAMAGE-m", "Short Damage M", "Damage divided by 1,000,000 (with no decimal places)", (Data, Extra) => { return CombatantFormatSwitch(Data, "DAMAGE-m", Extra); }));
			CombatantData.ExportVariables.Add("damage%", new CombatantData.TextExportFormatter("damage%", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-damage%"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-damage%"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "damage%", Extra); }));
			CombatantData.ExportVariables.Add("dps", new CombatantData.TextExportFormatter("dps", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-dps"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-dps"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "dps", Extra); }));
			CombatantData.ExportVariables.Add("DPS", new CombatantData.TextExportFormatter("DPS", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-DPS"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-DPS"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "DPS", Extra); }));
			CombatantData.ExportVariables.Add("DPS-k", new CombatantData.TextExportFormatter("DPS-k", "Short DPS K", "Short DPS divided by 1,000 (with no decimal places)", (Data, Extra) => { return CombatantFormatSwitch(Data, "DPS-k", Extra); }));
			CombatantData.ExportVariables.Add("encdps", new CombatantData.TextExportFormatter("encdps", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-extdps"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-extdps"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "encdps", Extra); }));
			CombatantData.ExportVariables.Add("ENCDPS", new CombatantData.TextExportFormatter("ENCDPS", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-EXTDPS"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-EXTDPS"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "ENCDPS", Extra); }));
			CombatantData.ExportVariables.Add("ENCDPS-k", new CombatantData.TextExportFormatter("ENCDPS-k", "Short Encounter DPS K", "Short Encounter DPS divided by 1,000 (with no decimal places)", (Data, Extra) => { return CombatantFormatSwitch(Data, "ENCDPS-k", Extra); }));
			CombatantData.ExportVariables.Add("hits", new CombatantData.TextExportFormatter("hits", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-hits"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-hits"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "hits", Extra); }));
			CombatantData.ExportVariables.Add("crithits", new CombatantData.TextExportFormatter("crithits", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-crithits"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-crithits"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "crithits", Extra); }));
			CombatantData.ExportVariables.Add("crithit%", new CombatantData.TextExportFormatter("crithit%", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-crithit%"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-crithit%"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "crithit%", Extra); }));
			CombatantData.ExportVariables.Add("fcrithit%", new CombatantData.TextExportFormatter("fcrithit%", "Filtered Critical Hit Chance", "Critical Hit Chance filtered against AttackTypes that have the ability to critically hit.", (Data, Extra) => { return CombatantFormatSwitch(Data, "fcrithit%", Extra); }));
			CombatantData.ExportVariables.Add("misses", new CombatantData.TextExportFormatter("misses", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-misses"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-misses"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "misses", Extra); }));
			CombatantData.ExportVariables.Add("hitfailed", new CombatantData.TextExportFormatter("hitfailed", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-hitfailed"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-hitfailed"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "hitfailed", Extra); }));
			CombatantData.ExportVariables.Add("swings", new CombatantData.TextExportFormatter("swings", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-swings"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-swings"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "swings", Extra); }));
			CombatantData.ExportVariables.Add("tohit", new CombatantData.TextExportFormatter("tohit", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-tohit"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-tohit"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "tohit", Extra); }));
			CombatantData.ExportVariables.Add("TOHIT", new CombatantData.TextExportFormatter("TOHIT", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-TOHIT"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-TOHIT"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "TOHIT", Extra); }));
			CombatantData.ExportVariables.Add("maxhit", new CombatantData.TextExportFormatter("maxhit", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-maxhit"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-maxhit"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "maxhit", Extra); }));
			CombatantData.ExportVariables.Add("MAXHIT", new CombatantData.TextExportFormatter("MAXHIT", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-MAXHIT"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-MAXHIT"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "MAXHIT", Extra); }));
			CombatantData.ExportVariables.Add("healed", new CombatantData.TextExportFormatter("healed", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-healed"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-healed"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "healed", Extra); }));
			CombatantData.ExportVariables.Add("healed%", new CombatantData.TextExportFormatter("healed%", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-healed%"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-healed%"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "healed%", Extra); }));
			CombatantData.ExportVariables.Add("enchps", new CombatantData.TextExportFormatter("enchps", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-exthps"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-exthps"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "enchps", Extra); }));
			CombatantData.ExportVariables.Add("ENCHPS", new CombatantData.TextExportFormatter("ENCHPS", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-EXTHPS"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-EXTHPS"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "ENCHPS", Extra); }));
			CombatantData.ExportVariables.Add("ENCHPS-k", new CombatantData.TextExportFormatter("ENCHPS-k", "Short Encounter HPS K", "Short Encounter HPS divided by 1,000 (with no decimal places)", (Data, Extra) => { return CombatantFormatSwitch(Data, "ENCHPS-k", Extra); }));
			CombatantData.ExportVariables.Add("critheals", new CombatantData.TextExportFormatter("critheals", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-critheals"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-critheals"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "critheals", Extra); }));
			CombatantData.ExportVariables.Add("critheal%", new CombatantData.TextExportFormatter("critheal%", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-critheal%"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-critheal%"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "critheal%", Extra); }));
			CombatantData.ExportVariables.Add("heals", new CombatantData.TextExportFormatter("heals", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-heals"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-heals"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "heals", Extra); }));
			CombatantData.ExportVariables.Add("cures", new CombatantData.TextExportFormatter("cures", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-cures"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-cures"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "cures", Extra); }));
			CombatantData.ExportVariables.Add("maxheal", new CombatantData.TextExportFormatter("maxheal", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-maxheal"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-maxheal"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "maxheal", Extra); }));
			CombatantData.ExportVariables.Add("MAXHEAL", new CombatantData.TextExportFormatter("MAXHEAL", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-MAXHEAL"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-MAXHEAL"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "MAXHEAL", Extra); }));
			CombatantData.ExportVariables.Add("maxhealward", new CombatantData.TextExportFormatter("maxhealward", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-maxhealward"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-maxhealward"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "maxhealward", Extra); }));
			CombatantData.ExportVariables.Add("MAXHEALWARD", new CombatantData.TextExportFormatter("MAXHEALWARD", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-MAXHEALWARD"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-MAXHEALWARD"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "MAXHEALWARD", Extra); }));
			CombatantData.ExportVariables.Add("damagetaken", new CombatantData.TextExportFormatter("damagetaken", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-damagetaken"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-damagetaken"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "damagetaken", Extra); }));
			CombatantData.ExportVariables.Add("healstaken", new CombatantData.TextExportFormatter("healstaken", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-healstaken"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-healstaken"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "healstaken", Extra); }));
//			CombatantData.ExportVariables.Add("powerdrain", new CombatantData.TextExportFormatter("powerdrain", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-powerdrain"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-powerdrain"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "powerdrain", Extra); }));
			CombatantData.ExportVariables.Add("powerheal", new CombatantData.TextExportFormatter("powerheal", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-powerheal"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-powerheal"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "powerheal", Extra); }));
			CombatantData.ExportVariables.Add("kills", new CombatantData.TextExportFormatter("kills", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-kills"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-kills"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "kills", Extra); }));
			CombatantData.ExportVariables.Add("deaths", new CombatantData.TextExportFormatter("deaths", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-deaths"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-deaths"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "deaths", Extra); }));
//			CombatantData.ExportVariables.Add("threatstr", new CombatantData.TextExportFormatter("threatstr", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-threatstr"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-threatstr"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "threatstr", Extra); }));
//			CombatantData.ExportVariables.Add("threatdelta", new CombatantData.TextExportFormatter("threatdelta", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-threatdelta"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-threatdelta"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "threatdelta", Extra); }));
			CombatantData.ExportVariables.Add("NAME3", new CombatantData.TextExportFormatter("NAME3", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME3"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME3"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME3", Extra); }));
			CombatantData.ExportVariables.Add("NAME4", new CombatantData.TextExportFormatter("NAME4", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME4"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME4"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME4", Extra); }));
			CombatantData.ExportVariables.Add("NAME5", new CombatantData.TextExportFormatter("NAME5", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME5"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME5"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME5", Extra); }));
			CombatantData.ExportVariables.Add("NAME6", new CombatantData.TextExportFormatter("NAME6", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME6"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME6"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME6", Extra); }));
			CombatantData.ExportVariables.Add("NAME7", new CombatantData.TextExportFormatter("NAME7", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME7"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME7"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME7", Extra); }));
			CombatantData.ExportVariables.Add("NAME8", new CombatantData.TextExportFormatter("NAME8", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME8"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME8"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME8", Extra); }));
			CombatantData.ExportVariables.Add("NAME9", new CombatantData.TextExportFormatter("NAME9", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME9"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME9"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME9", Extra); }));
			CombatantData.ExportVariables.Add("NAME10", new CombatantData.TextExportFormatter("NAME10", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME10"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME10"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME10", Extra); }));
			CombatantData.ExportVariables.Add("NAME11", new CombatantData.TextExportFormatter("NAME11", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME11"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME11"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME11", Extra); }));
			CombatantData.ExportVariables.Add("NAME12", new CombatantData.TextExportFormatter("NAME12", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME12"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME12"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME12", Extra); }));
			CombatantData.ExportVariables.Add("NAME13", new CombatantData.TextExportFormatter("NAME13", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME13"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME13"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME13", Extra); }));
			CombatantData.ExportVariables.Add("NAME14", new CombatantData.TextExportFormatter("NAME14", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME14"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME14"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME14", Extra); }));
			CombatantData.ExportVariables.Add("NAME15", new CombatantData.TextExportFormatter("NAME15", ActGlobals.ActLocalization.LocalizationStrings["exportFormattingLabel-NAME15"].DisplayedText, ActGlobals.ActLocalization.LocalizationStrings["exportFormattingDesc-NAME15"].DisplayedText, (Data, Extra) => { return CombatantFormatSwitch(Data, "NAME15", Extra); }));


			DamageTypeData.ColumnDefs.Clear();
			DamageTypeData.ColumnDefs.Add("EncId", new DamageTypeData.ColumnDef("EncId", false, "CHAR(8)", "EncId", (Data) => { return string.Empty; }, (Data) => { return Data.Parent.Parent.EncId; }));
			DamageTypeData.ColumnDefs.Add("Combatant", new DamageTypeData.ColumnDef("Combatant", false, "VARCHAR(64)", "Combatant", (Data) => { return Data.Parent.Name; }, (Data) => { return Data.Parent.Name; }));
			DamageTypeData.ColumnDefs.Add("Grouping", new DamageTypeData.ColumnDef("Grouping", false, "VARCHAR(92)", "Grouping", (Data) => { return string.Empty; }, GetDamageTypeGrouping));
			DamageTypeData.ColumnDefs.Add("Type", new DamageTypeData.ColumnDef("Type", true, "VARCHAR(64)", "Type", (Data) => { return Data.Type; }, (Data) => { return Data.Type; }));
			DamageTypeData.ColumnDefs.Add("StartTime", new DamageTypeData.ColumnDef("StartTime", false, "TIMESTAMP", "StartTime", (Data) => { return Data.StartTime == DateTime.MaxValue ? "--:--:--" : Data.StartTime.ToString("T"); }, (Data) => { return Data.StartTime == DateTime.MaxValue ? "0000-00-00 00:00:00" : Data.StartTime.ToString("u").TrimEnd(new char[] { 'Z' }); }));
			DamageTypeData.ColumnDefs.Add("EndTime", new DamageTypeData.ColumnDef("EndTime", false, "TIMESTAMP", "EndTime", (Data) => { return Data.EndTime == DateTime.MinValue ? "--:--:--" : Data.StartTime.ToString("T"); }, (Data) => { return Data.EndTime == DateTime.MinValue ? "0000-00-00 00:00:00" : Data.StartTime.ToString("u").TrimEnd(new char[] { 'Z' }); }));
			DamageTypeData.ColumnDefs.Add("Duration", new DamageTypeData.ColumnDef("Duration", false, "INT", "Duration", (Data) => { return Data.DurationS; }, (Data) => { return Data.Duration.TotalSeconds.ToString("0"); }));
			DamageTypeData.ColumnDefs.Add("Damage", new DamageTypeData.ColumnDef("Damage", true, "BIGINT", "Damage", (Data) => { return Data.Damage.ToString(GetIntCommas()); }, (Data) => { return Data.Damage.ToString(); }));
			DamageTypeData.ColumnDefs.Add("EncDPS", new DamageTypeData.ColumnDef("EncDPS", true, "DOUBLE", "EncDPS", (Data) => { return Data.EncDPS.ToString(GetFloatCommas()); }, (Data) => { return Data.EncDPS.ToString(usCulture); }));
			DamageTypeData.ColumnDefs.Add("CharDPS", new DamageTypeData.ColumnDef("CharDPS", false, "DOUBLE", "CharDPS", (Data) => { return Data.CharDPS.ToString(GetFloatCommas()); }, (Data) => { return Data.CharDPS.ToString(usCulture); }));
			DamageTypeData.ColumnDefs.Add("DPS", new DamageTypeData.ColumnDef("DPS", false, "DOUBLE", "DPS", (Data) => { return Data.DPS.ToString(GetFloatCommas()); }, (Data) => { return Data.DPS.ToString(usCulture); }));
			DamageTypeData.ColumnDefs.Add("Average", new DamageTypeData.ColumnDef("Average", true, "FLOAT", "Average", (Data) => { return Data.Average.ToString(GetFloatCommas()); }, (Data) => { return Data.Average.ToString(usCulture); }));
			DamageTypeData.ColumnDefs.Add("Median", new DamageTypeData.ColumnDef("Median", false, "INT", "Median", (Data) => { return Data.Median.ToString(GetIntCommas()); }, (Data) => { return Data.Median.ToString(); }));
			DamageTypeData.ColumnDefs.Add("MinHit", new DamageTypeData.ColumnDef("MinHit", true, "INT", "MinHit", (Data) => { return Data.MinHit.ToString(GetIntCommas()); }, (Data) => { return Data.MinHit.ToString(); }));
			DamageTypeData.ColumnDefs.Add("MaxHit", new DamageTypeData.ColumnDef("MaxHit", true, "INT", "MaxHit", (Data) => { return Data.MaxHit.ToString(GetIntCommas()); }, (Data) => { return Data.MaxHit.ToString(); }));
			DamageTypeData.ColumnDefs.Add("Hits", new DamageTypeData.ColumnDef("Hits", true, "INT", "Hits", (Data) => { return Data.Hits.ToString(GetIntCommas()); }, (Data) => { return Data.Hits.ToString(); }));
			DamageTypeData.ColumnDefs.Add("CritHits", new DamageTypeData.ColumnDef("CritHits", false, "INT", "CritHits", (Data) => { return Data.CritHits.ToString(GetIntCommas()); }, (Data) => { return Data.CritHits.ToString(); }));
			DamageTypeData.ColumnDefs.Add("Avoids", new DamageTypeData.ColumnDef("Avoids", false, "INT", "Blocked", (Data) => { return Data.Blocked.ToString(GetIntCommas()); }, (Data) => { return Data.Blocked.ToString(); }));
			DamageTypeData.ColumnDefs.Add("Misses", new DamageTypeData.ColumnDef("Misses", false, "INT", "Misses", (Data) => { return Data.Misses.ToString(GetIntCommas()); }, (Data) => { return Data.Misses.ToString(); }));
			DamageTypeData.ColumnDefs.Add("Swings", new DamageTypeData.ColumnDef("Swings", true, "INT", "Swings", (Data) => { return Data.Swings.ToString(GetIntCommas()); }, (Data) => { return Data.Swings.ToString(); }));
			DamageTypeData.ColumnDefs.Add("ToHit", new DamageTypeData.ColumnDef("ToHit", false, "FLOAT", "ToHit", (Data) => { return Data.ToHit.ToString(GetFloatCommas()); }, (Data) => { return Data.ToHit.ToString(); }));
			DamageTypeData.ColumnDefs.Add("AvgDelay", new DamageTypeData.ColumnDef("AvgDelay", false, "FLOAT", "AverageDelay", (Data) => { return Data.AverageDelay.ToString(GetFloatCommas()); }, (Data) => { return Data.AverageDelay.ToString(); }));
			DamageTypeData.ColumnDefs.Add("Crit%", new DamageTypeData.ColumnDef("Crit%", true, "VARCHAR(8)", "CritPerc", (Data) => { return Data.CritPerc.ToString("0'%"); }, (Data) => { return Data.CritPerc.ToString("0'%"); }));

			AttackType.ColumnDefs.Clear();
			AttackType.ColumnDefs.Add("EncId", new AttackType.ColumnDef("EncId", false, "CHAR(8)", "EncId", (Data) => { return string.Empty; }, (Data) => { return Data.Parent.Parent.Parent.EncId; }, (Left, Right) => { return 0; }));
			AttackType.ColumnDefs.Add("Attacker", new AttackType.ColumnDef("Attacker", false, "VARCHAR(64)", "Attacker", (Data) => { return Data.Parent.Outgoing ? Data.Parent.Parent.Name : string.Empty; }, (Data) => { return Data.Parent.Outgoing ? Data.Parent.Parent.Name : string.Empty; }, (Left, Right) => { return 0; }));
			AttackType.ColumnDefs.Add("Victim", new AttackType.ColumnDef("Victim", false, "VARCHAR(64)", "Victim", (Data) => { return Data.Parent.Outgoing ? string.Empty : Data.Parent.Parent.Name; }, (Data) => { return Data.Parent.Outgoing ? string.Empty : Data.Parent.Parent.Name; }, (Left, Right) => { return 0; }));
			AttackType.ColumnDefs.Add("SwingType", new AttackType.ColumnDef("SwingType", false, "TINYINT", "SwingType", GetAttackTypeSwingType, GetAttackTypeSwingType, (Left, Right) => { return 0; }));
			AttackType.ColumnDefs.Add("Type", new AttackType.ColumnDef("Type", true, "VARCHAR(64)", "Type", (Data) => { return Data.Type; }, (Data) => { return Data.Type; }, (Left, Right) => { return Left.Type.CompareTo(Right.Type); }));
			AttackType.ColumnDefs.Add("StartTime", new AttackType.ColumnDef("StartTime", false, "TIMESTAMP", "StartTime", (Data) => { return Data.StartTime == DateTime.MaxValue ? "--:--:--" : Data.StartTime.ToString("T"); }, (Data) => { return Data.StartTime == DateTime.MaxValue ? "0000-00-00 00:00:00" : Data.StartTime.ToString("u").TrimEnd(new char[] { 'Z' }); }, (Left, Right) => { return Left.StartTime.CompareTo(Right.StartTime); }));
			AttackType.ColumnDefs.Add("EndTime", new AttackType.ColumnDef("EndTime", false, "TIMESTAMP", "EndTime", (Data) => { return Data.EndTime == DateTime.MinValue ? "--:--:--" : Data.EndTime.ToString("T"); }, (Data) => { return Data.EndTime == DateTime.MinValue ? "0000-00-00 00:00:00" : Data.EndTime.ToString("u").TrimEnd(new char[] { 'Z' }); }, (Left, Right) => { return Left.EndTime.CompareTo(Right.EndTime); }));
			AttackType.ColumnDefs.Add("Duration", new AttackType.ColumnDef("Duration", false, "INT", "Duration", (Data) => { return Data.DurationS; }, (Data) => { return Data.Duration.TotalSeconds.ToString("0"); }, (Left, Right) => { return Left.Duration.CompareTo(Right.Duration); }));
			AttackType.ColumnDefs.Add("Damage", new AttackType.ColumnDef("Damage", true, "BIGINT", "Damage", (Data) => { return Data.Damage.ToString(GetIntCommas()); }, (Data) => { return Data.Damage.ToString(); }, (Left, Right) => { return Left.Damage.CompareTo(Right.Damage); }));
			AttackType.ColumnDefs.Add("EncDPS", new AttackType.ColumnDef("EncDPS", true, "DOUBLE", "EncDPS", (Data) => { return Data.EncDPS.ToString(GetFloatCommas()); }, (Data) => { return Data.EncDPS.ToString(usCulture); }, (Left, Right) => { return Left.EncDPS.CompareTo(Right.EncDPS); }));
			AttackType.ColumnDefs.Add("CharDPS", new AttackType.ColumnDef("CharDPS", false, "DOUBLE", "CharDPS", (Data) => { return Data.CharDPS.ToString(GetFloatCommas()); }, (Data) => { return Data.CharDPS.ToString(usCulture); }, (Left, Right) => { return Left.CharDPS.CompareTo(Right.CharDPS); }));
			AttackType.ColumnDefs.Add("DPS", new AttackType.ColumnDef("DPS", false, "DOUBLE", "DPS", (Data) => { return Data.DPS.ToString(GetFloatCommas()); }, (Data) => { return Data.DPS.ToString(usCulture); }, (Left, Right) => { return Left.DPS.CompareTo(Right.DPS); }));
			AttackType.ColumnDefs.Add("Average", new AttackType.ColumnDef("Average", true, "FLOAT", "Average", (Data) => { return Data.Average.ToString(GetFloatCommas()); }, (Data) => { return Data.Average.ToString(usCulture); }, (Left, Right) => { return Left.Average.CompareTo(Right.Average); }));
			AttackType.ColumnDefs.Add("Median", new AttackType.ColumnDef("Median", true, "INT", "Median", (Data) => { return Data.Median.ToString(GetIntCommas()); }, (Data) => { return Data.Median.ToString(); }, (Left, Right) => { return Left.Median.CompareTo(Right.Median); }));
			AttackType.ColumnDefs.Add("MinHit", new AttackType.ColumnDef("MinHit", true, "INT", "MinHit", (Data) => { return Data.MinHit.ToString(GetIntCommas()); }, (Data) => { return Data.MinHit.ToString(); }, (Left, Right) => { return Left.MinHit.CompareTo(Right.MinHit); }));
			AttackType.ColumnDefs.Add("MaxHit", new AttackType.ColumnDef("MaxHit", true, "INT", "MaxHit", (Data) => { return Data.MaxHit.ToString(GetIntCommas()); }, (Data) => { return Data.MaxHit.ToString(); }, (Left, Right) => { return Left.MaxHit.CompareTo(Right.MaxHit); }));
			AttackType.ColumnDefs.Add("Resist", new AttackType.ColumnDef("Resist", true, "VARCHAR(64)", "Resist", (Data) => { return Data.Resist; }, (Data) => { return Data.Resist; }, (Left, Right) => { return Left.Resist.CompareTo(Right.Resist); }));
			AttackType.ColumnDefs.Add("Hits", new AttackType.ColumnDef("Hits", true, "INT", "Hits", (Data) => { return Data.Hits.ToString(GetIntCommas()); }, (Data) => { return Data.Hits.ToString(); }, (Left, Right) => { return Left.Hits.CompareTo(Right.Hits); }));
			AttackType.ColumnDefs.Add("CritHits", new AttackType.ColumnDef("CritHits", false, "INT", "CritHits", (Data) => { return Data.CritHits.ToString(GetIntCommas()); }, (Data) => { return Data.CritHits.ToString(); }, (Left, Right) => { return Left.CritHits.CompareTo(Right.CritHits); }));
			AttackType.ColumnDefs.Add("Avoids", new AttackType.ColumnDef("Avoids", false, "INT", "Blocked", (Data) => { return Data.Blocked.ToString(GetIntCommas()); }, (Data) => { return Data.Blocked.ToString(); }, (Left, Right) => { return Left.Blocked.CompareTo(Right.Blocked); }));
			AttackType.ColumnDefs.Add("Misses", new AttackType.ColumnDef("Misses", false, "INT", "Misses", (Data) => { return Data.Misses.ToString(GetIntCommas()); }, (Data) => { return Data.Misses.ToString(); }, (Left, Right) => { return Left.Misses.CompareTo(Right.Misses); }));
			AttackType.ColumnDefs.Add("Swings", new AttackType.ColumnDef("Swings", true, "INT", "Swings", (Data) => { return Data.Swings.ToString(GetIntCommas()); }, (Data) => { return Data.Swings.ToString(); }, (Left, Right) => { return Left.Swings.CompareTo(Right.Swings); }));
			AttackType.ColumnDefs.Add("ToHit", new AttackType.ColumnDef("ToHit", true, "FLOAT", "ToHit", (Data) => { return Data.ToHit.ToString(GetFloatCommas()); }, (Data) => { return Data.ToHit.ToString(usCulture); }, (Left, Right) => { return Left.ToHit.CompareTo(Right.ToHit); }));
			AttackType.ColumnDefs.Add("AvgDelay", new AttackType.ColumnDef("AvgDelay", false, "FLOAT", "AverageDelay", (Data) => { return Data.AverageDelay.ToString(GetFloatCommas()); }, (Data) => { return Data.AverageDelay.ToString(usCulture); }, (Left, Right) => { return Left.AverageDelay.CompareTo(Right.AverageDelay); }));
			AttackType.ColumnDefs.Add("Crit%", new AttackType.ColumnDef("Crit%", true, "VARCHAR(8)", "CritPerc", (Data) => { return Data.CritPerc.ToString("0'%"); }, (Data) => { return Data.CritPerc.ToString("0'%"); }, (Left, Right) => { return Left.CritPerc.CompareTo(Right.CritPerc); }));

			MasterSwing.ColumnDefs.Clear();
			MasterSwing.ColumnDefs.Add("EncId", new MasterSwing.ColumnDef("EncId", false, "CHAR(8)", "EncId", (Data) => { return string.Empty; }, (Data) => { return Data.ParentEncounter.EncId; }, (Left, Right) => { return 0; }));
			MasterSwing.ColumnDefs.Add("Time", new MasterSwing.ColumnDef("Time", true, "TIMESTAMP", "STime", (Data) => { return Data.Time.ToString("T"); }, (Data) => { return Data.Time.ToString("u").TrimEnd(new char[] { 'Z' }); }, (Left, Right) => { return Left.Time.CompareTo(Right.Time); }));
			MasterSwing.ColumnDefs.Add("Attacker", new MasterSwing.ColumnDef("Attacker", true, "VARCHAR(64)", "Attacker", (Data) => { return Data.Attacker; }, (Data) => { return Data.Attacker; }, (Left, Right) => { return Left.Attacker.CompareTo(Right.Attacker); }));
			MasterSwing.ColumnDefs.Add("SwingType", new MasterSwing.ColumnDef("SwingType", false, "TINYINT", "SwingType", (Data) => { return Data.SwingType.ToString(); }, (Data) => { return Data.SwingType.ToString(); }, (Left, Right) => { return Left.SwingType.CompareTo(Right.SwingType); }));
			MasterSwing.ColumnDefs.Add("AttackType", new MasterSwing.ColumnDef("AttackType", true, "VARCHAR(64)", "AttackType", (Data) => { return Data.AttackType; }, (Data) => { return Data.AttackType; }, (Left, Right) => { return Left.AttackType.CompareTo(Right.AttackType); }));
			MasterSwing.ColumnDefs.Add("DamageType", new MasterSwing.ColumnDef("DamageType", true, "VARCHAR(64)", "DamageType", (Data) => { return Data.DamageType; }, (Data) => { return Data.DamageType; }, (Left, Right) => { return Left.DamageType.CompareTo(Right.DamageType); }));
			MasterSwing.ColumnDefs.Add("Victim", new MasterSwing.ColumnDef("Victim", true, "VARCHAR(64)", "Victim", (Data) => { return Data.Victim; }, (Data) => { return Data.Victim; }, (Left, Right) => { return Left.Victim.CompareTo(Right.Victim); }));
			MasterSwing.ColumnDefs.Add("DamageNum", new MasterSwing.ColumnDef("DamageNum", false, "INT", "Damage", (Data) => { return ((int)Data.Damage).ToString(); }, (Data) => { return ((int)Data.Damage).ToString(); }, (Left, Right) => { return Left.Damage.CompareTo(Right.Damage); }));
			MasterSwing.ColumnDefs.Add("Damage", new MasterSwing.ColumnDef("Damage", true, "VARCHAR(128)", "DamageString", /* lambda */ (Data) => { return Data.Damage.ToString(); }, (Data) => { return Data.Damage.ToString(); }, (Left, Right) => { return Left.Damage.CompareTo(Right.Damage); }));
			// As a C# lesson, the above lines(lambda expressions) can also be written as(anonymous methods):
			MasterSwing.ColumnDefs.Add("Critical", new MasterSwing.ColumnDef("Critical", true, "CHAR(1)", "Critical", /* anonymous */ delegate(MasterSwing Data) { return Data.Critical.ToString(); }, delegate(MasterSwing Data) { return Data.Critical.ToString(usCulture)[0].ToString(); }, delegate(MasterSwing Left, MasterSwing Right) { return Left.Critical.CompareTo(Right.Critical); }));
			// Or also written as(delegated methods):
			MasterSwing.ColumnDefs.Add("Special", new MasterSwing.ColumnDef("Special", true, "VARCHAR(64)", "Special", /* delegate */ GetCellDataSpecial, GetSqlDataSpecial, MasterSwingCompareSpecial));

            // ESO Specific:
            MasterSwing.ColumnDefs.Add("ActionResult", new MasterSwing.ColumnDef("ActionResult", true, "VARCHAR(128)", "ActionResult", (Data) => { return GetCellDataTag(Data, "ActionResult"); }, (Data) => { return GetCellDataTag(Data, "ActionResult"); }, (Left, Right) => { return MasterSwingCompareTagStrs(Left, Right, "ActionResult"); }));
            MasterSwing.ColumnDefs.Add("SlotType", new MasterSwing.ColumnDef("SlotType", true, "VARCHAR(64)", "SlotType", (Data) => { return GetCellDataTag(Data, "ActionSlotType"); }, (Data) => { return GetCellDataTag(Data, "ActionSlotType"); }, (Left, Right) => { return MasterSwingCompareTagStrs(Left, Right, "ActionSlotType"); }));
            MasterSwing.ColumnDefs.Add("PowerType", new MasterSwing.ColumnDef("PowerType", true, "VARCHAR(64)", "PowerType", (Data) => { return GetCellDataTag(Data, "PowerType"); }, (Data) => { return GetCellDataTag(Data, "PowerType"); }, (Left, Right) => { return MasterSwingCompareTagStrs(Left, Right, "PowerType"); }));

			foreach(KeyValuePair<string, MasterSwing.ColumnDef> pair in MasterSwing.ColumnDefs)
				pair.Value.GetCellForeColor = (Data) => { return GetSwingTypeColor(Data.SwingType); };

			ActGlobals.oFormActMain.ValidateLists();
			ActGlobals.oFormActMain.ValidateTableSetup();
        }

        private string GetCellDataTag(MasterSwing data, string tag)
        {
            object d = null;
            if (data.Tags.TryGetValue(tag, out d))
            {
                return (string)d;
            }

            return "";
        }

        private int MasterSwingCompareTagStrs(MasterSwing left, MasterSwing right, string tag)
        {
            string l = GetCellDataTag(left, tag);
            string r = GetCellDataTag(right, tag);
            return l.CompareTo(r);
        }

        private string CombatantNameCleaner(string name)
        {
            // TODO: Format player names
            return name;
        }

        #endregion ACT_Tables_Setup

        /*
        // Chatlog format
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
        */

        // Beta format
        private DateTime ParseDateTime(string FullLogLine)
        {
            // Timestamp format:
            // 2014-03-02T18:53:36.755-08:00 

            int i = FullLogLine.IndexOf(' ');

            if (i != -1)
            {
                long msec = long.Parse(FullLogLine.Substring(0, i));

                DateTime d = DateTime.Today;
                d = d.AddMilliseconds(msec);
                return d;
            }

            return ActGlobals.oFormActMain.LastKnownTime;
        }

        private void OnLogFileChanged(bool IsImport, string NewLogFileName)
        {
            // TODO: Sure we will need this hook soon enough
            reflectionTracker.Clear();
            damageShielded = null;
        }

        private void OnCombatEnd(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            // TODO: Sure we will need this hook soon enough
            reflectionTracker.Clear();
            damageShielded = null;
        }

        void OnBeforeLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            logInfo.detectedType = Color.Gray.ToArgb();
/*
            // To Short
            if (logInfo.logLine.Length < 35) return;
            
            // Magic '*' token
            if (logInfo.logLine[30] != '*') return;

            // Break the line down by the separater.
            string meat = logInfo.logLine.Substring(31);
*/

            // ---- BETA
            int i = logInfo.logLine.IndexOf(' ');
            if (i == -1) return;

            // Magic '*' token
            if (logInfo.logLine[i+1] != '*') return;

            // Break the line down by the separater.
            string meat = logInfo.logLine.Substring(i+2);
            // ----- BETA


            string[] segments = meat.Split(':');

            if (segments.Length < 2)
            {
                logInfo.detectedType = Color.DarkGray.ToArgb();
                return;
            }

            ActGlobals.oFormActMain.GlobalTimeSorter++;

            LogEntryTypes code = LogEntryTypes.logEntryType_UNKNOWN;
            if (logEntryTypes.TryGetValue(segments[0], out code))
            {
                switch (code)
                {
                    case LogEntryTypes.logEntryType_PLAYER:
                        ProcessPlayer(isImport, logInfo, segments);
                        break;
                    case LogEntryTypes.logEntryType_COMBAT:
                        CombatEvent ce = CombatEvent.Parse(segments);
                        if (ce != null) ProcessCombat(logInfo, ce);
                        break;
                    case LogEntryTypes.logEntryType_EFFECT:
                        ProcessEffect(logInfo, segments);
                        break;
                }
            }
        }

        private void ProcessPlayer(bool isImport, LogLineEventArgs logInfo, string[] segments)
        {
            if (segments.Length < 4) return;

            logInfo.detectedType = Color.Purple.ToArgb();

            if (segments[1].Length > 0) ActGlobals.charName = segments[1];

            if ((segments[2].Length > 0) && 
                (ActGlobals.oFormActMain.CurrentZone != segments[2]))
            {
                if (ActGlobals.oFormActMain.InCombat)
                {
                    ActGlobals.oFormActMain.EndCombat(!isImport);
                }

                if (!isImport)
                {
                    ActGlobals.oFormActMain.ChangeZone(segments[2]);
                }
            }
        }

        private CombatEvent FixNames(CombatEvent ce)
        {
            if ((ce.targetName.Length == 0) || (ce .sourceName.Length == 0))
            {
                CombatEvent.Builder b = CombatEvent.Builder.Build(ce);

                if (ce.targetName.Length == 0) b.Target(unk, "None");
                if (ce.sourceName.Length == 0) b.Source(unk, "None");

                return b.Done();
            }
            else
            {
                return ce;
            }
        }

        private void ProcessCombat(LogLineEventArgs logInfo, CombatEvent ce)
        {
            ce = FixNames(ce);

            switch (ce.type)
            {
                case CombatEvent.Type.Damage:
                    logInfo.detectedType = Color.Red.ToArgb();

                    if (ce.actionResult == CombatEvent.ActionResult.ACTION_RESULT_FALL_DAMAGE)
                    {
                        // Falling damage does not start combat.
                        if (!ActGlobals.oFormActMain.InCombat) return;

                        // TODO:  Need to test falling damage.
                    }

                    if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                    {
                        // TODO get the ability that triggers the reflect if possible..
                        string ability = "";

                        if (reflectionTracker.CheckReflected(ce, out ability))
                        {
                            CombatEvent new_ce = CombatEvent.Builder.Build(ce)
                                .Ability("Reflected: " + ce.ability)
                                .Done();

                            LogCombatEvent(SwingTypeEnum.NonMelee, logInfo, new_ce, "");
                        }
                        else
                        {
                            if ( (damageShielded != null) &&
                                 (damageShielded.targetName == ce.targetName) &&
                                 (damageShielded.sourceName == ce.sourceName) )
                            {
                                int shieled = 0;
                                int damage = 0;

                                if (damageShielded.hitValue < ce.hitValue)
                                {
                                    damage = ce.hitValue - damageShielded.hitValue;
                                    shieled = damageShielded.hitValue;
                                }
                                else
                                {
                                    damage = 0;
                                    shieled = ce.hitValue;
                                }

                                CombatEvent ds_ce = CombatEvent.Builder.Build(damageShielded)
                                    .Source(damageShielded.targetName, damageShielded.targetType)
                                    .HitValue(shieled)
                                    .DamageType("Damage Shield")
                                    .Done();

                                LogCombatEvent(SwingTypeEnum.Healing, logInfo, ds_ce, ce.ability);
                                LogCombatEvent(SwingTypeEnum.NonMelee, logInfo, ce, "Shielded: " + shieled.ToString());
                            }
                            else
                            {
                                LogCombatEvent(SwingTypeEnum.NonMelee, logInfo, ce);
                            }

                            damageShielded = null;
                        }
                    }
                    break;
                case CombatEvent.Type.MissLike:
                    logInfo.detectedType = Color.DarkRed.ToArgb();

                    if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                    {
                        string special = ce.result;
                        LogCombatEvent(SwingTypeEnum.NonMelee, logInfo, ce, special);
                    }
                    break;
                case CombatEvent.Type.Heal:
                    logInfo.detectedType = Color.Green.ToArgb();
                    if (ActGlobals.oFormActMain.InCombat)
                    {
                        if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                        {
                            CombatEvent heal_ce = null;
                            if (ce.actionResult == CombatEvent.ActionResult.ACTION_RESULT_HOT_TICK)
                            {
                                heal_ce = CombatEvent.Builder.Build(ce).DamageType("HoT").Done();
                            }
                            else
                            {
                                CombatEvent.Builder b = CombatEvent.Builder.Build(ce).DamageType("Heal");
                                if (ce.ability == "Mutagen") b = b.Ability("Mutagen Heal");
                                heal_ce = b.Done();
                            }

                            LogCombatEvent(SwingTypeEnum.Healing, logInfo, heal_ce);
                        }
                    }
                    break;
                case CombatEvent.Type.Death:
                    logInfo.detectedType = Color.Fuchsia.ToArgb();

                    // TODO: right names...
                    ActGlobals.oFormSpellTimers.RemoveTimerMods(ce.targetName);
                    ActGlobals.oFormSpellTimers.DispellTimerMods(ce.targetName);

                    if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                    {
                        MasterSwing ms =
                            new MasterSwing((int)SwingTypeEnum.NonMelee, false, "",
                                Dnum.Death, logInfo.detectedTime, ActGlobals.oFormActMain.GlobalTimeSorter, 
                                "Killing", ce.sourceName, "Death", ce.targetName);

                        ms.Tags.Add("ActionResult", ce.result);
                        ms.Tags.Add("ActionSlotType", ce.abilitySlotType);
                        ms.Tags.Add("PowerType", ce.powerType);

                        ActGlobals.oFormActMain.AddCombatAction(ms);
                    }
                    break;
                case CombatEvent.Type.Other:
                    logInfo.detectedType = Color.Blue.ToArgb();

                    switch (ce.actionResult)
                    {
                        case CombatEvent.ActionResult.ACTION_RESULT_POWER_ENERGIZE:
                            
                            logInfo.detectedType = Color.Gold.ToArgb();
                            if (ActGlobals.oFormActMain.InCombat)
                            {
                                if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                                {
                                    // TODO: Magicka vs Stamina handling... Special case off ability name?
                                    //       ce.powerType -> Magicka for both magicka and stamina restoring.
                                    if (ce.ability == "Absorb Stamina")
                                    {
                                        // NOTE: Only statmina case handled.
                                        LogCombatEvent(SwingTypeEnum.StaminaHealing, logInfo, ce);
                                    }
                                    else
                                    {
                                        LogCombatEvent(SwingTypeEnum.MagickaHealing, logInfo, ce);
                                    }
                                }
                            }
                            break;
                             
                        case CombatEvent.ActionResult.ACTION_RESULT_REFLECTED:
                            logInfo.detectedType = Color.LightPink.ToArgb();

                            if (!ActGlobals.oFormActMain.InCombat)
                            {
                                if ((ce.sourceType == ce.targetType) && (ce.sourceName == ce.targetName)) break;
                            }

                            if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                            {
                                reflectionTracker.AddReflected(ce);
                            }
                            break;
                        case CombatEvent.ActionResult.ACTION_RESULT_BLOCKED_DAMAGE:
                            logInfo.detectedType = Color.Red.ToArgb();

                            // TODO: possibly make this a CombatEvent.Type.Damage instead of a special case.
                            //       Add a special field to CombatEvent?
                            if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                            {
                                // Block does 50% damage by default.
                                LogCombatEvent(SwingTypeEnum.NonMelee, logInfo, ce, "Blocked");
                            }
                            break;
                        case CombatEvent.ActionResult.ACTION_RESULT_DAMAGE_SHIELDED:
                            logInfo.detectedType = Color.Green.ToArgb();

                            // DamageShielded = The number of damage shield points used by the next Damage line
                            // Damage = The damage hit ( need to subtract shielded damage )

                            if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                            {
                                damageShielded = ce;
                            }
                            break;
                    }

                    // if (ActGlobals.oFormActMain.SetEncounter(logInfo.detectedTime, ce.sourceName, ce.targetName))
                    // {
                    //     LogCombatEvent(SwingTypeEnum.NonMelee, logInfo, ce);
                    // }
                    break;
            }
        }

        // TODO: Determine if we need this event type...  Combat event seems to have enough.
        void ProcessEffect(LogLineEventArgs logInfo, string[] segments)
        {
            // logInfo.detectedType = Color.Blue.ToArgb();
        }

        void LogCombatEvent(SwingTypeEnum st, LogLineEventArgs logInfo, CombatEvent ce, string special = "")
        {
            MasterSwing ms = new MasterSwing(
                                (int)st,
                                ce.critical,
                                special,
                                new Dnum(ce.hitValue),
                                logInfo.detectedTime,
                                ActGlobals.oFormActMain.GlobalTimeSorter,
                                ce.ability,
                                ce.sourceName,
                                ce.damageType,
                                ce.targetName);

            ms.Tags.Add("ActionResult", ce.result);
            ms.Tags.Add("ActionSlotType", ce.abilitySlotType);
            ms.Tags.Add("PowerType", ce.powerType);

            ActGlobals.oFormActMain.AddCombatAction(ms);
        }
    }

    internal class ESOUserControl : UserControl
    {
    }

    internal class ReflectionTracker
    {
        // TODO
        // Change to a 3 state process
        // 1) Reflected source == target - store the ability used for reflecting
        // 2) Reflected source != target - store the attack being reflected
        // 3) Damage all but result match reflected source != target.

        // Need a multi reflect log with overlapping different reflect abilities.
        // ie templar or DK reflect abilities + 1h-shield Defensive Posture.

        public ReflectionTracker()
        {
        }

        public void Clear()
        {
            reflects.Clear();
        }

        public void AddReflected(CombatEvent ce)
        {
            if ((ce.sourceType != ce.targetType) ||
                 (ce.sourceName != ce.targetName))
            {
                // An attack has been relfected.
                reflects.Add(ce.targetName + ':' + ce.targetType + ':' + ce.ability, ce.ability);
            }
            else
            {
                // source == target
                // Indicates the ability activated that will perform the reflect.  ie:
                // *CMBT:Reflected:Defensive Posture:NormalAbility:Lodur Darkbane:Player:Lodur Darkbane:Player:0:Invalid:Generic
            }
        }

        public bool CheckReflected(CombatEvent ce, out string abilityOut)
        {
            abilityOut = "";

            if (reflects.Count == 0) return false;

            string ability = "";
            string key = ce.sourceName + ':' + ce.sourceType + ':' + ce.ability;
            if (reflects.TryGetValue(key, out ability))
            {
                reflects.Remove(key);
                abilityOut = ability;
                return true;
            }
            
            return false;
        }

        private Dictionary<string, string> reflects = new Dictionary<string, string>();
    }

    internal class CombatEvent
    {
        public enum ActionResult
        {
            ACTION_RESULT_ABILITY_ON_COOLDOWN,
            ACTION_RESULT_ABSORBED,
            ACTION_RESULT_BAD_TARGET,
            ACTION_RESULT_BEGIN,
            ACTION_RESULT_BEGIN_CHANNEL,
            ACTION_RESULT_BLADETURN,
            ACTION_RESULT_BLOCKED,
            ACTION_RESULT_BLOCKED_DAMAGE,
            ACTION_RESULT_BUFF,
            ACTION_RESULT_BUSY,
            ACTION_RESULT_CANNOT_USE,
            ACTION_RESULT_CANT_SEE_TARGET,
            ACTION_RESULT_CASTER_DEAD,
            ACTION_RESULT_COMPLETE,
            ACTION_RESULT_CRITICAL_DAMAGE,
            ACTION_RESULT_CRITICAL_HEAL,
            ACTION_RESULT_DAMAGE,
            ACTION_RESULT_DAMAGE_SHIELDED,
            ACTION_RESULT_DEBUFF,
            ACTION_RESULT_DEFENDED,
            ACTION_RESULT_DIED,
            ACTION_RESULT_DIED_XP,
            ACTION_RESULT_DISARMED,
            ACTION_RESULT_DISORIENTED,
            ACTION_RESULT_DODGED,
            ACTION_RESULT_DOT_TICK,
            ACTION_RESULT_DOT_TICK_CRITICAL,
            ACTION_RESULT_EFFECT_FADED,
            ACTION_RESULT_EFFECT_GAINED,
            ACTION_RESULT_EFFECT_GAINED_DURATION,
            ACTION_RESULT_FAILED,
            ACTION_RESULT_FAILED_REQUIREMENTS,
            ACTION_RESULT_FAILED_SIEGE_CREATION_REQUIREMENTS,
            ACTION_RESULT_FALLING,
            ACTION_RESULT_FALL_DAMAGE,
            ACTION_RESULT_FEARED,
            ACTION_RESULT_GRAVEYARD_DISALLOWED_IN_INSTANCE,
            ACTION_RESULT_GRAVEYARD_TOO_CLOSE,
            ACTION_RESULT_HEAL,
            ACTION_RESULT_HOT_TICK,
            ACTION_RESULT_HOT_TICK_CRITICAL,
            ACTION_RESULT_IMMUNE,
            ACTION_RESULT_INSUFFICIENT_RESOURCE,
            ACTION_RESULT_INTERCEPTED,
            ACTION_RESULT_INTERRUPT,
            ACTION_RESULT_INVALID,
            ACTION_RESULT_INVALID_FIXTURE,
            ACTION_RESULT_INVALID_TERRAIN,
            ACTION_RESULT_IN_COMBAT,
            ACTION_RESULT_IN_ENEMY_KEEP,
            ACTION_RESULT_KILLING_BLOW,
            ACTION_RESULT_LEVITATED,
            ACTION_RESULT_LINKED_CAST,
            ACTION_RESULT_MISS,
            ACTION_RESULT_MISSING_EMPTY_SOUL_GEM,
            ACTION_RESULT_MISSING_FILLED_SOUL_GEM,
            ACTION_RESULT_MOUNTED,
            ACTION_RESULT_MUST_BE_IN_OWN_KEEP,
            ACTION_RESULT_NOT_ENOUGH_INVENTORY_SPACE,
            ACTION_RESULT_NO_LOCATION_FOUND,
            ACTION_RESULT_NO_RAM_ATTACKABLE_TARGET_WITHIN_RANGE,
            ACTION_RESULT_NPC_TOO_CLOSE,
            ACTION_RESULT_OFFBALANCE,
            ACTION_RESULT_PACIFIED,
            ACTION_RESULT_PARRIED,
            ACTION_RESULT_PARTIAL_RESIST,
            ACTION_RESULT_POWER_DRAIN,
            ACTION_RESULT_POWER_ENERGIZE,
            ACTION_RESULT_PRECISE_DAMAGE,
            ACTION_RESULT_QUEUED,
            ACTION_RESULT_RAM_ATTACKABLE_TARGETS_ALL_DESTROYED,
            ACTION_RESULT_RAM_ATTACKABLE_TARGETS_ALL_OCCUPIED,
            ACTION_RESULT_REFLECTED,
            ACTION_RESULT_REINCARNATING,
            ACTION_RESULT_RESIST,
            ACTION_RESULT_RESURRECT,
            ACTION_RESULT_ROOTED,
            ACTION_RESULT_SIEGE_LIMIT,
            ACTION_RESULT_SIEGE_TOO_CLOSE,
            ACTION_RESULT_SILENCED,
            ACTION_RESULT_SPRINTING,
            ACTION_RESULT_STAGGERED,
            ACTION_RESULT_STUNNED,
            ACTION_RESULT_SWIMMING,
            ACTION_RESULT_TARGET_DEAD,
            ACTION_RESULT_TARGET_NOT_IN_VIEW,
            ACTION_RESULT_TARGET_NOT_PVP_FLAGGED,
            ACTION_RESULT_TARGET_OUT_OF_RANGE,
            ACTION_RESULT_TARGET_TOO_CLOSE,
            ACTION_RESULT_UNEVEN_TERRAIN,
            ACTION_RESULT_WEAPONSWAP,
            ACTION_RESULT_WRECKING_DAMAGE,
            ACTION_RESULT_WRONG_WEAPON
        };

        public enum Type
        {
            Damage,
            MissLike,
            Heal,
            Death,
            Other
        };

        public struct ActionData
        {
            public readonly ActionResult actionResult;
            public readonly Type type;
            public readonly bool critical;

            public ActionData(ActionResult actionResult, Type type, bool critical)
            {
                this.actionResult = actionResult;
                this.type = type;
                this.critical = critical;
            }
        };

        private static readonly Dictionary<string,ActionData> actionResults = new Dictionary<string,ActionData>()
        {
            { "AbilityOnCooldown", new ActionData(ActionResult.ACTION_RESULT_ABILITY_ON_COOLDOWN, Type.Other, false) },
            { "Absorbed", new ActionData(ActionResult.ACTION_RESULT_ABSORBED, Type.Other, false) },
            { "BadTarget", new ActionData(ActionResult.ACTION_RESULT_BAD_TARGET, Type.Other, false) },
            { "Begin", new ActionData(ActionResult.ACTION_RESULT_BEGIN, Type.Other, false) },
            { "BeginChannel", new ActionData(ActionResult.ACTION_RESULT_BEGIN_CHANNEL, Type.Other, false) },
            { "Bladeturn", new ActionData(ActionResult.ACTION_RESULT_BLADETURN, Type.Other, false) },
            { "Blocked", new ActionData(ActionResult.ACTION_RESULT_BLOCKED, Type.Other, false) },
            { "BlockedDamage", new ActionData(ActionResult.ACTION_RESULT_BLOCKED_DAMAGE, Type.Other, false) },
            { "Buff", new ActionData(ActionResult.ACTION_RESULT_BUFF, Type.Other, false) },
            { "Busy", new ActionData(ActionResult.ACTION_RESULT_BUSY, Type.Other, false) },
            { "CannotUse", new ActionData(ActionResult.ACTION_RESULT_CANNOT_USE, Type.Other, false) },
            { "CantSeeTarget", new ActionData(ActionResult.ACTION_RESULT_CANT_SEE_TARGET, Type.Other, false) },
            { "CasterDead", new ActionData(ActionResult.ACTION_RESULT_CASTER_DEAD, Type.Other, false) },
            { "Complete", new ActionData(ActionResult.ACTION_RESULT_COMPLETE, Type.Other, false) },
            { "CriticalDamage", new ActionData(ActionResult.ACTION_RESULT_DAMAGE, Type.Damage, true) },
            { "CriticalHeal", new ActionData(ActionResult.ACTION_RESULT_HEAL, Type.Heal, true) },
            { "Damage", new ActionData(ActionResult.ACTION_RESULT_DAMAGE, Type.Damage, false) },
            { "DamageShielded", new ActionData(ActionResult.ACTION_RESULT_DAMAGE_SHIELDED, Type.Other, false) },
            { "Debuff", new ActionData(ActionResult.ACTION_RESULT_DEBUFF, Type.Other, false) },
            { "Defended", new ActionData(ActionResult.ACTION_RESULT_DEFENDED, Type.Other, false) },
            { "Died", new ActionData(ActionResult.ACTION_RESULT_DIED, Type.Death, false) },
            { "DiedXP", new ActionData(ActionResult.ACTION_RESULT_DIED_XP, Type.Death, false) },
            { "Disarmed", new ActionData(ActionResult.ACTION_RESULT_DISARMED, Type.Other, false) },
            { "Disoriented", new ActionData(ActionResult.ACTION_RESULT_DISORIENTED, Type.Other, false) },
            { "Dodged", new ActionData(ActionResult.ACTION_RESULT_DODGED, Type.MissLike, false) },
            { "DotTick", new ActionData(ActionResult.ACTION_RESULT_DOT_TICK, Type.Damage, false) },
            { "DotTickCritical", new ActionData(ActionResult.ACTION_RESULT_DOT_TICK, Type.Damage, true) },
            { "EffectFaded", new ActionData(ActionResult.ACTION_RESULT_EFFECT_FADED, Type.Other, false) },
            { "EffectGained", new ActionData(ActionResult.ACTION_RESULT_EFFECT_GAINED, Type.Other, false) },
            { "EffectGainedDuration", new ActionData(ActionResult.ACTION_RESULT_EFFECT_GAINED_DURATION, Type.Other, false) },
            { "Failed", new ActionData(ActionResult.ACTION_RESULT_FAILED, Type.Other, false) },
            { "FailedRequirements", new ActionData(ActionResult.ACTION_RESULT_FAILED_REQUIREMENTS, Type.Other, false) },
            { "FailedSiegeCreationRequirements", new ActionData(ActionResult.ACTION_RESULT_FAILED_SIEGE_CREATION_REQUIREMENTS, Type.Other, false) },
            { "Falling", new ActionData(ActionResult.ACTION_RESULT_FALLING, Type.Other, false) },
            { "FallDamage", new ActionData(ActionResult.ACTION_RESULT_FALL_DAMAGE, Type.Other, false) },
            { "Feared", new ActionData(ActionResult.ACTION_RESULT_FEARED, Type.Other, false) },
            { "GraveyardDisallowedInInstance", new ActionData(ActionResult.ACTION_RESULT_GRAVEYARD_DISALLOWED_IN_INSTANCE, Type.Other, false) },
            { "GraveyardTooClose", new ActionData(ActionResult.ACTION_RESULT_GRAVEYARD_TOO_CLOSE, Type.Other, false) },
            { "Heal", new ActionData(ActionResult.ACTION_RESULT_HEAL, Type.Heal, false) },
            { "HotTick", new ActionData(ActionResult.ACTION_RESULT_HOT_TICK, Type.Heal, false) },
            { "HotTickCritical", new ActionData(ActionResult.ACTION_RESULT_HOT_TICK, Type.Heal, true) },
            { "Immune", new ActionData(ActionResult.ACTION_RESULT_IMMUNE, Type.MissLike, false) },
            { "InsufficientResource", new ActionData(ActionResult.ACTION_RESULT_INSUFFICIENT_RESOURCE, Type.Other, false) },
            { "Intercepted", new ActionData(ActionResult.ACTION_RESULT_INTERCEPTED, Type.Other, false) },
            { "Interrupt", new ActionData(ActionResult.ACTION_RESULT_INTERRUPT, Type.Other, false) },
            { "Invalid", new ActionData(ActionResult.ACTION_RESULT_INVALID, Type.Other, false) },
            { "InvalidFixture", new ActionData(ActionResult.ACTION_RESULT_INVALID_FIXTURE, Type.Other, false) },
            { "InvalidTerrain", new ActionData(ActionResult.ACTION_RESULT_INVALID_TERRAIN, Type.Other, false) },
            { "InCombat", new ActionData(ActionResult.ACTION_RESULT_IN_COMBAT, Type.Other, false) },
            { "InEnemyKeep", new ActionData(ActionResult.ACTION_RESULT_IN_ENEMY_KEEP, Type.Other, false) },
            { "KillingBlow", new ActionData(ActionResult.ACTION_RESULT_KILLING_BLOW, Type.Death, false) },
            { "Levitated", new ActionData(ActionResult.ACTION_RESULT_LEVITATED, Type.Other, false) },
            { "LinkedCast", new ActionData(ActionResult.ACTION_RESULT_LINKED_CAST, Type.Other, false) },
            { "Miss", new ActionData(ActionResult.ACTION_RESULT_MISS, Type.MissLike, false) },
            { "MissingEmptySoulGem", new ActionData(ActionResult.ACTION_RESULT_MISSING_EMPTY_SOUL_GEM, Type.Other, false) },
            { "MissingFilledSoulGem", new ActionData(ActionResult.ACTION_RESULT_MISSING_FILLED_SOUL_GEM, Type.Other, false) },
            { "Mounted", new ActionData(ActionResult.ACTION_RESULT_MOUNTED, Type.Other, false) },
            { "MustBeInOwnKeep", new ActionData(ActionResult.ACTION_RESULT_MUST_BE_IN_OWN_KEEP, Type.Other, false) },
            { "NotEnoughInventorySpace", new ActionData(ActionResult.ACTION_RESULT_NOT_ENOUGH_INVENTORY_SPACE, Type.Other, false) },
            { "NoLocationFound", new ActionData(ActionResult.ACTION_RESULT_NO_LOCATION_FOUND, Type.Other, false) },
            { "NoRamAttackableTargetWithinRange", new ActionData(ActionResult.ACTION_RESULT_NO_RAM_ATTACKABLE_TARGET_WITHIN_RANGE, Type.Other, false) },
            { "NPCTooClose", new ActionData(ActionResult.ACTION_RESULT_NPC_TOO_CLOSE, Type.Other, false) },
            { "Offbalance", new ActionData(ActionResult.ACTION_RESULT_OFFBALANCE, Type.Other, false) },
            { "Pacified", new ActionData(ActionResult.ACTION_RESULT_PACIFIED, Type.Other, false) },
            { "Parried", new ActionData(ActionResult.ACTION_RESULT_PARRIED, Type.MissLike, false) },
            { "PartialResist", new ActionData(ActionResult.ACTION_RESULT_PARTIAL_RESIST, Type.Other, false) },
            { "PowerDrain", new ActionData(ActionResult.ACTION_RESULT_POWER_DRAIN, Type.Other, false) },
            { "PowerEnergize", new ActionData(ActionResult.ACTION_RESULT_POWER_ENERGIZE, Type.Other, false) },
            { "PreciseDamage", new ActionData(ActionResult.ACTION_RESULT_PRECISE_DAMAGE, Type.Other, false) },
            { "Queued", new ActionData(ActionResult.ACTION_RESULT_QUEUED, Type.Other, false) },
            { "RamAttackableTargetsAllDestroyed", new ActionData(ActionResult.ACTION_RESULT_RAM_ATTACKABLE_TARGETS_ALL_DESTROYED, Type.Other, false) },
            { "RamAttackableTargetsAllOccupied", new ActionData(ActionResult.ACTION_RESULT_RAM_ATTACKABLE_TARGETS_ALL_OCCUPIED, Type.Other, false) },
            { "Reflected", new ActionData(ActionResult.ACTION_RESULT_REFLECTED, Type.Other, false) },
            { "Reincarnating", new ActionData(ActionResult.ACTION_RESULT_REINCARNATING, Type.Other, false) },
            { "Resist", new ActionData(ActionResult.ACTION_RESULT_RESIST, Type.MissLike, false) },
            { "Resurrect", new ActionData(ActionResult.ACTION_RESULT_RESURRECT, Type.Other, false) },
            { "Rooted", new ActionData(ActionResult.ACTION_RESULT_ROOTED, Type.Other, false) },
            { "SiegeLimit", new ActionData(ActionResult.ACTION_RESULT_SIEGE_LIMIT, Type.Other, false) },
            { "SiegeTooClose", new ActionData(ActionResult.ACTION_RESULT_SIEGE_TOO_CLOSE, Type.Other, false) },
            { "Silenced", new ActionData(ActionResult.ACTION_RESULT_SILENCED, Type.Other, false) },
            { "Sprinting", new ActionData(ActionResult.ACTION_RESULT_SPRINTING, Type.Other, false) },
            { "Staggered", new ActionData(ActionResult.ACTION_RESULT_STAGGERED, Type.Other, false) },
            { "Stunned", new ActionData(ActionResult.ACTION_RESULT_STUNNED, Type.Other, false) },
            { "Swimming", new ActionData(ActionResult.ACTION_RESULT_SWIMMING, Type.Other, false) },
            { "TargetDead", new ActionData(ActionResult.ACTION_RESULT_TARGET_DEAD, Type.Other, false) },
            { "TargetNotInView", new ActionData(ActionResult.ACTION_RESULT_TARGET_NOT_IN_VIEW, Type.Other, false) },
            { "TargetNotPvpFlagged", new ActionData(ActionResult.ACTION_RESULT_TARGET_NOT_PVP_FLAGGED, Type.Other, false) },
            { "TargetOutOfRange", new ActionData(ActionResult.ACTION_RESULT_TARGET_OUT_OF_RANGE, Type.Other, false) },
            { "TargetTooClose", new ActionData(ActionResult.ACTION_RESULT_TARGET_TOO_CLOSE, Type.Other, false) },
            { "UnevenTerrain", new ActionData(ActionResult.ACTION_RESULT_UNEVEN_TERRAIN, Type.Other, false) },
            { "Weaponswap", new ActionData(ActionResult.ACTION_RESULT_WEAPONSWAP, Type.Other, false) },
            { "WreckingDamage", new ActionData(ActionResult.ACTION_RESULT_WRECKING_DAMAGE, Type.Other, false) },
            { "WrongWeapon", new ActionData(ActionResult.ACTION_RESULT_WRONG_WEAPON, Type.Other, false) },

        }; 

        public static CombatEvent Parse(string[] segments)
        {
            try
            {
                if (segments.Length == 11)
                {
                    ActionData ad;
                    if (actionResults.TryGetValue(segments[1], out ad))
                    {
                        return new CombatEvent(segments, ad);
                    }
                }
                else if (segments.Length > 11)
                {
                    ActionData ad;
                    if (actionResults.TryGetValue(segments[1], out ad))
                    {
                        int mergeCount = (segments.Length - 11);
                        string ability = segments[2];
                        for (int i = 0; i < mergeCount; i++)
                        {
                            ability = ability + ':' + segments[3 + i];
                        }

                        return new CombatEvent(segments, ability, 3 + mergeCount, ad);
                    }
                }
            }
            catch (FormatException) { }

            return null;
        }

        public class Builder
        {
            private Builder() {}

            public static Builder Build(CombatEvent ce)
            {
                Builder b = new Builder();

                b.result = ce.result;
                b.ability = ce.ability;
                b.abilitySlotType = ce.abilitySlotType;
                b.sourceName = ce.sourceName;
                b.sourceType = ce.sourceType;
                b.targetName = ce.targetName;
                b.targetType = ce.targetType;
                b.hitValue = ce.hitValue;
                b.powerType = ce.powerType;
                b.damageType = ce.damageType;
                b.critical = ce.critical;
                b.type = ce.type;
                b.actionResult = ce.actionResult;

                return b;
            }

            public Builder Source(string name, string type)
            {
                sourceName = name;
                sourceType = type;

                return this;
            }

            public Builder Target(string name, string type)
            {
                targetName = name;
                targetType = type;

                return this;
            }

            public Builder SwapSourceTarget()
            {
                string tn = targetName;
                string tt = targetType;

                targetName = sourceName;
                targetType = sourceType;
                sourceName = tn;
                sourceType = tt;

                return this;
            }

            public Builder Ability(String ability)
            {
                this.ability = ability;
                return this;
            }

            public Builder HitValue(int hitValue)
            {
                this.hitValue = hitValue;
                return this;
            }

            public Builder DamageType(string damageType)
            {
                this.damageType = damageType;
                return this;
            }

            public CombatEvent Done()
            {
                return new CombatEvent(
                    result,
                    ability,
                    abilitySlotType,
                    sourceName,
                    sourceType,
                    targetName,
                    targetType,
                    hitValue,
                    powerType,
                    damageType,
                    critical,
                    type,
                    actionResult);
            }


            string result;
            string ability;
            string abilitySlotType;
            string sourceName;
            string sourceType;
            string targetName;
            string targetType;
            int hitValue;
            string powerType;
            string damageType;

            // Calculated
            bool critical;
            Type type;
            ActionResult actionResult;
        }

        private CombatEvent(
            string result,
            string ability,
            string abilitySlotType,
            string sourceName,
            string sourceType,
            string targetName,
            string targetType,
            int hitValue,
            string powerType,
            string damageType,
            bool critical,
            Type type,
            ActionResult actionResult)
        {
            this.result = result;
            this.ability = ability;
            this.abilitySlotType = abilitySlotType;
            this.sourceName = sourceName;
            this.sourceType = sourceType;
            this.targetName = targetName;
            this.targetType = targetType;
            this.hitValue = hitValue;
            this.powerType = powerType;
            this.damageType = damageType;
            this.critical = critical;
            this.type = type;
            this.actionResult = actionResult;
        }

        private CombatEvent(string[] segments, ActionData ad)
        {
            int index = 1;

            result = segments[index++];
            // isError = bool.Parse(segments[index++]);
            ability = segments[index++];
            // index++; // abilityGraphic
            abilitySlotType = segments[index++];
            sourceName = segments[index++];
            sourceType = segments[index++];
            targetName = segments[index++];
            targetType = segments[index++];
            hitValue = int.Parse(segments[index++]);
            powerType = segments[index++];
            damageType = segments[index++];

            critical = ad.critical;
            type = ad.type;
            actionResult = ad.actionResult;
        }

        private CombatEvent(string[] segments, string ability, int offset, ActionData ad)
        {
            result = segments[1];
            // isError = bool.Parse(segments[2]);

            this.ability = ability;

            abilitySlotType = segments[offset];
            sourceName = segments[offset+1];
            sourceType = segments[offset+2];
            targetName = segments[offset+3];
            targetType = segments[offset+4];
            hitValue = int.Parse(segments[offset+5]);
            powerType = segments[offset+6];
            damageType = segments[offset+7];

            critical = ad.critical;
            type = ad.type;
            actionResult = ad.actionResult;
        }
        
        // Raw Results
        public readonly string result;
        // public readonly bool isError;
        public readonly string ability;
        public readonly string abilitySlotType;
        public readonly string sourceName;
        public readonly string sourceType;
        public readonly string targetName;
        public readonly string targetType;
        public readonly int hitValue;
        public readonly string powerType;
        public readonly string damageType;

        // Calculated
        public readonly bool critical;
        public readonly Type type;
        public readonly ActionResult actionResult;

    };

    // Stuff from the EQ2 English Parser plugin that is needed for clean setup.
    // Column Setup Support.  While this code is in ACT, it doesn't appear to be accessable.
    // This is all code by Aditu of Permafrost.
    public class ACTPluginBase
    {
        #region ACT_English_Parser

        protected string GetIntCommas()
        {
            return ActGlobals.mainTableShowCommas ? "#,0" : "0";
        }

        protected string GetFloatCommas()
        {
            return ActGlobals.mainTableShowCommas ? "#,0.00" : "0.00";
        }

        protected Color GetSwingTypeColor(int SwingType)
        {
            switch (SwingType)
            {
                case 1:
                case 2:
                    return Color.Crimson;
                case 3:
                    return Color.Blue;
                case 4:
                    return Color.DarkRed;
                case 5:
                    return Color.DarkOrange;
                case 8:
                    return Color.DarkOrchid;
                case 9:
                    return Color.DodgerBlue;
                default:
                    return Color.Black;
            }
        }

        protected string EncounterFormatSwitch(EncounterData Data, List<CombatantData> SelectiveAllies, string VarName, string Extra)
        {
            long damage = 0;
            long healed = 0;
            int swings = 0;
            int hits = 0;
            int crits = 0;
            int heals = 0;
            int critheals = 0;
            int cures = 0;
            int misses = 0;
            int hitfail = 0;
            float tohit = 0;
            double dps = 0;
            double hps = 0;
            long healstaken = 0;
            long damagetaken = 0;
            long powerdrain = 0;
            long powerheal = 0;
            int kills = 0;
            int deaths = 0;

            switch (VarName)
            {
                case "maxheal":
                    return Data.GetMaxHeal(true, false);
                case "MAXHEAL":
                    return Data.GetMaxHeal(false, false);
                case "maxhealward":
                    return Data.GetMaxHeal(true, true);
                case "MAXHEALWARD":
                    return Data.GetMaxHeal(false, true);
                case "maxhit":
                    return Data.GetMaxHit(true);
                case "MAXHIT":
                    return Data.GetMaxHit(false);
                case "duration":
                    return Data.DurationS;
                case "DURATION":
                    return Data.Duration.TotalSeconds.ToString("0");
                case "damage":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    return damage.ToString();
                case "damage-m":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    return (damage / 1000000.0).ToString("0.00");
                case "DAMAGE-k":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    return (damage / 1000.0).ToString("0");
                case "DAMAGE-m":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    return (damage / 1000000.0).ToString("0");
                case "healed":
                    foreach (CombatantData cd in SelectiveAllies)
                        healed += cd.Healed;
                    return healed.ToString();
                case "swings":
                    foreach (CombatantData cd in SelectiveAllies)
                        swings += cd.Swings;
                    return swings.ToString();
                case "hits":
                    foreach (CombatantData cd in SelectiveAllies)
                        hits += cd.Hits;
                    return hits.ToString();
                case "crithits":
                    foreach (CombatantData cd in SelectiveAllies)
                        crits += cd.CritHits;
                    return crits.ToString();
                case "crithit%":
                    foreach (CombatantData cd in SelectiveAllies)
                        crits += cd.CritHits;
                    foreach (CombatantData cd in SelectiveAllies)
                        hits += cd.Hits;
                    float critdamperc = (float)crits / (float)hits;
                    return critdamperc.ToString("0'%");
                case "heals":
                    foreach (CombatantData cd in SelectiveAllies)
                        heals += cd.Heals;
                    return heals.ToString();
                case "critheals":
                    foreach (CombatantData cd in SelectiveAllies)
                        critheals += cd.CritHits;
                    return critheals.ToString();
                case "critheal%":
                    foreach (CombatantData cd in SelectiveAllies)
                        critheals += cd.CritHeals;
                    foreach (CombatantData cd in SelectiveAllies)
                        heals += cd.Heals;
                    float crithealperc = (float)critheals / (float)heals;
                    return crithealperc.ToString("0'%");
                case "cures":
                    foreach (CombatantData cd in SelectiveAllies)
                        cures += cd.CureDispels;
                    return cures.ToString();
                case "misses":
                    foreach (CombatantData cd in SelectiveAllies)
                        misses += cd.Misses;
                    return misses.ToString();
                case "hitfailed":
                    foreach (CombatantData cd in SelectiveAllies)
                        hitfail += cd.Blocked;
                    return hitfail.ToString();
                case "TOHIT":
                    foreach (CombatantData cd in SelectiveAllies)
                        tohit += cd.ToHit;
                    tohit /= SelectiveAllies.Count;
                    return tohit.ToString("0");
                case "DPS":
                case "ENCDPS":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    dps = damage / Data.Duration.TotalSeconds;
                    return dps.ToString("0");
                case "DPS-k":
                case "ENCDPS-k":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    dps = damage / Data.Duration.TotalSeconds;
                    return (dps / 1000.0).ToString("0");
                case "ENCHPS":
                    foreach (CombatantData cd in SelectiveAllies)
                        healed += cd.Healed;
                    hps = healed / Data.Duration.TotalSeconds;
                    return hps.ToString("0");
                case "ENCHPS-k":
                    foreach (CombatantData cd in SelectiveAllies)
                        healed += cd.Healed;
                    hps = healed / Data.Duration.TotalSeconds;
                    return (hps / 1000.0).ToString("0");
                case "tohit":
                    foreach (CombatantData cd in SelectiveAllies)
                        tohit += cd.ToHit;
                    tohit /= SelectiveAllies.Count;
                    return tohit.ToString("F");
                case "dps":
                case "encdps":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    dps = damage / Data.Duration.TotalSeconds;
                    return dps.ToString("F");
                case "dps-k":
                case "encdps-k":
                    foreach (CombatantData cd in SelectiveAllies)
                        damage += cd.Damage;
                    dps = damage / Data.Duration.TotalSeconds;
                    return (dps / 1000.0).ToString("F");
                case "enchps":
                    foreach (CombatantData cd in SelectiveAllies)
                        healed += cd.Healed;
                    hps = healed / Data.Duration.TotalSeconds;
                    return hps.ToString("F");
                case "enchps-k":
                    foreach (CombatantData cd in SelectiveAllies)
                        healed += cd.Healed;
                    hps = healed / Data.Duration.TotalSeconds;
                    return (hps / 1000.0).ToString("F");
                case "healstaken":
                    foreach (CombatantData cd in SelectiveAllies)
                        healstaken += cd.HealsTaken;
                    return healstaken.ToString();
                case "damagetaken":
                    foreach (CombatantData cd in SelectiveAllies)
                        damagetaken += cd.DamageTaken;
                    return damagetaken.ToString();
                case "powerdrain":
                    foreach (CombatantData cd in SelectiveAllies)
                        powerdrain += cd.PowerDamage;
                    return powerdrain.ToString();
                case "powerheal":
                    foreach (CombatantData cd in SelectiveAllies)
                        powerheal += cd.PowerReplenish;
                    return powerheal.ToString();
                case "kills":
                    foreach (CombatantData cd in SelectiveAllies)
                        kills += cd.Kills;
                    return kills.ToString();
                case "deaths":
                    foreach (CombatantData cd in SelectiveAllies)
                        deaths += cd.Deaths;
                    return deaths.ToString();
                case "title":
                    return Data.Title;

                default:
                    return VarName;
            }
        }

        protected string CombatantFormatSwitch(CombatantData Data, string VarName, string Extra)
        {
            int len = 0;
            switch (VarName)
            {
                case "name":
                    return Data.Name;
                case "NAME":
                    len = Int32.Parse(Extra);
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME3":
                    len = 3;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME4":
                    len = 4;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME5":
                    len = 5;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME6":
                    len = 6;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME7":
                    len = 7;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME8":
                    len = 8;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME9":
                    len = 9;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME10":
                    len = 10;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME11":
                    len = 11;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME12":
                    len = 12;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME13":
                    len = 13;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME14":
                    len = 14;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "NAME15":
                    len = 15;
                    return Data.Name.Length - len > 0 ? Data.Name.Remove(len, Data.Name.Length - len).Trim() : Data.Name;
                case "DURATION":
                    return Data.Duration.TotalSeconds.ToString("0");
                case "duration":
                    return Data.DurationS;
                case "maxhit":
                    return Data.GetMaxHit(true);
                case "MAXHIT":
                    return Data.GetMaxHit(false);
                case "maxheal":
                    return Data.GetMaxHeal(true, false);
                case "MAXHEAL":
                    return Data.GetMaxHeal(false, false);
                case "maxhealward":
                    return Data.GetMaxHeal(true, true);
                case "MAXHEALWARD":
                    return Data.GetMaxHeal(false, true);
                case "damage":
                    return Data.Damage.ToString();
                case "damage-k":
                    return (Data.Damage / 1000.0).ToString("0.00");
                case "damage-m":
                    return (Data.Damage / 1000000.0).ToString("0.00");
                case "DAMAGE-k":
                    return (Data.Damage / 1000.0).ToString("0");
                case "DAMAGE-m":
                    return (Data.Damage / 1000000.0).ToString("0");
                case "healed":
                    return Data.Healed.ToString();
                case "swings":
                    return Data.Swings.ToString();
                case "hits":
                    return Data.Hits.ToString();
                case "crithits":
                    return Data.CritHits.ToString();
                case "critheals":
                    return Data.CritHeals.ToString();
                case "crithit%":
                    return Data.CritDamPerc.ToString("0'%");
                case "fcrithit%":
                    return GetFilteredCritChance(Data).ToString("0'%");
                case "critheal%":
                    return Data.CritHealPerc.ToString("0'%");
                case "heals":
                    return Data.Heals.ToString();
                case "cures":
                    return Data.CureDispels.ToString();
                case "misses":
                    return Data.Misses.ToString();
                case "hitfailed":
                    return Data.Blocked.ToString();
                case "TOHIT":
                    return Data.ToHit.ToString("0");
                case "DPS":
                    return Data.DPS.ToString("0");
                case "DPS-k":
                    return (Data.DPS / 1000.0).ToString("0");
                case "ENCDPS":
                    return Data.EncDPS.ToString("0");
                case "ENCDPS-k":
                    return (Data.EncDPS / 1000.0).ToString("0");
                case "ENCHPS":
                    return Data.EncHPS.ToString("0");
                case "ENCHPS-k":
                    return (Data.EncHPS / 1000.0).ToString("0");
                case "tohit":
                    return Data.ToHit.ToString("F");
                case "dps":
                    return Data.DPS.ToString("F");
                case "dps-k":
                    return (Data.DPS / 1000.0).ToString("F");
                case "encdps":
                    return Data.EncDPS.ToString("F");
                case "encdps-k":
                    return (Data.EncDPS / 1000.0).ToString("F");
                case "enchps":
                    return Data.EncHPS.ToString("F");
                case "enchps-k":
                    return (Data.EncHPS / 1000.0).ToString("F");
                case "healstaken":
                    return Data.HealsTaken.ToString();
                case "damagetaken":
                    return Data.DamageTaken.ToString();
                case "powerdrain":
                    return Data.PowerDamage.ToString();
                case "powerheal":
                    return Data.PowerReplenish.ToString();
                case "kills":
                    return Data.Kills.ToString();
                case "deaths":
                    return Data.Deaths.ToString();
                case "damage%":
                    return Data.DamagePercent;
                case "healed%":
                    return Data.HealedPercent;
                case "threatstr":
                    return Data.GetThreatStr("Threat (Out)");
                case "threatdelta":
                    return Data.GetThreatDelta("Threat (Out)").ToString();
                case "n":
                    return "\n";
                case "t":
                    return "\t";

                default:
                    return VarName;
            }
        }

        protected string GetCellDataSpecial(MasterSwing Data)
        {
            return Data.Special;
        }

        protected string GetSqlDataSpecial(MasterSwing Data)
        {
            return Data.Special;
        }

        protected int MasterSwingCompareSpecial(MasterSwing Left, MasterSwing Right)
        {
            return Left.Special.CompareTo(Right.Special);
        }

        protected string GetAttackTypeSwingType(AttackType Data)
        {
            int swingType = 100;
            List<int> swingTypes = new List<int>();
            List<MasterSwing> cachedItems = new List<MasterSwing>(Data.Items);
            for (int i = 0; i < cachedItems.Count; i++)
            {
                MasterSwing s = cachedItems[i];
                if (swingTypes.Contains(s.SwingType) == false)
                    swingTypes.Add(s.SwingType);
            }
            if (swingTypes.Count == 1)
                swingType = swingTypes[0];

            return swingType.ToString();
        }

        protected string GetDamageTypeGrouping(DamageTypeData Data)
        {
            string grouping = string.Empty;

            int swingTypeIndex = 0;
            if (Data.Outgoing)
            {
                grouping += "attacker=" + Data.Parent.Name;
                foreach (KeyValuePair<int, List<string>> links in CombatantData.SwingTypeToDamageTypeDataLinksOutgoing)
                {
                    foreach (string damageTypeLabel in links.Value)
                    {
                        if (Data.Type == damageTypeLabel)
                        {
                            grouping += String.Format("&swingtype{0}={1}", swingTypeIndex++ == 0 ? string.Empty : swingTypeIndex.ToString(), links.Key);
                        }
                    }
                }
            }
            else
            {
                grouping += "victim=" + Data.Parent.Name;
                foreach (KeyValuePair<int, List<string>> links in CombatantData.SwingTypeToDamageTypeDataLinksIncoming)
                {
                    foreach (string damageTypeLabel in links.Value)
                    {
                        if (Data.Type == damageTypeLabel)
                        {
                            grouping += String.Format("&swingtype{0}={1}", swingTypeIndex++ == 0 ? string.Empty : swingTypeIndex.ToString(), links.Key);
                        }
                    }
                }
            }

            return grouping;
        }

        protected float GetFilteredCritChance(CombatantData Data)
        {
            List<AttackType> allAttackTypes = new List<AttackType>();
            List<AttackType> filteredAttackTypes = new List<AttackType>();

            foreach (KeyValuePair<string, AttackType> item in Data.Items["Damage (Out)"].Items)
                allAttackTypes.Add(item.Value);
            foreach (KeyValuePair<string, AttackType> item in Data.Items["Healed (Out)"].Items)
                allAttackTypes.Add(item.Value);

            foreach (AttackType item in allAttackTypes)
            {
                if (item.Type == ActGlobals.ActLocalization.LocalizationStrings["attackTypeTerm-all"].DisplayedText)
                    continue;
                if (item.CritPerc == 0.0f)
                    continue;

                string damageType = string.Empty;
                bool cont = false;
                for (int i = 0; i < item.Items.Count; i++)
                {
                    string itemDamageType = item.Items[i].DamageType;
                    if (String.IsNullOrEmpty(damageType))
                    {
                        damageType = itemDamageType;
                    }
                    else
                    {
                        if (itemDamageType == "melee")
                            continue;
                        if (itemDamageType == "non-melee")
                            continue;
                        if (itemDamageType != damageType)
                        {
                            cont = true;
                            break;
                        }
                    }
                }
                if (cont)
                    continue;
                filteredAttackTypes.Add(item);
            }

            if (filteredAttackTypes.Count == 0)
                return float.NaN;
            else
            {
                float hits = 0;
                float critHits = 0;
                for (int i = 0; i < filteredAttackTypes.Count; i++)
                {
                    AttackType item = filteredAttackTypes[i];
                    hits += item.Hits;
                    critHits += item.CritHits;
                }
                float perc = critHits / hits;
                float ratio = hits / (float)Data.AllOut[ActGlobals.ActLocalization.LocalizationStrings["attackTypeTerm-all"].DisplayedText].Hits;
                //ActGlobals.oFormActMain.WriteDebugLog(String.Format("FCrit: {0} -> {1} / {2} = {3:0%} [{4:0%} data used]", Data.Name, critHits, hits, perc, ratio));
                if (perc == 1)
                {
                    if (ratio > 0.25f)
                        return 100;
                    else
                        return float.NaN;
                }
                if (ratio > 0.25f)
                    return (int)(perc * 100f);
                else
                    return float.NaN;
            }
        }

        #endregion ACT_English_Parser
    }
}