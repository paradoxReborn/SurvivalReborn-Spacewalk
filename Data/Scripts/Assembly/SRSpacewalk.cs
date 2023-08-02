///    Copyright (C) 2022 Matthew Kern, a.k.a. Paradox Reborn
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
using Sandbox;

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
            // Fuel level on the last tick used for controlling some mechanics
            public float LastFuelLevel;

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
 
                Inventory = (MyInventory)character.GetInventory();
                InventoryBottles = new List<SRInventoryBottle>();
                OxygenComponent = character.Components?.Get<MyCharacterOxygenComponent>();
                CollisionDamageEnabled = false; // disabled until character moves to prevent damage on world load on moving ship
                RefuelDelay = 0f;

                // Error checks and logging
                if (Inventory != null)
                    Inventory.InventoryContentChanged += Inventory_InventoryContentChanged;

                // Set max speed according to Keen's algorithm in MyCharacter.UpdateCharacterPhysics() since I apparently can't access this value directly.
                var maxShipSpeed = Math.Max(MyDefinitionManager.Static.EnvironmentDefinition.LargeShipMaxSpeed, MyDefinitionManager.Static.EnvironmentDefinition.SmallShipMaxSpeed);
                var maxDudeSpeed = Math.Max(characterDef.MaxSprintSpeed,
                    Math.Max(characterDef.MaxRunSpeed, characterDef.MaxBackrunSpeed));
                //MaxSpeed = maxShipSpeed + maxDudeSpeed;
                //MaxSpeedSquared = MaxSpeed * MaxSpeed;
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

        // Config for the mod
        SRSpacewalkSettings config = new SRSpacewalkSettings();

        // List of characters to apply game rules to
        Dictionary<IMyCharacter, SRCharacterInfo> m_charinfos = new Dictionary<IMyCharacter, SRCharacterInfo>();
        // List of characters to remove from m_characters this tick
        List<IMyCharacter> m_collisionRule = new List<IMyCharacter>();
        List<IMyCharacter> m_jetpackRule = new List<IMyCharacter>();
        List<IMyCharacter> m_autoRefuel = new List<IMyCharacter>();

        // Defaults to restore on world close
        private float m_defaultCharacterGravity;
        private float m_defaultWalkAcceleration;
        private float m_defaultWalkDeceleration;
        private float m_defaultSprintAcceleration;
        private float m_defaultSprintDeceleration;

        // optmization so we don't have to square this every tick
        private float m_damageThresholdSq;

        public override void LoadData()
        {
            // Load config
            config.Load();

            // optmization so we don't have to square this every tick
            m_damageThresholdSq = (float)Math.Pow(config.CollisionDamageThreshold, 2);

            // Hook entity create and add for character list
            MyEntities.OnEntityCreate += TrackCharacter;
            // Tracking OnEntityAdd catches certain edge cases like changing characters in medical room.
            MyEntities.OnEntityAdd += TrackCharacter;

            // Register desync fix
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(config.SecureMessageChannel, ReceivedFuelLevel);

            // Fix character movement if enabled
            if (config.CharacterMovementTweaks){
                // Remember defaults
                m_defaultCharacterGravity = MyPerGameSettings.CharacterGravityMultiplier;
                m_defaultWalkAcceleration = MyPerGameSettings.CharacterMovement.WalkAcceleration;
                m_defaultWalkDeceleration = MyPerGameSettings.CharacterMovement.WalkDecceleration;
                m_defaultSprintAcceleration = MyPerGameSettings.CharacterMovement.SprintAcceleration;
                m_defaultSprintDeceleration = MyPerGameSettings.CharacterMovement.SprintDecceleration;

                // Fix supergravity
                MyPerGameSettings.CharacterGravityMultiplier = config.CharacterGravityMultiplier;

                // Fix jerky character movement
                MyPerGameSettings.CharacterMovement.WalkAcceleration = config.WalkAcceleration;
                MyPerGameSettings.CharacterMovement.WalkDecceleration = config.WalkDecceleration;
                MyPerGameSettings.CharacterMovement.SprintAcceleration = config.SprintAcceleration;
                MyPerGameSettings.CharacterMovement.SprintDecceleration = config.SprintDecceleration;

                //Log settings
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.SprintAcceleration set to: " + MyPerGameSettings.CharacterMovement.SprintAcceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.SprintDecceleration set to: " + MyPerGameSettings.CharacterMovement.SprintDecceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.WalkAcceleration set to: " + MyPerGameSettings.CharacterMovement.WalkAcceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterMovement.WalkDecceleration set to: " + MyPerGameSettings.CharacterMovement.WalkDecceleration);
                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings.CharacterGravityMultiplier set to: " + MyPerGameSettings.CharacterGravityMultiplier);
            }

            MyLog.Default.WriteLineAndConsole("SurvivalReborn: Loaded Spacewalk Release Candidate A for version 1.2.0.");
            //MyLog.Default.WriteLineAndConsole("SurvivalReborn: Loaded Spacewalk Stable 1.1.3.");
            //MyLog.Default.WriteLineAndConsole("SurvivalReborn: Loaded Spacewalk Release Candidate C for version 1.1.");
            //MyAPIGateway.Utilities.ShowMessage("SurvivalReborn", "Loaded Spacewalk Release Candidate C for version 1.1.");
            //MyLog.Default.WriteLine("SurvivalReborn: Loaded Spacewalk Dev Testing Version.");
            //MyAPIGateway.Utilities.ShowNotification("SurvivalReborn: Loaded Spacewalk Dev Testing version.", 60000);
        }

        protected override void UnloadData()
        {
            // Unhook events
            MyEntities.OnEntityAdd -= TrackCharacter;
            MyEntities.OnEntityCreate -= TrackCharacter;

            // Unregister desync fix
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(config.SecureMessageChannel, ReceivedFuelLevel);

            // Restore all defaults - Don't leave a mess if the mod is removed later.
            if (config.CharacterMovementTweaks)
            {
                MyPerGameSettings.CharacterGravityMultiplier = m_defaultCharacterGravity;
                MyPerGameSettings.CharacterMovement.WalkAcceleration = m_defaultWalkAcceleration;
                MyPerGameSettings.CharacterMovement.WalkDecceleration = m_defaultWalkDeceleration;
                MyPerGameSettings.CharacterMovement.SprintAcceleration = m_defaultSprintAcceleration;
                MyPerGameSettings.CharacterMovement.SprintDecceleration = m_defaultSprintDeceleration;

                MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings returned to defaults.");
            }
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
                    //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_charinfos.Count + " characters in the dictionary.");

                    // Prepare to remove character from list when it's removed from world (Remember to unbind this when the character's removed from dictionary)
                    character.OnMarkForClose += Untrack_Character;
                    character.CharacterDied += Untrack_Character;

                    // Add to collision enforcement list if not parented
                    if (config.CollisionTweaks && character.Parent == null)
                    {
                        m_collisionRule.Add(character);
                        //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_collisionRule.Count + " characters in the collision list.");
                    }
                    // Setup jetpack and refuel rules if character has a valid jetpack and inventory
                    if (newCharacterInfo.FuelId != null && newCharacterInfo.OxygenComponent != null && newCharacterInfo.Inventory != null)
                    {
                        newCharacterInfo.BottleMoved += ScanInventory;
                        // Initial inventory scan
                        ScanInventory(character);
                    }
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

            if (InventoryBottles.Count > 0)
            {
                if (config.JetpackTopoff && !m_autoRefuel.Contains(character))
                {
                    m_autoRefuel.Add(character);
                    //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
                }
                if (config.JetpackNerf && !m_jetpackRule.Contains(character))
                {
                    m_jetpackRule.Add(character);
                    //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
                }                  
            }
            else
            {
                m_autoRefuel.Remove(character);
                //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
                m_jetpackRule.Remove(character);
                //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
            }
        }

        /// <summary>
        /// Remove a character from dictionary and lists when marked for close.
        /// </summary>
        /// <param name="obj"></param>
        private void Untrack_Character(IMyEntity obj)
        {
            // Ignore non-characters; sanity-check characters to see if they're actually being tracked.
            IMyCharacter character = obj as IMyCharacter;
            if (character != null && m_charinfos.ContainsKey(character))
            {
                character.OnMarkForClose -= Untrack_Character;
                character.CharacterDied -= Untrack_Character;
                m_charinfos[character].BottleMoved -= ScanInventory;
                m_charinfos[character].Close();
                m_charinfos.Remove(character);
                m_collisionRule.Remove(character);
                m_jetpackRule.Remove(character);
                m_autoRefuel.Remove(character);

                //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_collisionRule.Count + " characters in the collision list.");
                //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
                //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
            }
        }

        /// <summary>
        /// In multiplayer, the client needs to receive a sync packet to prevent a desync when the server tries and fails to refuel a jetpack.
        /// This happens because the jetpack refuel raises a multiplayer event, but changing gas levels with the mod API does not.
        /// </summary>
        /// <param name="handlerId">Packet handler ID for SR:Spacewalk</param>
        /// <param name="raw">The payload, a serialized SRFuelSyncPacket</param>
        /// <param name="steamId">SteamID of the sender</param>
        /// <param name="fromServer">True if packet is from the server</param>
        private void ReceivedFuelLevel(ushort handlerId, byte[] raw, ulong steamId, bool fromServer)
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
                MyLog.Default.WriteLineToConsole("Survival Reborn: Error code EXCLUSION: Spacewalk may be experiencing a network channel collision with another mod. This may impact performance. Try changing SecureMessageChannel in the config file.");
                MyLog.Default.Error("Survival Reborn: Error code EXCLUSION: Spacewalk may be experiencing a network channel collision with another mod. This may impact performance. Try changing SecureMessageChannel in the config file.");
                MyLog.Default.WriteLineAndConsole(ex.Message);
                MyLog.Default.WriteLineAndConsole(ex.StackTrace);
            }
        }

 
        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            // Game rules only run on server
            if (!MyAPIGateway.Session.IsServer) return;

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
                if (character.Parent == null)
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
                    // Trip collision damage on high G-force
                    // Ignore if character's velocity has been set to exactly zero by another mod - this will not happen naturally in collisions.
                    else if (accelSquared > m_damageThresholdSq && character.Physics.LinearVelocity.LengthSquared() != 0f)
                    {
                        float damage = config.CollisionDamagePerMSS * Math.Max(0, (Math.Min(config.CollisionDamageCutoff, (float)Math.Sqrt(accelSquared)) - config.CollisionDamageThreshold));
                        character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        MyLog.Default.WriteLine("SurvivalReborn:" + character.DisplayName + " took " + damage + " collision damage from SR:Spacewalk game rules.");
                    }
                    // Update lastLinearVelocity each tick
                    characterInfo.lastLinearVelocity = character.Physics.LinearVelocity;
                }
                else
                {
                    m_collisionRule.RemoveAt(i);
                    //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_collisionRule.Count + " characters in the collision list.");
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

                        // From the server, send a correction packet to prevent desync when the server lies to the client about jetpack getting refueled.
                        try
                        {
                            MyLog.Default.WriteLine("SurvivalReborn: Syncing fuel level for " + character.DisplayName);
                            SRFuelSyncPacket correction = new SRFuelSyncPacket(character.EntityId, gasToRemove);
                            var packet = MyAPIGateway.Utilities.SerializeToBinary(correction);
                            MyAPIGateway.Multiplayer.SendMessageToOthers(config.SecureMessageChannel, packet);
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
                // OPTIMIZATION: If parented and gas isn't low, the jetpack will not use any more fuel. Stop running this rule as the fuel will never get low.
                else if (character.Parent != null)
                {
                    m_jetpackRule.RemoveAt(i);
                    //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_jetpackRule.Count + " characters in the Jetpack list.");
                }
                // Delayed check to ensure an illegal refill doesn't disable the check meant to find it.
                characterInfo.GasLow = gasLowThisTick;
            }

            // AUTO-REFUEL rule
            for (int i = m_autoRefuel.Count - 1; i >= 0; i--)
            {
                var character = m_autoRefuel[i];
                var characterInfo = m_charinfos[character];
                var fuelLevel = characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId);

                // Reset cooldown and fuel check if jetpack is on
                if (character.EnabledThrusts)
                {
                    characterInfo.RefuelDelay = config.JetpackCooldown;
                    characterInfo.LastFuelLevel = fuelLevel;
                    continue;
                }

                // Wait out delay before repeating other checks
                characterInfo.RefuelDelay -= MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;
                if (characterInfo.RefuelDelay > 0f)
                    continue;

                // Don't refuel if already refueled in the last second or since jetpack powered off.
                if (fuelLevel > characterInfo.LastFuelLevel)
                {
                    characterInfo.RefuelDelay = 1.5f; // Always 1.5s to match vanilla. Hardcoded for a reason.
                    characterInfo.LastFuelLevel = fuelLevel;
                    continue;
                }

                // If refueling is allowed, top-off from bottles.
                // Fuel never appears to set all the way to 1.0 for some reason so use < 0.995f
                if (fuelLevel < 0.995f)
                {
                    foreach (SRCharacterInfo.SRInventoryBottle bottle in characterInfo.InventoryBottles)
                    {
                        // Skip empty bottles
                        if (bottle.currentFillLevel <= 0f)
                            continue;

                        // Calculate gas moved from this bottle (note that fuel flow appears to be in gas/sec)
                        double fuelNeeded = characterInfo.FuelCapacity * (1.0f - fuelLevel);
                        double gasToTake = Math.Min(characterInfo.FuelThroughput * 1.5, Math.Min(bottle.currentFillLevel * bottle.capacity, fuelNeeded));
                        //MyLog.Default.WriteLineAndConsole("SurvivalReborn: Gas to take from bottle: " + gasToTake);

                        // Transfer Gas
                        var bottleItem = bottle.Item.Content as MyObjectBuilder_GasContainerObject;
                        bottleItem.GasLevel -= (float)gasToTake / bottle.capacity;
                        bottle.lastKnownFillLevel = bottleItem.GasLevel;
                        float newFuelLevel = fuelLevel + ((float)gasToTake / characterInfo.FuelCapacity); // parintheses for clarity only
                        characterInfo.OxygenComponent.UpdateStoredGasLevel(ref characterInfo.FuelId, newFuelLevel);

                        // Sync bottle
                        characterInfo.Inventory.OnContentsChanged();
                        // Only refill from one bottle per tick. May cause a small tick when switching fuel feeds but that's okay.
                        //MyAPIGateway.Utilities.ShowNotification("Debug: Filled jetpack from bottle.");
                        break;
                    }

                    characterInfo.RefuelDelay = 1.5f; // Always 1.5s to match Vanilla behaviors
                    characterInfo.LastFuelLevel = fuelLevel;
                }
                // OPTIMIZATION: Remove from list if parented and not in need of refuel
                else if (character.Parent != null)
                {
                    m_autoRefuel.RemoveAt(i);
                    //MyLog.Default.WriteLineAndConsole("SurvivalReborn: There are " + m_autoRefuel.Count + " characters in the Refuel list.");
                }
                // Update last fuel level
                characterInfo.LastFuelLevel = fuelLevel;
            }
        }
    }

}
