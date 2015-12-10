/*
	Copyright 2014 Francesco "Franc[e]sco" Noferi (francesco149@gmail.com)

	This file is part of OpenTwitchPlays.

	OpenTwitchPlays is free software: you can redistribute it and/or modify
	it under the terms of the GNU General Public License as published by
	the Free Software Foundation, either version 3 of the License, or
	(at your option) any later version.

	OpenTwitchPlays is distributed in the hope that it will be useful,
	but WITHOUT ANY WARRANTY; without even the implied warranty of
	MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
	GNU General Public License for more details.

	You should have received a copy of the GNU General Public License
	along with OpenTwitchPlays.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace OpenTwitchPlays {
    // TODO: separate this file into a more modular design? 700+ lines are a bit too much imho

    /// <summary>
    /// Main window.
    /// TODO: add some logging
    /// </summary>
    public partial class Form1 : Form {
        // TODO: properly wrap serialization inside these data structures instead of 
        // having the serialization code in Form1

        /// <summary>
        /// Stores the current uptime and keypress count status. Serializable.
        /// </summary>
        [Serializable]
        protected class Status {
            // keypress count indexed by GameKey name
            public Dictionary<string, ulong> keypresses = new Dictionary<string, ulong>();
            public ulong totalkeypresses = 0; // total keypress count
            public TimeSpan elapsed = TimeSpan.Zero; // total uptime
        }

        /// <summary>
        /// Temporary data structure used to serialize the program's settings.
        /// TODO: make this a proper immutable struct (lol too lazy)
        /// </summary>
        [Serializable]
        protected class Settings {
            /// <summary>
            /// Temporary data structure used to serialize the program's key bindings.
            /// TODO: make this a proper immutable struct (lol too lazy)
            /// </summary>
            [Serializable]
            public class KeyBinding {
                public string command; // command text
                public int delay; // delay in millisecs
                public ushort vkey; // virtual key code (will be used to retrieve the actual GameKey)
            }

            public List<KeyBinding> binds = new List<KeyBinding>(); // list of all the keybindings
            public bool usepostmessage = false; // true if the application should use PostMessage for keystrokes
            public bool autosave = true; // true if autosave is enabled
            public int autosaveinterval = 60; // autosave interval in seconds
            public string autosavekey = "+{F2}"; // autosave hotkey as a SendKeys string.
            // Note: multiple autosave keys are not allowed in PostMessage mode.
        }

        Status st = new Status(); // current status (serializable)
        string chatfile = ""; // path of the source chat log
        GameWindow gamewindow = null; // handle to the game window
        ulong commandsdone = 0; // commands executed since the last update of the commands / s counter
        DateTime lastcommandrefresh = DateTime.Now; // last time the commands / s counter was updated
        DateTime lastautosave = DateTime.Now; // time at which the last autosave was performed
        DateTime lastuptimerefresh = DateTime.Now; // last time the timer was updated
        int autosaveinterval = 60; // in seconds

        FileStream fs = null; // filestream to the chat log
        StreamReader sr = null; // reader for the chat log file stream
        bool firsttime = true; // true if we're accessing the chat log for the first time (skips old logs)
        bool wasendofstream = false; // used to check when the EOS state changes

        bool democracyEnabled = false; //Democracy Mode
        int democracyVoteTime = 60; //Turn time for Democracy Mode
        List<DemocracyGameKey> democracyTurnList = new List<DemocracyGameKey>();
        DateTime lastDemocracyTick = DateTime.Now;

        public Form1() {
            InitializeComponent();
            notifyIcon.Icon = Icon; // In order to the tray icon appear, assign an icon to it!
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
            Show();
        }

        /// <summary>
        /// Stops the bot, saves the current status and quits the program.
        /// </summary>
        protected void SaveAndQuit() {
            if (timerProcessMessages.Enabled)
                Stop();

            SaveStatus();
            FormClosing -= Form1_Hiding; // Get rid of usual hide function of the form
            FormClosing += Form1_FormClosing; // Introduce the real close application event
            Close();
        }

        /// <summary>
        /// Serializes the current settings to settings.bin
        /// </summary>
        protected void SaveSettings() {
            Settings cfg = new Settings();
            cfg.autosave = menuAutosave.Checked;
            cfg.autosaveinterval = autosaveinterval;
            cfg.usepostmessage = menuUsePostMessage.Checked;

            // key bindings
            for (int i = 0; i < listKeyBindings.Items.Count; i++) {
                var theitem = listKeyBindings.Items[i];
                Settings.KeyBinding bind = new Settings.KeyBinding();
                bind.command = theitem.Text;
                bind.vkey = ((GameKey)theitem.Tag).VirtualKey;
                bind.delay = Convert.ToInt32(theitem.SubItems[2].Text);
                cfg.binds.Add(bind);
            }

            IFormatter formatter = new BinaryFormatter();
            Stream stream = new FileStream("settings.bin", FileMode.Create, FileAccess.Write, FileShare.None);
            formatter.Serialize(stream, cfg);
            stream.Close();

            statusBar1.Text = "Successfully saved settings.";
        }

        /// <summary>
        /// Restores the serialized settings from settings.bin
        /// </summary>
        protected void RestoreSettings() {
            Settings cfg = null;

            if (!File.Exists("settings.bin"))
                return;

            IFormatter formatter = new BinaryFormatter();
            Stream stream = new FileStream("settings.bin", FileMode.Open, FileAccess.Read, FileShare.Read);
            cfg = (Settings)formatter.Deserialize(stream);
            stream.Close();

            menuAutosave.Checked = cfg.autosave;
            autosaveinterval = cfg.autosaveinterval; // internal autosave interval
            menuUsePostMessage.Checked = cfg.usepostmessage;

            // key bindings
            foreach (Settings.KeyBinding bind in cfg.binds)
                AddKeyBinding(bind.command, GameKey.ByVKey(bind.vkey), bind.delay);
        }

        /// <summary>
        /// Saves the current uptime and keycount status as status.bin
        /// </summary>
        protected void SaveStatus() {
            IFormatter formatter = new BinaryFormatter();
            Stream stream = new FileStream("status.bin", FileMode.Create, FileAccess.Write, FileShare.None);
            formatter.Serialize(stream, st);
            stream.Close();
        }

        /// <summary>
        /// Restores the current uptime and keycount status from status.bin
        /// </summary>
        protected void RestoreStatus() {
            if (!File.Exists("status.bin"))
                return;

            IFormatter formatter = new BinaryFormatter();
            Stream stream = new FileStream("status.bin", FileMode.Open, FileAccess.Read, FileShare.Read);
            st = (Status)formatter.Deserialize(stream);
            stream.Close();

            labelKeyPresses.Text = "Keypresses: " + st.totalkeypresses;
            UpdateUptime();
        }

        /// <summary>
        /// Starts the IRC bot.
        /// </summary>
        protected void Start() {
            if (timerProcessMessages.Enabled)
                return;

            if (gamewindow == null) {
                MessageBox.Show("No game window detected! Please make sure your game is detected.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (String.IsNullOrEmpty(chatfile)) {
                MessageBox.Show("You first need to open a source chat log.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            fs = new FileStream(chatfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            sr = new StreamReader(fs);

            // if we're not sending minimized keystrokes, put the window on top
            if (!menuUsePostMessage.Checked)
                gamewindow.SetForeground();

            Thread.Sleep(500);

            lastuptimerefresh = DateTime.Now; // initialize last time the uptime was refreshed
            timerUptime.Enabled = true; // start the uptime refresher

            firsttime = true; // we're accessing the chat log for the first time
            menuStart.Enabled = false; // cannot start twice
            timerProcessMessages.Enabled = true; // start the irc bot timer
            menuStop.Enabled = true; // we can now stop the bot if we want

            democracyEnabled = checkDemocracyMode.Checked;
            democracyVoteTime = Decimal.ToInt32(textDemocracyVoteTime.Value);
            if(democracyEnabled){
                timerDemocracyMode.Interval = democracyVoteTime * 1000;
                timerDemocracyMode.Enabled = true; //starts the democracy mode timer
                lastDemocracyTick = DateTime.Now;
            }
        }

        /// <summary>
        /// Stops the IRC bot.
        /// </summary>
        protected void Stop() {
            if (!timerProcessMessages.Enabled)
                return;

            menuStop.Enabled = false; // can't stop twice
            timerProcessMessages.Enabled = false; // stop the irc bot timer
            timerUptime.Enabled = false; // stop the uptime ticker
            timerDemocracyMode.Enabled = false; //stop the democracy mode ticker
            statusBar1.Text = "Bot stopped.";
            menuStart.Enabled = true; // we can now start the bot if we want

            // close and dispose the file stream and the streamreader
            fs.Close();
            sr.Close();
            fs.Dispose();
            sr.Dispose();
            fs = null;
            sr = null;
        }

        /// <summary>
        /// Reset the uptime and keystroke status and write it to file.
        /// </summary>
        protected void Reset() {
            bool restart = timerProcessMessages.Enabled;

            if (restart)
                Stop();

            st.totalkeypresses = 0;
            st.keypresses = new Dictionary<string, ulong>();
            ResetKeys();
            st.elapsed = TimeSpan.Zero;
            SaveStatus();
            RestoreStatus();

            if (restart)
                Start();
        }

        /// <summary>
        /// Handles a chat line and executes any valid command found.
        /// </summary>
        /// <param name="line">A raw chat line in formatted as username\tmessage</param>
        protected void HandleChatLine(string line) {
            string msgbody = ""; // will contain the message body
            string user = ""; // will contain the username
            GameKey key = GameKey.Invalid; // will contain the requested keystroke if the message is a command
            int delay = 0; // will contain the duration of the keystroke if the msg is a command

            string[] splitted = line.Split('\t'); // split username from the message body

            try {
                user = splitted[0];
                msgbody = splitted[1];
            } catch (Exception) {
                // invalid format
                return;
            }

            // check if the command actually exists by searching it sequentially in the listview
            // TODO: index commands in a dictionary for better performance? 
            //       (not really necessary for something this trivial)
            for (int i = 0; i < listKeyBindings.Items.Count; i++) {
                var item = listKeyBindings.Items[i];

                if (msgbody == item.Text) {
                    // command found! retrieve key and duration
                    key = (GameKey)item.Tag;
                    delay = Convert.ToInt32(item.SubItems[2].Text);
                    break;
                }
            }

            if (key == GameKey.Invalid) // command not found
                return;

            if (delay <= 0) // invalid delay, should never happen
                return;

            // should never happen if ResetKeys is properly called when it should
            if (!st.keypresses.ContainsKey(msgbody))
                st.keypresses.Add(msgbody, 0);

            st.keypresses[msgbody]++;
            st.totalkeypresses++;
            // send the keystroke
            if (democracyEnabled) {
                // Store the KeyPress or add a vote if it exists
                bool previousVote = false;
                for (int i = 0; i < democracyTurnList.Count; i++) {
                    if (democracyTurnList[i].username == user) {
                        previousVote = true;
                        continue;
                    }

                    if (democracyTurnList[i].gameKey == key && democracyTurnList[i].delay == delay) {
                        democracyTurnList[i].votes++;
                        previousVote = true;
                        continue;
                    }
                }
                if (!previousVote)
                    democracyTurnList.Add(new DemocracyGameKey(user, key, delay));
            } else {
                if (menuUsePostMessage.Checked)
                    gamewindow.SendMinimizedKeystroke(key, delay);
                else {
                    if (menuUseSendKeys.Checked)
                        GameWindow.SendGlobalKeystroke(key, delay);
                    else
                        GameWindow.SendGlobalKeybdEvent(key, delay);
                }
            }

            commandsdone++; // this is for the command/s counter

            // log the command
            listBoxCommands.Items.Add(user + " " + msgbody);

            // keep the command log under 100 lines to save RAM
            if (listBoxCommands.Items.Count > 100)
                listBoxCommands.Items.RemoveAt(0);

            // autscroll the command log to the bottom
            listBoxCommands.TopIndex = listBoxCommands.Items.Count - 1;

            // refresh keypresses counter in the GUI
            labelKeyPresses.Text = "Keypresses: " + st.totalkeypresses;
        }

        /// <summary>
        /// Refreshes the uptime label in the GUI
        /// </summary>
        protected void UpdateUptime() {
            labelUptime.Text = "Uptime: " + st.elapsed.ToString(@"dd\d\ hh\h\ mm\m\ ss\s");
        }

        /// <summary>
        /// Writes stats, uptime and command log to stats.txt, uptime.txt and commands.txt
        /// </summary>
        protected void OutputStatsToFile() {
            using (FileStream fs = new FileStream("stats.txt", FileMode.Create, FileAccess.Write, FileShare.Read)) {
                using (StreamWriter sw = new StreamWriter(fs)) {
                    sw.WriteLine("Keypresses (total " + st.totalkeypresses + "):");

                    foreach (var entry in st.keypresses)
                        sw.WriteLine(entry.Key + ": " + entry.Value);

                    sw.Close();
                }
            }

            using (FileStream fs = new FileStream("uptime.txt", FileMode.Create, FileAccess.Write, FileShare.Read)) {
                using (StreamWriter sw = new StreamWriter(fs)) {
                    sw.WriteLine(st.elapsed.ToString(@"dd\d\ hh\h\ mm\m\ ss\s"));
                    sw.Close();
                }
            }

            using (FileStream fs = new FileStream("commands.txt", FileMode.Create, FileAccess.Write, FileShare.Read)) {
                using (StreamWriter sw = new StreamWriter(fs)) {
                    if (listBoxCommands.Items.Count > 0) {
                        int stopindex = listBoxCommands.Items.Count - 1 - 14;

                        if (stopindex < 0)
                            stopindex = 0;

                        for (int i = stopindex; i < listBoxCommands.Items.Count; i++)
                            sw.WriteLine(listBoxCommands.Items[i]);
                    }

                    sw.Close();
                }
            }
        }

        /// <summary>
        /// Initializes the keypresses dictionary with all the current keys.
        /// </summary>
        protected void ResetKeys() {
            for (int i = 0; i < listKeyBindings.Items.Count; i++) {
                string k = listKeyBindings.Items[i].Text;

                if (!st.keypresses.ContainsKey(k))
                    st.keypresses.Add(k, 0);
            }
        }

        /// <summary>
        /// Adds a key binding to the GUI's listview.
        /// </summary>
        /// <param name="command">Command text</param>
        /// <param name="thekey">Desired key</param>
        /// <param name="delay">How long the key will be held down</param>
        /// <param name="multiplekeypresses">Allow appending a number to the command for multiple key presses.</param>
        protected void AddKeyBinding(string command, GameKey thekey, int delay) {
            var item = listKeyBindings.Items.Add(command);
            item.SubItems.Add(thekey.Name);
            item.SubItems.Add(delay.ToString());
            item.Tag = thekey;
        }

        private void menuOpenSource_Click(object sender, EventArgs e) {
            // displays a file open dialog and allows the user to pick a chat logfile.
            // NOTE: the logfile must be in the format username\tmessage
            var res = openFileDialog1.ShowDialog();

            if (res != DialogResult.OK)
                return;

            chatfile = openFileDialog1.FileName;
            labelSourceFile.Text = "Source chat file: " + chatfile;
        }

        private void menuExit_Click(object sender, EventArgs e) {
            SaveAndQuit();
        }

        private void Form1_Load(object sender, EventArgs e) {
            RestoreSettings();
            ResetKeys();
            menuStop.Enabled = false;
            RestoreStatus();

            foreach (GameKey key in GameKey.List) // keys combobox
                comboKeyBindings.Items.Add(key);

            comboKeyBindings.SelectedItem = GameKey.ByName("left"); // left is selected by default
        }

        private void menuAbout_Click(object sender, EventArgs e) {
            MessageBox.Show("Original Code by Francesco \"Franc[e]sco\" Noferi [francesco149@gmail.com]. \n Modified by CremaGames [info@cremagames.com]", "About",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void menuStart_Click(object sender, EventArgs e) {
            Start();
        }

        private void menuStop_Click(object sender, EventArgs e) {
            Stop();
            SaveStatus();
        }

        private void menuReset_Click(object sender, EventArgs e) {
            Reset();
        }

        private void Form1_Hiding(object sender, FormClosingEventArgs e) {
            e.Cancel = true;
            notifyIcon.ShowBalloonTip(2000, null, "OpenTwitchPlays is still running", ToolTipIcon.Info);
            Hide();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) {
            if (timerProcessMessages.Enabled)
                Stop();

            SaveStatus();
        }

        private void menuAutosave_Click(object sender, EventArgs e) {
            menuAutosave.Checked ^= true;
        }

        private void timerProcessMessages_Tick(object sender, EventArgs e) {
            // read a new line
            if (!sr.EndOfStream) {
                if (wasendofstream) {
                    statusBar1.Text = "Processing new messages...";
                    wasendofstream = sr.EndOfStream;
                }

                string theline;

                if (firsttime) {
                    while (!sr.EndOfStream) // skip old logs
                        theline = sr.ReadLine().ToLower();
                } else {
                    theline = sr.ReadLine().ToLower();
                    HandleChatLine(theline);
                    OutputStatsToFile();
                }
            }

            // wait for new lines
            else if (sr.EndOfStream) {
                if (!wasendofstream) {
                    statusBar1.Text = "Waiting for new messages...";
                    wasendofstream = sr.EndOfStream;
                }

                OutputStatsToFile();

                firsttime = false;
            }

            // refresh the command/s counter
            if (DateTime.Now - lastcommandrefresh >= TimeSpan.FromSeconds(1.0)) {
                labelCommandsPerSec.Text = "Commands/s: " + commandsdone;
                commandsdone = 0;
                lastcommandrefresh = DateTime.Now;
                SaveStatus();
            }
        }

        private void timerUptime_Tick(object sender, EventArgs e) {
            // refreshes the uptime accurately on every tick

            DateTime now = DateTime.Now;
            st.elapsed += now - lastuptimerefresh;
            lastuptimerefresh = now;
            UpdateUptime();

            // Refresh the democracy turn time
            if (democracyEnabled) {
                double seconds = (now - lastDemocracyTick).TotalSeconds;

                double progressValue = seconds*100/democracyVoteTime;

                progressBarDemocracyCurrent.Value = Convert.ToInt32(progressValue);
            }
        }

        private void timerDemocracyMode_Tick(object sender, EventArgs e) {
            Console.WriteLine("Timer Democracy");
            lastDemocracyTick = DateTime.Now;

            if (democracyTurnList.Count > 0) {
                //Select the most voted option
                democracyTurnList.Sort(new DemocracyGameKeyComparer());
                DemocracyGameKey mostVotedKey = democracyTurnList[0];
                Console.WriteLine("Most Voted: " + mostVotedKey.gameKey + ", times: " + mostVotedKey.votes);

                //Send the input
                if (menuUsePostMessage.Checked)
                    gamewindow.SendMinimizedKeystroke(mostVotedKey.gameKey, mostVotedKey.delay);
                else {
                    if (menuUseSendKeys.Checked)
                        GameWindow.SendGlobalKeystroke(mostVotedKey.gameKey, mostVotedKey.delay);
                    else
                        GameWindow.SendGlobalKeybdEvent(mostVotedKey.gameKey, mostVotedKey.delay);
                }

                //Resets the list to start a new turn
                democracyTurnList.Clear();
            }
        }

        private void menuAttach_Click(object sender, EventArgs e) {
            // displays the PickWindow form as a modal window and retrieves the picked window

            PickWindow pw = new PickWindow();
            pw.ShowDialog(this);

            if (pw.PickedWindow != IntPtr.Zero)
                gamewindow = new GameWindow(pw.PickedWindow);
            else
                gamewindow = null;

            labelGameWindow.Text = "Game window: " + (gamewindow != null ? gamewindow.ToString("X8") : "0");
        }

        private void menuUsePostMessage_Click(object sender, EventArgs e) {
            menuUsePostMessage.Checked ^= true;
        }

        private void buttonClearKeyBindings_Click(object sender, EventArgs e) {
            listKeyBindings.Items.Clear();
        }

        private void buttonAddKeyBinding_Click(object sender, EventArgs e) {
            try {
                int delay = 0;
                GameKey thekey = (GameKey)comboKeyBindings.SelectedItem;

                if (thekey == null || thekey == GameKey.Invalid)
                    throw new InvalidOperationException("invalid key");

                try {
                    delay = Convert.ToInt32(textDelay.Text);
                } catch (Exception) {
                    throw new InvalidOperationException("invalid delay value");
                }

                if (delay <= 0)
                    throw new InvalidOperationException("cannot use a negative delay");

                if (String.IsNullOrEmpty(textCommand.Text))
                    throw new InvalidOperationException("the command must be at least one character long");

                for (int i = 0; i < listKeyBindings.Items.Count; i++) {
                    if (listKeyBindings.Items[i].Text == textCommand.Text)
                        throw new InvalidOperationException("this command already exists");
                }

                AddKeyBinding(textCommand.Text.ToLower(), thekey, delay);
            } catch (InvalidOperationException shit) {
                MessageBox.Show("Could not add key binding: " + shit.Message, "Warning",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void buttonRemoveKeyBinding_Click(object sender, EventArgs e) {
            if (listKeyBindings.SelectedItems.Count <= 0) // no keybinds selected
                return;

            while (listKeyBindings.SelectedItems.Count > 0) // remove all selected keybindings
                listKeyBindings.Items.Remove(listKeyBindings.SelectedItems[0]);
        }

        private void menuSaveConfig_Click(object sender, EventArgs e) {
            SaveSettings();
        }

        private void menuUseSendKeys_Click(object sender, EventArgs e) {
            menuUseSendKeys.Checked ^= true;
        }

        private void Form1_Show(object sender, EventArgs e) {
            Show();
        }

        private void openFileDialog1_FileOk(object sender, System.ComponentModel.CancelEventArgs e) {

        }

        private void listBoxCommands_SelectedIndexChanged(object sender, EventArgs e) {

        }
    }
}
