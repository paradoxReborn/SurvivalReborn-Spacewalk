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

using System.IO;
using Sandbox.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Utils;

namespace SurvivalReborn
{
    /// <summary>
    /// Simple solution for loading and saving per-world configs for SR:Spacewalk.
    /// Props to TheDigi for teaching me how to do this. None of Digi's code is used here, but the logic is similar.
    /// See: https://github.com/THDigi/SE-ModScript-Examples/blob/master/Data/Scripts/Examples/Example_ServerConfig_Basic.cs
    /// </summary>
    class SRSpacewalkSettings
    {
        const string CONFIG_FILENAME = "SRSpacewalkSettings.ini";
        const string VARIABLE_ID = "SRSpacewalkSettings";

        // These defaults will be used unless changed by the config file.
        // CONFIG ITEMS, General:
        public bool JetpackNerf = true;
        public bool JetpackTopoff = true;
        public bool CollisionTweaks = true;
        public bool CharacterMovementTweaks = true;
        // CONFIG ITEMS, Jetpack:
        public float JetpackCooldown = 3f;
        // CONFIG ITEMS, Collision tweaks:
        public float CollisionDamageThreshold = 750f;
        public float CollisionDamagePerMSS = 0.03f;
        public float CollisionDamageCutoff = 1500f;
        // CONFIG ITEMS, Movement tweaks:
        public float CharacterGravityMultiplier = 1f;
        public float WalkAcceleration = 13.5f;
        public float WalkDecceleration = 100f;
        public float SprintAcceleration = 15f;
        public float SprintDecceleration = 100f;
        // CONFIG ITEMS: Troubleshooting
        public ushort SecureMessageChannel = 5064;

        /// <summary>
        /// Should always be called once when the mod first loads
        /// </summary>
        public void Load()
        {
            if (MyAPIGateway.Session.IsServer)
            {
                // Load pre-existing config from the save file if there is one.
                LoadLocalSettings();
                // Sanitize config, removing any nonexistant values and adding defaults in place of missing values.
                WriteSettings();
            }
            else
            {
                // Load settings from multiplayer host or dedicated server
                LoadServerSettings();
            }
        }

        /// <summary>
        /// Load configs from the save file and copy them into sandbox.sbc, which is shared with all multiplayer clients when they connect.
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
        /// Read settings from the multiplayer host via sandbox.sbc, which the server sends to all clients.
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
            if (rawText == null || rawText == "")
            {
                MyLog.Default.Warning("Survival Reborn: Empty config file loaded. A new default config will be created.");
                return;
            }

            MyIni ini = new MyIni();
            MyIniParseResult discard; // Discard character not supported in old C#
            bool success = ini.TryParse(rawText, out discard);
            
            if (!success)
            {

                MyLog.Default.Warning("Survival Reborn: Invalid or corrupt config. A new default config will be created.");
                return;
            }

            string section = "General";
            JetpackNerf = ini.Get(section, nameof(JetpackNerf)).ToBoolean(JetpackNerf);
            JetpackTopoff = ini.Get(section, nameof(JetpackTopoff)).ToBoolean(JetpackTopoff);
            CollisionTweaks = ini.Get(section, nameof(CollisionTweaks)).ToBoolean(CollisionTweaks);
            CharacterMovementTweaks = ini.Get(section, nameof(CharacterMovementTweaks)).ToBoolean(CharacterMovementTweaks);

            section = "JetpackTopoff";
            JetpackCooldown = ini.Get(section, nameof(JetpackCooldown)).ToSingle(JetpackCooldown);

            section = "CollisionTweaks";
            CollisionDamageThreshold = ini.Get(section, nameof(CollisionDamageThreshold)).ToSingle(CollisionDamageThreshold);
            CollisionDamagePerMSS = ini.Get(section, nameof(CollisionDamagePerMSS)).ToSingle(CollisionDamagePerMSS);
            CollisionDamageCutoff = ini.Get(section, nameof(CollisionDamageCutoff)).ToSingle(CollisionDamageCutoff);

            section = "CharacterMovement";
            CharacterGravityMultiplier = ini.Get(section, nameof(CharacterGravityMultiplier)).ToSingle(CharacterGravityMultiplier);
            WalkAcceleration = ini.Get(section, nameof(WalkAcceleration)).ToSingle(WalkAcceleration);
            WalkDecceleration = ini.Get(section, nameof(WalkDecceleration)).ToSingle(WalkDecceleration);
            SprintAcceleration = ini.Get(section, nameof(SprintAcceleration)).ToSingle(SprintAcceleration);
            SprintDecceleration = ini.Get(section, nameof(SprintDecceleration)).ToSingle(SprintDecceleration);

            section = "Troubleshooting";
            SecureMessageChannel = ini.Get(section, nameof(SecureMessageChannel)).ToUInt16(SecureMessageChannel);
    }

        /// <summary>
        /// Write current settings to local world file
        /// </summary>
        private void WriteSettings()
        {
            MyIni ini = new MyIni();

            // Set values and comments
            string section = "General";
            ini.Set(section, nameof(JetpackNerf), JetpackNerf);
            ini.SetComment(section, nameof(JetpackNerf), "If true, the vanilla jetpack refill from bottles is disabled. Recommend enabling jetpack_topoff as well or bottles will not function at all.");
            ini.Set(section, nameof(JetpackTopoff), JetpackTopoff);
            ini.SetComment(section, nameof(JetpackTopoff), "If true, the jetpack will gradually refill after being shut off and cooled down. Fuel will top off when possible even if it is not low.");
            ini.Set(section, nameof(CollisionTweaks), CollisionTweaks);
            ini.SetComment(section, nameof(CollisionTweaks), "If true, astronauts will be more easily damaged by collisions and hard landings.");
            ini.Set(section, nameof(CharacterMovementTweaks), CharacterMovementTweaks);
            ini.SetComment(section, nameof(CharacterMovementTweaks), "If false, astronauts experience double gravity and jerky movement as in vanilla.");

            section = "JetpackTopoff";
            ini.Set(section, nameof(JetpackCooldown), JetpackCooldown);
            ini.SetComment(section, nameof(JetpackCooldown), "Delay in seconds after jetpack is disabled before topoff begins.");

            section = "CollisionTweaks";
            ini.Set(section, nameof(CollisionDamageThreshold), CollisionDamageThreshold);
            ini.SetComment(section, nameof(CollisionDamageThreshold), "Minimum threshold for collision damage in m/s^2. Damage is determined by the G-force the character experiences in the collision.");
            ini.Set(section, nameof(CollisionDamagePerMSS), CollisionDamagePerMSS);
            ini.SetComment(section, nameof(CollisionDamagePerMSS), "Damage dealt per m/s^2 above CollisionDamageThreshold.");
            ini.Set(section, nameof(CollisionDamageCutoff), CollisionDamageCutoff);
            ini.SetComment(section, nameof(CollisionDamageCutoff), "Above this acceleration, no additional collision damage will be added. It is not recommended to change this; the default stops approximately as vanilla damage begins to kick in.");


            section = "CharacterMovement";
            ini.Set(section, nameof(CharacterGravityMultiplier), CharacterGravityMultiplier);
            ini.SetComment(section, nameof(CharacterGravityMultiplier), "Vanilla value is 2.0. Double character gravity is common in video games, but has strange results in a physics sandbox game.");
            ini.Set(section, nameof(WalkAcceleration), WalkAcceleration);
            ini.SetComment(section, nameof(WalkAcceleration), "The lower the acceleration, the longer it takes the character to pick up speed when the movement key is pressed. This results in smoother movement.");
            ini.Set(section, nameof(WalkDecceleration), WalkDecceleration);
            ini.SetComment(section, nameof(WalkDecceleration), "'Decceleration' is not a ramp-down after realeasing the key. Characters will 'remember' forward speed somewhat if the key is pressed again quickly. This is Keen's doing, not mine.");
            ini.Set(section, nameof(SprintAcceleration), SprintAcceleration);
            ini.Set(section, nameof(SprintDecceleration), SprintDecceleration);

            section = "Troubleshooting";
            ini.Set(section, nameof(SecureMessageChannel), SecureMessageChannel);
            ini.SetComment(section, nameof(SecureMessageChannel), "Leave as default unless you get error code EXCLUSION.");

            // Convert config to string to save
            string raw = ini.ToString();

            // Write to sandbox.sbc to share with connected clients
            MyAPIGateway.Utilities.SetVariable<string>(VARIABLE_ID, raw);
            // Write to config file so admin can modify it
            using(TextWriter cfgfile = MyAPIGateway.Utilities.WriteFileInWorldStorage(CONFIG_FILENAME, typeof(SRSpacewalkSettings)))
            {
                cfgfile.Write(raw);
            }
        }
    }
}
