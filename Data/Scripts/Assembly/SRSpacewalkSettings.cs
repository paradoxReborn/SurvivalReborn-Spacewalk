///    Copyright (C) 2023 Matthew Kern, a.k.a. Paradox Reborn
///
///    This program is free software: you can redistribute it and/or modify
///    it under the terms of the GNU General Public License as published by
///    the Free Software Foundation, either version 3 of the License, or
///    (at your option) any later version.
///
///    This program is distributed in the hope that it will be useful,
///    but WITHOUT ANY WARRANTY; without even the implied warranty of
///    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
///    GNU General Public License for more details.
///
///    You should have received a copy of the GNU General Public License
///    along with this program.  If not, see <https://www.gnu.org/licenses/>.
///
///    In accordance with section 7 of the GNU General Public License,
///    the license is supplemented with the following additional terms:
///    1. You may not claim affiliation with Survival Reborn or its author.
///    2. You must not represent your work as being part of the Survival Reborn series 
///    or use the Survival Reborn name or imagery in any misleading or deceptive way.
///    3. Permission is granted to publish modified versions of files in this program 
///    bearing the .sbc file extension without licensing them under GPL.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Utils;

namespace SurvivalReborn
{
    /// <summary>
    /// Simple solution for loading and saving per-world configs for SR:Spacewalk.
    /// Props to TheDigi for teaching me how to do this. None of Digi's code is used here, but the logic is similar.
    /// See: https://github.com/THDigi/SE-ModScript-Examples/blob/master/Data/Scripts/Examples/Example_ServerConfig_Basic.cs
    /// </summary>
    class SRSpacewalkSettings : MySessionComponentBase
    {
        const string CONFIG_FILENAME = "SRSpacewalkSettings.ini";
        const string VARIABLE_ID = "SRSpacewalkSettings";

        // These defaults will be used unless changed by the config file.
        // CONFIG ITEMS:
        public bool jetpack_nerf = true;
        public bool jetpack_topoff = true;
        public bool collision_tweaks = true;
        public bool character_movement_tweaks = true;
        // TODO: include adjustment for collision and movement tweak numbers

        public override void LoadData()
        {

        }

        /// <summary>
        /// Load configs from the save file and include them in sandbox.sbc for clients to read.
        /// </summary>
        private void LoadLocalSettings()
        {
            if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(CONFIG_FILENAME, typeof(SRSpacewalkSettings)))
            {
                MyLog.Default.WriteLine("SurvivalReborn: Spacewalk config does not exist yet. Defaults will be used and a config file will be created.");
                return;
            }

            string raw;

            using(TextReader cfgfile = MyAPIGateway.Utilities.ReadFileInWorldStorage(CONFIG_FILENAME, typeof (SRSpacewalkSettings)))
            {
                raw = cfgfile.ReadToEnd();
            }

            ReadSettings(raw);
        }

        /// <summary>
        /// Read settings from the multiplayer host via sandbox.sbc
        /// </summary>
        private void LoadServerSettings()
        {
            string raw;
            if (!MyAPIGateway.Utilities.GetVariable<string>(VARIABLE_ID, out raw))
            {
                // TODO: Better warning?
                MyLog.Default.Warning("Survival Reborn: Spacewalk was unable to load configs from the server. Defaults will be used on client. Contact your server administrator.");
                return;
            }

            ReadSettings(raw);
        }

        /// <summary>
        /// Read settings from a given text string
        /// </summary>
        private void ReadSettings(string rawText)
        {
            if (rawText == null)
            {
                // TODO: Ignore the cfg file if it's empty and prompt the admin/user to delete/recreate it
                MyLog.Default.Warning("Survival Reborn: Empty config file loaded. Defaults will be used and the config will be overwritten.");
                return;
            }

            MyIni ini = new MyIni();
            MyIniParseResult discard; // Discard character not supported in old C#
            bool success = ini.TryParse(rawText, out discard);
            
            if (!success)
            {
                // TODO: Ignore the cfg file if it's empty and prompt the admin/user to delete/recreate it
                MyLog.Default.Warning("Survival Reborn: Invalid or corrupt config. Defaults will be used and the config will be overwritten.");
                return;
            }

            string section = "General";
            jetpack_nerf = ini.Get(section, nameof(jetpack_nerf)).ToBoolean(jetpack_nerf);
            jetpack_topoff = ini.Get(section, nameof(jetpack_topoff)).ToBoolean(jetpack_topoff);
            collision_tweaks = ini.Get(section, nameof(collision_tweaks)).ToBoolean(collision_tweaks);
            character_movement_tweaks = ini.Get(section, nameof(character_movement_tweaks)).ToBoolean(character_movement_tweaks);
    }

        /// <summary>
        /// Write current settings to local world file
        /// </summary>
        private void WriteSettings()
        {

        }
    }
}
