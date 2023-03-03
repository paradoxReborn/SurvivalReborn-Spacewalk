﻿///    Copyright (C) 2022 Matthew Kern, a.k.a. Paradox Reborn
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

using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SurvivalReborn
{
    /// <summary>
    /// To track characters and information about them efficiently, multiple features are included in this class:
    /// - Character movement config tweaks
    /// - Character collision damage changes
    /// - Disable Vanilla jetpack refuel from bottles
    /// - Automatic top-off from fuel bottles only while jetpack is shut off and "cool."
    /// 
    /// Fixes some aspects of character movement that could not be set through sbc definitions:
    /// - Remove supergravity
    /// - Lower fall/collision damage threshold
    /// - Smooth out player movement a bit
    /// Defaults are restored on world close in case the mod is removed. Otherwise the world would keep the modded global values.
    /// 
    /// Jetpack refueling changes work regardless of the gas a specific character uses for fuel.
    /// It should be compatible with custom jetpack fuel gasses such as hydrogen peroxide or methane.
    /// 
    /// Refueling rules attempt to account for mods that may refill tanks while in the inventory. This has a small performance cost,
    /// but the mod's overall performance impact should be negligible anyway.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRSpacewalk : MySessionComponentBase
    {
        /// <summary>
        /// Data for tracking information about characters in order to enforce SR:Spacewalk game rules
        /// </summary>
        private class SRCharacterInfo
        {
            // Reference to character this info belongs to
            public IMyCharacter subject;
            // Warning bit for errors during constructor
            public bool valid = true;

            /// VALUES FOR COLLISION DAMAGE RULE
            // If disabled, will skip checking for collision damage until enabled
            public bool CollisionDamageEnabled;
            // Character's max movement speed
            public float MaxSpeed;
            // Max speed squared for optimized checks
            public float MaxSpeedSquared;
            // Linear velocity from the tick before collision damage was tripped
            public Vector3 lastLinearVelocity;

            /// VALUES FOR JETPACK REFUELING RULE
            // Must monitor character's inventory for Hydrogen tanks
            public MyInventory Inventory;
            // Maintained list of hydrogen tanks in player's inventory
            public List<SRInventoryBottle> InventoryBottles;
            // Character's oxygencomponent stores hydrogen, oxygen, etc.
            public MyCharacterOxygenComponent OxygenComponent;
            // Gas that this character's jetpack uses as fuel
            public MyDefinitionId FuelId;
            // This character's fuel capacity
            public float FuelCapacity;
            // Throughput of fuel gas in OxygenComponent
            public float FuelThroughput;
            // GasLow true if the game may attempt a vanilla refuel.
            public bool GasLow;
            // Seconds until jetpack is allowed to refuel
            public float RefuelDelay;

            // Event for a fuel bottle getting moved around in inventory
            public delegate void BottleMovedHandler(IMyCharacter character);
            public event BottleMovedHandler BottleMoved;

            /// <summary>
            /// A data structure for tracking gas bottles in character inventories to enforce the jetpack refueling rule
            /// </summary>
            public class SRInventoryBottle
            {
                public MyPhysicalInventoryItem Item;
                public float capacity;
                public float lastKnownFillLevel;
                public float currentFillLevel { get { return (Item.Content as MyObjectBuilder_GasContainerObject).GasLevel; } }
                public SRInventoryBottle(MyPhysicalInventoryItem item)
                {
                    Item = item;
                    lastKnownFillLevel = currentFillLevel;
                    var gasBottleDefinition = MyDefinitionManager.Static.GetPhysicalItemDefinition(item.Content.GetId()) as MyOxygenContainerDefinition;
                    capacity = gasBottleDefinition.Capacity;
                }
            }

            // BUG: This function can throw a null reference exception
            public SRCharacterInfo(IMyCharacter character)
            {
                if(character == null)
                {
                    MyLog.Default.Warning("SurvivalReborn: SRCharacterInfo called on a null character.");
                    // MyAPIGateway.Utilities.ShowNotification("SurvivalReborn has encountered an error. Submit a bug report with your Space Engineers log.", 20000, "Red");
                    valid = false;
                    return;
                }
                else
                {
                    subject = character;
                    //MyLog.Default.WriteLine("SurvivalReborn: Running SRCharacterInfo constructor for character: " + character.Name);
                }

                var characterDef = character.Definition as MyCharacterDefinition;
                if(characterDef == null)
                {
                    MyLog.Default.Warning("SurvivalReborn: Character definition for " + character.Name + " is null!");
                    // MyAPIGateway.Utilities.ShowNotification("SurvivalReborn has encountered an error. Submit a bug report with your Space Engineers log.", 20000, "Red");
                    valid = false;
                    return;
                }

                // Find the fuel this character uses
                string fuelName = characterDef.Jetpack?.ThrustProperties?.FuelConverter?.FuelId.SubtypeName;
                MyDefinitionId.TryParse("MyObjectBuilder_GasProperties/" + fuelName, out FuelId);

                // Look through character's stored gasses to find fuel, and record its capacity.
                var storedGasses = characterDef.SuitResourceStorage;
                if (storedGasses != null && FuelId != null)
                {
                    foreach (var gas in storedGasses)
                    {
                        if (gas.Id.SubtypeName == fuelName)
                        {
                            FuelCapacity = gas.MaxCapacity;
                            FuelThroughput = gas.Throughput;
                            break;
                        }
                    }
                }
                //else
                    //MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " has no suit gas storage or jetpack fuel isn't defined. Skipping fuel subtype registration.");

                Inventory = (MyInventory)character.GetInventory();
                InventoryBottles = new List<SRInventoryBottle>();
                OxygenComponent = character.Components?.Get<MyCharacterOxygenComponent>();
                CollisionDamageEnabled = false; // disabled until character moves to prevent damage on world load on moving ship
                RefuelDelay = 0f;

                // Error checks and logging
                if (Inventory != null)
                    Inventory.InventoryContentChanged += Inventory_InventoryContentChanged;
                //else
                    //MyLog.Default.WriteLine("SurvivalReborn: Character added with a null inventory.");

                //if (OxygenComponent == null)
                    //MyLog.Default.WriteLine("SurvivalReborn: Character added with a null Oxygen Component.");

                // Set max speed according to Keen's algorithm in MyCharacter.UpdateCharacterPhysics() since I apparently can't access this value directly.
                var maxShipSpeed = Math.Max(MyDefinitionManager.Static.EnvironmentDefinition.LargeShipMaxSpeed, MyDefinitionManager.Static.EnvironmentDefinition.SmallShipMaxSpeed);
                var maxDudeSpeed = Math.Max(characterDef.MaxSprintSpeed,
                    Math.Max(characterDef.MaxRunSpeed, characterDef.MaxBackrunSpeed));
                MaxSpeed = maxShipSpeed + maxDudeSpeed;
                MaxSpeedSquared = MaxSpeed * MaxSpeed;
            }

            /// <summary>
            /// Call Close() on this character's SRCharacterInfo before removing this it from m_characters
            /// </summary>
            public void Close()
            {
                Inventory.InventoryContentChanged -= Inventory_InventoryContentChanged;
            }

            /// <summary>
            /// Refresh this character's list of inventory bottles when a compatible fuel bottle is added or removed.
            /// </summary>
            private void Inventory_InventoryContentChanged(MyInventoryBase arg1, MyPhysicalInventoryItem arg2, VRage.MyFixedPoint arg3)
            {
                // Ignore anything that's not a fuel bottle
                if (CanRefuelFrom(arg2))
                    BottleMoved.Invoke(subject);
            }

            /// <summary>
            /// Return true if this item is a gas container that holds fuel for this character's jetpack.
            /// </summary>
            public bool CanRefuelFrom(MyPhysicalInventoryItem item)
            {
                // OPTIMIZATION to prevent unnecessary calls to GetPhysicalItemDefinition
                if (item.Content as MyObjectBuilder_GasContainerObject == null || FuelId == null)
                    return false;

                // Check item definition to see what gas it holds
                var gasBottleDefinition = MyDefinitionManager.Static.GetPhysicalItemDefinition(item.Content.GetId()) as MyOxygenContainerDefinition;

                if (gasBottleDefinition != null && gasBottleDefinition.StoredGasId.Equals(FuelId))
                    return true;
                else
                    return false;
            }
        }

        // List of characters to apply game rules to
        Dictionary<IMyCharacter, SRCharacterInfo> m_charinfos = new Dictionary<IMyCharacter, SRCharacterInfo>();
        // List of characters to remove from m_characters this tick
        List<IMyCharacter> m_collisionRule = new List<IMyCharacter>();
        List<IMyCharacter> m_jetpackRule = new List<IMyCharacter>();
        List<IMyCharacter> m_autoRefuel = new List<IMyCharacter>();

        // Game rules for fall damage - settings are in m/s/s
        const float DAMAGE_THRESHOLD = 750f;
        const float DAMAGE_THRESHOLD_SQ = 562500f;
        const float IGNORE_ABOVE = 1500f; // Should be roughly where vanilla damage starts
        const float DAMAGE_PER_MSS = 0.03f;

        // Delay to refuel jetpack in seconds from the time it shuts off
        const float JETPACK_COOLDOWN = 2.5f;

        // Defaults to restore on world close
        private float m_defaultCharacterGravity;
        private float m_defaultWalkAcceleration;
        private float m_defaultWalkDeceleration;
        private float m_defaultSprintAcceleration;
        private float m_defaultSprintDeceleration;

        public override void LoadData()
        {
            // Hook entity create and add for character list
            MyEntities.OnEntityCreate += TrackCharacter;
            // Tracking OnEntityAdd catches certain edge cases like changing characters in medical room.
            MyEntities.OnEntityAdd += TrackCharacter;

            // Register desync fix
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(5064, ReceivedCorrection);

            // Fix character movement
            {
                // Remember defaults
                m_defaultCharacterGravity = MyPerGameSettings.CharacterGravityMultiplier;
                m_defaultWalkAcceleration = MyPerGameSettings.CharacterMovement.WalkAcceleration;
                m_defaultWalkDeceleration = MyPerGameSettings.CharacterMovement.WalkDecceleration;
                m_defaultSprintAcceleration = MyPerGameSettings.CharacterMovement.SprintAcceleration;
                m_defaultSprintDeceleration = MyPerGameSettings.CharacterMovement.SprintDecceleration;

                // Fix supergravity
                MyPerGameSettings.CharacterGravityMultiplier = 1f;

                // Fix jerky character movement
                MyPerGameSettings.CharacterMovement.WalkAcceleration = 13.5f;
                MyPerGameSettings.CharacterMovement.WalkDecceleration = 100f;
                MyPerGameSettings.CharacterMovement.SprintAcceleration = 15f;
                MyPerGameSettings.CharacterMovement.SprintDecceleration = 100f;

                //Log settings
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.SprintAcceleration set to: " + MyPerGameSettings.CharacterMovement.SprintAcceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.SprintDecceleration set to: " + MyPerGameSettings.CharacterMovement.SprintDecceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.WalkAcceleration set to: " + MyPerGameSettings.CharacterMovement.WalkAcceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.WalkDecceleration set to: " + MyPerGameSettings.CharacterMovement.WalkDecceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterGravityMultiplier set to: " + MyPerGameSettings.CharacterGravityMultiplier);
            }

            //MyLog.Default.WriteLineAndConsole("SurvivalReborn: Loaded Spacewalk Stable 1.1.");
            MyLog.Default.WriteLineAndConsole("SurvivalReborn: Loaded Spacewalk Release Candidate B for version 1.1.");
            //MyLog.Default.WriteLine("SurvivalReborn: Loaded Spacewalk Dev Testing Version.");
            //MyAPIGateway.Utilities.ShowNotification("SurvivalReborn: Loaded Spacewalk Dev Testing version.", 60000);
        }

        protected override void UnloadData()
        {
            // Unhook events
            MyEntities.OnEntityAdd -= TrackCharacter;
            MyEntities.OnEntityCreate -= TrackCharacter;

            // Unregister desync fix
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(5064, ReceivedCorrection);

            // Restore all defaults - Don't leave a mess if the mod is removed later.
            MyPerGameSettings.CharacterGravityMultiplier = m_defaultCharacterGravity;
            MyPerGameSettings.CharacterMovement.WalkAcceleration = m_defaultWalkAcceleration;
            MyPerGameSettings.CharacterMovement.WalkDecceleration = m_defaultWalkDeceleration;
            MyPerGameSettings.CharacterMovement.SprintAcceleration = m_defaultSprintAcceleration;
            MyPerGameSettings.CharacterMovement.SprintDecceleration = m_defaultSprintDeceleration;

            //Log
            MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings returned to defaults.");
        }

        /// <summary>
        /// Add character to m_characters and appropriate rules lists.
        /// </summary>
        private void TrackCharacter(MyEntity obj)
        {
            if (m_charinfos == null)
            {
                MyLog.Default.Error("SurvivalReborn: Error code NULLIFICATION: Attempted to add a character, but m_characters was null.");
                MyLog.Default.WriteLineAndConsole("SurvivalReborn: Error code NULLIFICATION. Please submit a bug report with your server logs.");
                return;
            }
            IMyCharacter character = obj as IMyCharacter;
            if (character != null && !character.IsDead)
            {
                // There will be a duplicate if the player changes suit in the Medical Room.
                // Duplicate must be removed and replaced to ensure the SRCharacterInfo is correct.
                // There's a little extra overhead for completely replacing the SRCharacterInfo but this ensures a clean start in every case.
                if (m_charinfos.ContainsKey(character))
                    Untrack_Character(character);

                // Add to dictionary
                var newCharacterInfo = new SRCharacterInfo(character);
                if (newCharacterInfo != null && newCharacterInfo.valid)
                {
                    m_charinfos.Add(character, newCharacterInfo);
                    MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_charinfos.Count + " characters in the dictionary.");

                    // Prepare to remove character from list when it's removed from world (Remember to unbind this when the character's removed from dictionary)
                    character.OnMarkForClose += Untrack_Character;
                    character.CharacterDied += Untrack_Character; // Checking for death every tick no longer needed

                    // Add to collision enforcement list if not parented
                    if (character.Parent == null)
                    {
                        m_collisionRule.Add(character);
                        MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_collisionRule.Count + " characters in the collision list.");
                    }
                    // Setup jetpack and refuel rules if character has a valid jetpack and inventory
                    if (newCharacterInfo.FuelId != null && newCharacterInfo.OxygenComponent != null && newCharacterInfo.Inventory != null)
                    {
                        newCharacterInfo.BottleMoved += ScanInventory;
                        // Initial inventory scan
                        ScanInventory(character);
                    }

                    //MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " added to world. There are now " + m_charinfos.Count + " characters listed.");
                }
                else
                {
                    MyLog.Default.Warning("SurvivalReborn: Skipped adding an invalid, null, or dead character.");
                    return;
                }
            }
        }

        /// <summary>
        /// Scan a character's inventory for bottles containing jetpack fuel.
        /// Add character to fuel-related rule lists if in possession of at least one fuel bottle.
        /// </summary>
        /// <param name="character"></param>
        private void ScanInventory(IMyCharacter character)
        {
            var characterInfo = m_charinfos[character];         
            var Inventory = characterInfo.Inventory;
            if (Inventory == null)
                return;

            var InventoryBottles = characterInfo.InventoryBottles;
            // Reset and repopulate bottle list
            InventoryBottles.Clear();
            List<MyPhysicalInventoryItem> items = Inventory.GetItems();
            foreach (MyPhysicalInventoryItem item in items)
            {
                // Add gas bottles to list
                if (characterInfo.CanRefuelFrom(item))
                    InventoryBottles.Add(new SRCharacterInfo.SRInventoryBottle(item));
            }
            //MyAPIGateway.Utilities.ShowNotification("Scanned your inventory and found " + InventoryBottles.Count + " hydrogen tanks.");

            if (InventoryBottles.Count > 0)
            {
                if (!m_autoRefuel.Contains(character))
                {
                    m_autoRefuel.Add(character);
                    MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
                }
                if (!m_jetpackRule.Contains(character))
                {
                    m_jetpackRule.Add(character);
                    MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
                }                  
            }
            else
            {
                m_autoRefuel.Remove(character);
                MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
                m_jetpackRule.Remove(character);
                MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
            }

            // MyAPIGateway.Utilities.ShowNotification("SurvivalReborn debug: Scanned inventory of " + character.DisplayName);
        }

        /// <summary>
        /// Remove a character from dictionary and lists when marked for close.
        /// </summary>
        /// <param name="obj"></param>
        private void Untrack_Character(IMyEntity obj)
        {
            // Ensure this is a character. Ignore otherwise.
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                character.OnMarkForClose -= Untrack_Character;
                character.CharacterDied -= Untrack_Character;
                MyLog.Default.Warning("SurvivalReborn: Tried to untrack a character that wasn't in m_charinfos");
                m_charinfos[character].BottleMoved -= ScanInventory;
                m_charinfos[character].Close();
                m_charinfos.Remove(character);
                m_collisionRule.Remove(character);
                m_jetpackRule.Remove(character);
                m_autoRefuel.Remove(character);

                MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_collisionRule.Count + " characters in the collision list.");
                MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
                MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
            }
            //MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " marked for close. There are now " + m_charinfos.Count + " characters listed.");
        }

        /// <summary>
        /// In multiplayer, the client needs to receive a sync packet to prevent a desync when the server tries and fails to refuel a jetpack.
        /// This happens because the jetpack refuel raises a multiplayer event, but changing gas levels with the mod API does not.
        /// </summary>
        /// <param name="handlerId">Packet handler ID for SR:Spacewalk</param>
        /// <param name="raw">The payload, a serialized SRFuelSyncPacket</param>
        /// <param name="steamId">SteamID of the sender</param>
        /// <param name="fromServer">True if packet is from the server</param>
        private void ReceivedCorrection(ushort handlerId, byte[] raw, ulong steamId, bool fromServer)
        {
            // Ignore any packets that aren't from the server.
            if (!fromServer)
                return;
            // Ignore packet if the server somehow sends it to itself
            if (MyAPIGateway.Session.IsServer)
                return;

            MyLog.Default.WriteLine("SurvivalReborn: Received a fuel level sync packet.");
            try
            {
                SRFuelSyncPacket correction = MyAPIGateway.Utilities.SerializeFromBinary<SRFuelSyncPacket>(raw);

                // Ensure character isn't null as it might not be loaded on all clients.
                IMyCharacter character = MyEntities.GetEntityById(correction.EntityId) as IMyCharacter;
                if(character != null)
                {
                    // FIX THE FUEL LEVEL
                    var tanks = character.Components.Get<MyCharacterOxygenComponent>();
                    var trueFuelLvl = tanks.GetGasFillLevel(m_charinfos[character].FuelId) - correction.FuelAmount;
                    tanks.UpdateStoredGasLevel(ref m_charinfos[character].FuelId, trueFuelLvl);
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineToConsole("Survival Reborn: Error code EXCLUSION: Spacewalk may be experiencing a network channel collision with another mod on channel 5064. This may impact performance. Submit a bug report with a list of mods you are using.");
                MyLog.Default.Error("Survival Reborn: Error code EXCLUSION: Spacewalk may be experiencing a network channel collision with another mod on channel 5064. This may impact performance. Submit a bug report with a list of mods you are using.");
                MyLog.Default.WriteLineAndConsole(ex.Message);
                MyLog.Default.WriteLineAndConsole(ex.StackTrace);
            }
        }


        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            // COLLISION DAMAGE RULE
            for (int i = m_collisionRule.Count - 1; i >= 0; i--)
            {
                var character = m_collisionRule[i];
                var characterInfo = m_charinfos[character];

                /// Skip collision damage for this character until it moves. This "hamfisted but genius" solution catches several edge cases and prevents false positives:
                /// 1. When leaving a seat
                /// 2. When respawning
                /// 3. On world load while moving and not in a seat
                /// The character receives a microscopic nudge to trip this check as soon as physics are ready.
                if (MyAPIGateway.Session.IsServer && character.Parent == null)
                {
                    var accelSquared = (60 * (characterInfo.lastLinearVelocity - character.Physics.LinearVelocity)).LengthSquared();

                    if (!characterInfo.CollisionDamageEnabled)
                    {
                        character.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE, 0.0001f * Vector3.Down, null, null);
                        if (character.Physics.LinearVelocity.LengthSquared() > 0)
                        {
                            characterInfo.CollisionDamageEnabled = true;
                            characterInfo.lastLinearVelocity = character.Physics.LinearVelocity; // Initialize for sanity on first movement.
                            MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " moved. Collision damage enabled.");
                        }
                    }
                    // Trip collision damage on high G-force, but ignore if linear velocity is impossibly high
                    else if (accelSquared > DAMAGE_THRESHOLD_SQ)
                    //&& character.Physics.LinearVelocity.LengthSquared() < characterInfo.MaxSpeedSquared
                    //&& characterInfo.lastLinearVelocity.LengthSquared() < characterInfo.MaxSpeedSquared
                    //&& character.Physics.LinearVelocity.LengthSquared() != 0f
                    {
                        if (character.Physics.LinearVelocity.LengthSquared() > characterInfo.MaxSpeedSquared || characterInfo.lastLinearVelocity.LengthSquared() > characterInfo.MaxSpeedSquared)
                        {
                            MyAPIGateway.Utilities.ShowNotification("SR:Spacewalk error code OVERSPEED. Submit a bug report.", 20000, "Red");
                            MyLog.Default.WriteLineToConsole("SurvivalReborn: Error code OVERSPEED: Linear acceleration calculations appear to have glitched out.");
                            MyLog.Default.Error("SurvivalReborn: Error code OVERSPEED: Linear acceleration calculations appear to have glitched out.");
                            MyLog.Default.WriteLineAndConsole("SurvivalReborn: Send a bug report and tell the developer what you were doing at the time the unexpected damage spike occurred!");
                        }
                        if (character.Physics.LinearVelocity.LengthSquared() == 0f)
                        {
                            MyAPIGateway.Utilities.ShowNotification("SR:Spacewalk error code STASIS. Submit a bug report.", 20000, "Red");
                            MyLog.Default.WriteLineToConsole("SurvivalReborn: Error code STASIS: Character's speed was set to zero and caused damage!");
                            MyLog.Default.Error("SurvivalReborn: Error code STASIS: Character's speed was set to zero and caused damage!");
                            MyLog.Default.WriteLineAndConsole("SurvivalReborn: Send a bug report and tell the developer what you were doing at the time the unexpected damage spike occurred!");
                        }

                        // We definitely crashed into something.
                        float damage = DAMAGE_PER_MSS * Math.Max(0, (Math.Min(IGNORE_ABOVE, (float)Math.Sqrt(accelSquared)) - DAMAGE_THRESHOLD));
                        character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        MyLog.Default.WriteLine("SurvivalReborn:" + character.DisplayName + " took " + damage + " collision damage from SR:Spacewalk game rules.");
                    }
                    // Update lastLinearVelocity each tick
                    characterInfo.lastLinearVelocity = character.Physics.LinearVelocity;
                }
                else if (character.Parent != null)
                {
                    m_collisionRule.RemoveAt(i);
                    MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_collisionRule.Count + " characters in the collision list.");
                }
            }

            // JETPACK REFUELING RULE
            // Disable Vanilla bottle refueling
            for (int i = m_jetpackRule.Count - 1; i >= 0; i--)
            {
                var character = m_jetpackRule[i];
                var characterInfo = m_charinfos[character];
                var gasLowThisTick = characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) < MyCharacterOxygenComponent.GAS_REFILL_RATION;

                // OPTIMIZATION: only check for illegal refuel if gas was low enough to cause one on this tick or the last one
                // This doesn't account for an extremely rare edge case where a bottle was refilled by another mod and an illegal refill happens on the same tick that gas gets low.
                if (characterInfo.GasLow || gasLowThisTick)
                {
                    foreach (SRCharacterInfo.SRInventoryBottle bottle in characterInfo.InventoryBottles)
                    {
                        var delta = bottle.currentFillLevel - bottle.lastKnownFillLevel;
                        // Skip bottle if it hasn't lost gas.
                        if (delta >= 0)
                        {
                            // Allow bottle to be filled, but not deplete. Allows for SKs that refill bottles.
                            if (delta > 0)
                                bottle.lastKnownFillLevel = bottle.currentFillLevel;
                            continue;
                        }

                        // Calculate correct amount to remove
                        float gasToRemove = -delta * bottle.capacity / characterInfo.FuelCapacity;

                        // Set the fuel level back to what it should be.
                        float fixedGasLevel = characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) - gasToRemove;
                        characterInfo.OxygenComponent.UpdateStoredGasLevel(ref characterInfo.FuelId, fixedGasLevel);

                        // Put the gas back in the misbehaving bottle
                        var badBottle = bottle.Item.Content as MyObjectBuilder_GasContainerObject;
                        badBottle.GasLevel = bottle.lastKnownFillLevel;

                        //MyLog.Default.WriteLine("SurvivalReborn: Corrected a disallowed jetpack refuel for " + character.DisplayName);

                        // From the server, send a correction packet to prevent desync when the server lies to the client about jetpack getting refueled.
                        if (MyAPIGateway.Session.IsServer)
                        {
                            try
                            {
                                MyLog.Default.WriteLine("SurvivalReborn: Syncing fuel level for " + character.DisplayName);
                                SRFuelSyncPacket correction = new SRFuelSyncPacket(character.EntityId, gasToRemove);
                                var packet = MyAPIGateway.Utilities.SerializeToBinary(correction);

                                MyAPIGateway.Multiplayer.SendMessageToOthers(5064, packet);
                            }
                            catch (Exception e)
                            {
                                MyLog.Default.Error("SurvivalReborn: Error code DEFLECTION: Server errored out while trying to send a packet. Submit a bug report.");
                                MyLog.Default.WriteLineToConsole("SurvivalReborn: Error code DEFLECTION: Server errored out while trying to send a packet. Submit a bug report.");
                                MyLog.Default.WriteLineAndConsole(e.Message);
                                MyLog.Default.WriteLineAndConsole(e.StackTrace);
                            }
                        }
                    }
                }
                // OPTIMIZATION: If parented and gas isn't low, the jetpack will not use any more fuel. Stop running this rule as the fuel will never get low.
                else if (character.Parent != null)
                {
                    m_jetpackRule.RemoveAt(i);
                    MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
                }
                // Delay check for GasLow to ensure an illegal refill doesn't disable the check meant to find it.
                characterInfo.GasLow = gasLowThisTick;
            }

            // AUTO-REFUEL rule
            // No resync is needed as the server doesn't lie to clients about this part.
            for (int i = m_autoRefuel.Count - 1; i >= 0; i--)
            {
                var character = m_autoRefuel[i];
                var characterInfo = m_charinfos[character];

                // Reset cooldown if jetpack is on
                if (character.EnabledThrusts)
                    characterInfo.RefuelDelay = JETPACK_COOLDOWN;
                // If refueling is delayed, tick down timer.
                else if (characterInfo.RefuelDelay > 0f)
                    characterInfo.RefuelDelay -= MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;
                // If refueling is allowed, top-off from bottles.
                else if (characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) < 0.995f)
                {
                    // Fuel never appears to set all the way to 1.0
                    foreach (SRCharacterInfo.SRInventoryBottle bottle in characterInfo.InventoryBottles)
                    {
                        // Skip empty bottles
                        if (bottle.currentFillLevel <= 0f)
                            continue;

                        // Calculate gas moved from this bottle
                        double fuelNeeded = characterInfo.FuelCapacity * (1.0f - characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId));
                        double gasToTake = Math.Min(characterInfo.FuelThroughput, Math.Min(bottle.currentFillLevel * bottle.capacity, fuelNeeded));

                        // Transfer Gas
                        var bottleItem = bottle.Item.Content as MyObjectBuilder_GasContainerObject;
                        bottleItem.GasLevel -= (float)gasToTake / bottle.capacity;
                        bottle.lastKnownFillLevel = bottleItem.GasLevel;

                        float fuelLevel = characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId);
                        float newFuelLevel = fuelLevel + ((float)gasToTake / characterInfo.FuelCapacity); // parintheses for clarity only
                        characterInfo.OxygenComponent.UpdateStoredGasLevel(ref characterInfo.FuelId, newFuelLevel);

                        break; // Only refill from one bottle per tick. May cause a small tick when switching fuel feeds but that's okay.
                    }

                    // Set timer for next refuel tick. Fuel flow is always per second so this is always one second.
                    characterInfo.RefuelDelay = 1f;
                }
                // OPTIMIZATION: Remove from list if parented and not in need of refuel
                else if (character.Parent != null)
                {
                    m_autoRefuel.RemoveAt(i);
                    MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
                }
            }
        }
    }

}
