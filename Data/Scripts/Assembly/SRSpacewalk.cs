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
    /// To avoid creating multiple separate lists of characters in the world, multiple features are included in this class:
    /// - Character movement tweaks
    /// - Character collision damage changes
    /// - Anti-refueling while jetpack is on
    /// 
    /// Fixes some aspects of character movement that could not be set through sbc definitions:
    /// - Remove supergravity
    /// - Lower fall/collision damage threshold
    /// - Smooth out player movement a bit
    /// Defaults are restored on world close in case the mod is removed. Otherwise the world would keep the modded global values.
    /// 
    /// Jetpack anti-refueling works regardless of the gas a specific character uses for fuel.
    /// It should be compatible with custom jetpack fuel gasses such as hydrogen peroxide or methane.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRSpacewalk : MySessionComponentBase
    {
        /// <summary>
        /// Data for tracking information about characters in order to enforce SR:Spacewalk game rules
        /// </summary>
        private class SRCharacterInfo
        {
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
            // Bool to detect when jetpack turns on
            public bool JetPackOn;
            // Control variable to ensure illegal refills get caught
            public bool GasLow;

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
                    MyLog.Default.WriteLine("SurvivalReborn: Running SRCharacterInfo constructor for character: " + character.Name);
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
                            //MyAPIGateway.Utilities.ShowNotification("Setting fuel capacity to " + gas.MaxCapacity, 20000);
                            FuelCapacity = gas.MaxCapacity;
                            //MyAPIGateway.Utilities.ShowNotification("Set fuel capacity to " + FuelCapacity, 20000);
                            //break;
                        }
                        //MyAPIGateway.Utilities.ShowNotification("This character's " + gas.Id + " capacity is " + gas.MaxCapacity, 20000);
                    }
                }
                else
                    MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " has no suit gas storage or jetpack fuel isn't defined. Skipping fuel subtype registration.");

                Inventory = (MyInventory)character.GetInventory();
                InventoryBottles = new List<SRInventoryBottle>();
                OxygenComponent = character.Components?.Get<MyCharacterOxygenComponent>();
                CollisionDamageEnabled = false; // disabled until character moves to prevent damage on world load on moving ship
                JetPackOn = character.EnabledThrusts;

                // Error checks and logging
                if (Inventory != null)
                    Inventory.InventoryContentChanged += Inventory_InventoryContentChanged;
                else
                {
                    MyLog.Default.WriteLine("SurvivalReborn: Character added with a null inventory!");
                    //MyAPIGateway.Utilities.ShowNotification("SurvivalReborn has encountered an error. Submit a bug report with your Space Engineers log.", 20000, "Red");
                }

                if (OxygenComponent == null)
                {
                    MyLog.Default.WriteLine("SurvivalReborn: Character added with a null Oxygen Component!");
                    //MyAPIGateway.Utilities.ShowNotification("SurvivalReborn has encountered an error. Submit a bug report with your Space Engineers log.", 20000, "Red");
                }

                // Set max speed according to Keen's algorithm in MyCharacter.UpdateCharacterPhysics() since I apparently can't access this value directly.
                var maxShipSpeed = Math.Max(MyDefinitionManager.Static.EnvironmentDefinition.LargeShipMaxSpeed, MyDefinitionManager.Static.EnvironmentDefinition.SmallShipMaxSpeed);
                var maxDudeSpeed = Math.Max(characterDef.MaxSprintSpeed,
                    Math.Max(characterDef.MaxRunSpeed, characterDef.MaxBackrunSpeed));
                MaxSpeed = maxShipSpeed + maxDudeSpeed;
                MaxSpeedSquared = MaxSpeed * MaxSpeed;

                //MyAPIGateway.Utilities.ShowNotification("Created character info with fuel capacity of " + FuelCapacity, 20000);

                // Initial inventory scan
                ScanInventory();
                //MyAPIGateway.Utilities.ShowNotification("Loaded your inventory and found " + InventoryBottles.Count + " hydrogen tanks.");
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
                if (HoldsFuel(arg2))
                    ScanInventory();
            }

            /// <summary>
            /// Scan this character's inventory for bottles that hold fuel for its jetpack.
            /// </summary>
            private void ScanInventory()
            {
                if (Inventory == null)
                    return;

                // Reset bottle list
                InventoryBottles.Clear();

                List<MyPhysicalInventoryItem> items = Inventory.GetItems();
                foreach (MyPhysicalInventoryItem item in items)
                {
                    // Add gas bottles to list
                    if (HoldsFuel(item))
                        InventoryBottles.Add(new SRInventoryBottle(item));
                }
                //MyAPIGateway.Utilities.ShowNotification("Scanned your inventory and found " + InventoryBottles.Count + " hydrogen tanks.");
            }

            /// <summary>
            /// Return true if this item is a gas container that holds fuel for this character's jetpack.
            /// </summary>
            private bool HoldsFuel(MyPhysicalInventoryItem item)
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
        Dictionary<IMyCharacter, SRCharacterInfo> m_characters = new Dictionary<IMyCharacter, SRCharacterInfo>();
        // List of characters to remove from m_characters this tick
        List<IMyCharacter> m_toRemove = new List<IMyCharacter>();

        // Game rules for fall damage - settings are in m/s/s
        const float DAMAGE_THRESHOLD = 750f;
        const float DAMAGE_THRESHOLD_SQ = 562500f;
        const float IGNORE_ABOVE = 1500f; // Should be roughly where vanilla damage starts
        const float DAMAGE_PER_MSS = 0.03f;

        // Defaults to restore on world close
        private float m_defaultCharacterGravity;
        private float m_defaultWalkAcceleration;
        private float m_defaultWalkDeceleration;
        private float m_defaultSprintAcceleration;
        private float m_defaultSprintDeceleration;

        public override void LoadData()
        {
            // Hook entity add for character list
            MyEntities.OnEntityAdd += MyEntities_OnEntityAdd;

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

            //MyLog.Default.WriteLine("SurvivalReborn: Loaded Spacewalk Stable 1.1.");
            MyLog.Default.WriteLine("SurvivalReborn: Loaded Spacewalk development testing version.");

        }

        protected override void UnloadData()
        {
            // Unhook events
            MyEntities.OnEntityAdd -= MyEntities_OnEntityAdd;

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
        /// Add each character spawned in the world to m_characters.
        /// </summary>
        private void MyEntities_OnEntityAdd(MyEntity obj)
        {
            if (m_characters == null)
            {
                MyAPIGateway.Utilities.ShowNotification("SurvivalReborn has encountered an error. Submit a bug report with your Space Engineers log.", 20000, "Red");
                MyLog.Default.Error("SurvivalReborn: Attempted to add a character, but m_characters was null.");
                return;
            }
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                // There will be a duplicate if the player changes suit in the Medical Room.
                // Duplicate must be removed and replaced to ensure the SRCharacterInfo is correct.
                if (m_characters.ContainsKey(character))
                {
                    m_characters[character].Close();
                    character.OnMarkForClose -= Character_OnMarkForClose;
                    m_characters.Remove(character);
                }

                // Add to dictionary
                var newCharacterInfo = new SRCharacterInfo(character);
                if (newCharacterInfo != null && newCharacterInfo.valid)
                {
                    m_characters.Add(character, newCharacterInfo);

                    // Prepare to remove character from list when it's removed from world (Remember to unbind this when the character's removed from dictionary)
                    character.OnMarkForClose += Character_OnMarkForClose;

                    MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " added to world. There are now " + m_characters.Count + " characters listed.");
                }
                else
                {
                    MyLog.Default.Warning("SurvivalReborn: Skipped adding an invalid or null character.");
                }
            }
        }

        /// <summary>
        /// Remove a character from m_characters when marked for close.
        /// </summary>
        /// <param name="obj"></param>
        private void Character_OnMarkForClose(IMyEntity obj)
        {
            // Ensure this is a character. Ignore otherwise.
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                m_characters[character].Close();
                m_characters.Remove(character);
                character.OnMarkForClose -= Character_OnMarkForClose;
            }
            MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " marked for close. There are now " + m_characters.Count + " characters listed.");
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
                    var trueFuelLvl = tanks.GetGasFillLevel(m_characters[character].FuelId) - correction.FuelAmount;
                    tanks.UpdateStoredGasLevel(ref m_characters[character].FuelId, trueFuelLvl);
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineToConsole("Survival Reborn: Spacewalk may be experiencing a network channel collision with another mod on channel 5064. This may impact performance. Submit a bug report with a list of mods you are using.");
                MyLog.Default.Error("Survival Reborn: Spacewalk may be experiencing a network channel collision with another mod on channel 5064. This may impact performance. Submit a bug report with a list of mods you are using.");
                MyLog.Default.WriteLineAndConsole(ex.Message);
                MyLog.Default.WriteLineAndConsole(ex.StackTrace);
            }
        }


        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            // Apply game rules to all living, unparented characters
            foreach (KeyValuePair<IMyCharacter, SRCharacterInfo> pair in m_characters)
            {
                IMyCharacter character = pair.Key;
                SRCharacterInfo characterInfo = pair.Value;

                // Remove character from list if it's dead or its parent is not null (entered a seat, etc.)
                if (character.Parent != null || character.IsDead)
                {
                    // Can't remove while iterating or enumeration might fail, so do it afterward
                    m_toRemove.Add(character);
                    // Don't do anything else to this character. We are done with it.
                    continue;
                }

                // JETPACK REFUELING RULE
                // OPTIMIZATION: Don't run refueling rule if there are no bottles in inventory.
                if (characterInfo.FuelId != null && characterInfo.OxygenComponent != null && characterInfo.InventoryBottles.Count > 0)
                {
                    // Check for jetpack changing state
                    // Should not be needed if bottle is added while jetpack is on, since the bottle's capacity is checked on add to inventory
                    if (character.EnabledThrusts != characterInfo.JetPackOn)
                    {
                        // When the jetpack is switched on, update the last known value of bottles to prevent refueling
                        if (character.EnabledThrusts)
                            foreach (var bottle in characterInfo.InventoryBottles)
                                bottle.lastKnownFillLevel = bottle.currentFillLevel;

                        characterInfo.JetPackOn = character.EnabledThrusts;
                        var vect = character.Physics.LinearVelocity;
                        MyLog.Default.WriteLine("SurvivalReborn: Jetpack activated. Rescanning inventory of " + character.DisplayName);
                    }

                    // Check for gas falling below threshold and begin checking for illegal refuels immediately.
                    if (characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) < MyCharacterOxygenComponent.GAS_REFILL_RATION)
                        characterInfo.GasLow = true;

                    // Prevent disallowed refueling.
                    // OPTIMIZATION: Only check this when the jetpack is on, there are bottles in inventory, and fuel is low enough to attempt refueling
                    if (character.EnabledThrusts && characterInfo.GasLow)
                    {
                        // Check for illegal refills
                        foreach (SRCharacterInfo.SRInventoryBottle bottle in characterInfo.InventoryBottles)
                        {
                            var delta = bottle.currentFillLevel - bottle.lastKnownFillLevel;
                            if (delta != 0f)
                            {
                                // Calculate correct amount to remove
                                float gasToRemove = -delta * bottle.capacity / characterInfo.FuelCapacity;
                                //MyAPIGateway.Utilities.ShowNotification("You weren't supposed to refuel. Removing " + gasToRemove + " hydrogen.");

                                // Set the fuel level back to what it should be.
                                float fixedGasLevel = characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) - gasToRemove;
                                characterInfo.OxygenComponent.UpdateStoredGasLevel(ref characterInfo.FuelId, fixedGasLevel);

                                // Put the gas back in the bottle
                                var badBottle = bottle.Item.Content as MyObjectBuilder_GasContainerObject;
                                badBottle.GasLevel = bottle.lastKnownFillLevel;

                                MyLog.Default.WriteLine("SurvivalReborn: Corrected a disallowed jetpack refuel for " + character.DisplayName);

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
                                        MyLog.Default.Error("SurvivalReborn: Server errored out while trying to send a packet. Submit a bug report.");
                                        MyLog.Default.WriteLineAndConsole(e.Message);
                                        MyLog.Default.WriteLineAndConsole(e.StackTrace);
                                    }

                                }
                            }
                        }
                    }

                    // Delayed check for gas fill level. If this isn't delayed by one tick, the illegal refill will prevent the check that's meant to find it.
                    if (characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) > MyCharacterOxygenComponent.GAS_REFILL_RATION)
                        characterInfo.GasLow = false;
                }

                // COLLISION DAMAGE RULE

                /// Skip collision damage for this character until it moves. This "hamfisted but genius" solution catches several edge cases and prevents false positives:
                /// 1. When leaving a seat
                /// 2. When respawning
                /// 3. On world load while moving and not in a seat
                /// The character receives a microscopic nudge to trip this check as soon as physics are ready.
                if (MyAPIGateway.Session.IsServer)
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
                            MyLog.Default.Error("SurvivalReborn: Linear acceleration calculations appear to have glitched out.");
                            MyLog.Default.Error("SurvivalReborn: Send a bug report and tell the developer what you were doing at the time the unexpected damage spike occurred!");
                        }
                        if(character.Physics.LinearVelocity.LengthSquared() == 0f)
                        {
                            MyLog.Default.Error("SurvivalReborn: Character's speed was set to zero and caused damage!");
                            MyLog.Default.Error("SurvivalReborn: Send a bug report and tell the developer what you were doing at the time the unexpected damage spike occurred!");
                        }

                        // We definitely crashed into something.
                        float damage = DAMAGE_PER_MSS * Math.Max(0, (Math.Min(IGNORE_ABOVE, (float)Math.Sqrt(accelSquared)) - DAMAGE_THRESHOLD));
                        character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        MyLog.Default.WriteLine("SurvivalReborn:" + character.DisplayName + " took " + damage + " collision damage from SR:Spacewalk game rules.");
                    }
                    // Update lastLinearVelocity each tick
                    characterInfo.lastLinearVelocity = character.Physics.LinearVelocity;
                }
            }

            // Remove characters from dictionary if needed
            // This cannot happen in the above loop as it might interrupt enumeration.
            foreach (var character in m_toRemove)
            {
                m_characters[character].Close();
                character.OnMarkForClose -= Character_OnMarkForClose;
                m_characters.Remove(character);
                MyLog.Default.WriteLine("SurvivalReborn: " + character.DisplayName + " reparented or died. There are now " + m_characters.Count + " characters listed.");
            }
            m_toRemove.Clear();
        }
    }

}
