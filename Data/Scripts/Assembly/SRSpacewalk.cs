using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using Sandbox.ModAPI;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities;
using Sandbox.Definitions;
using VRage.Utils;

namespace SurvivalReborn
{
    /// <summary>
    /// To avoid creating multiple separate lists of characters in the world, multiple features are included in this class:
    /// - Movement tweaks
    /// - Collision damage changes
    /// - Anti-refueling while jetpack is on
    /// 
    /// Fixes some aspects of character movement that could not be set through sbc definitions:
    /// - Remove supergravity
    /// - Lower fall/collision damage threshold
    /// - Smooth out player movement a bit
    /// Defaults are restored on world close in case the mod is removed. Otherwise the world would keep the modded global values.
    /// 
    /// Jetpack anti-refueling works regardless of the gas a specific character uses for fuel.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRSpacewalk : MySessionComponentBase
    {
        // Data structure for keeping track of info about players
        private class SRCharacterInfo
        {
            // If disabled, will skip checking for collision damage until enabled
            public bool CollisionDamageEnabled;
            // Must monitor character's inventory for Hydrogen tanks
            public MyInventory Inventory;
            // Maintained list of hydrogen tanks in player's inventory
            public List<InventoryBottle> InventoryBottles;
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

            // Small struct for keeping track of gastanks in inventories and their last known values for no-refuel enforcement
            public class InventoryBottle
            {
                public MyPhysicalInventoryItem Item;
                public float capacity;
                public float lastKnownFillLevel;
                public float currentFillLevel { get { return (Item.Content as MyObjectBuilder_GasContainerObject).GasLevel; } }
                public InventoryBottle(MyPhysicalInventoryItem item)
                {
                    Item = item;
                    lastKnownFillLevel = currentFillLevel;
                    var gasBottleDefinition = MyDefinitionManager.Static.GetPhysicalItemDefinition(item.Content.GetId()) as MyOxygenContainerDefinition;
                    capacity = gasBottleDefinition.Capacity;
                }
            }

            public SRCharacterInfo(IMyCharacter character)
            {
                // Find the fuel this character uses
                string fuelName = (character.Definition as MyCharacterDefinition).Jetpack.ThrustProperties.FuelConverter.FuelId.SubtypeName;
                MyDefinitionId.TryParse("MyObjectBuilder_GasProperties/" + fuelName, out FuelId);

                // Look through character's stored gasses to find fuel, and record its capacity.
                var storedGasses = (character.Definition as MyCharacterDefinition).SuitResourceStorage;
                foreach(var gas in storedGasses)
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

                Inventory = (MyInventory)character.GetInventory();
                Inventory.InventoryContentChanged += Inventory_InventoryContentChanged;
                InventoryBottles = new List<InventoryBottle>();
                OxygenComponent = character.Components.Get<MyCharacterOxygenComponent>();
                CollisionDamageEnabled = false; // disabled until character moves to prevent damage on world load on moving ship
                JetPackOn = character.EnabledThrusts;

                //MyAPIGateway.Utilities.ShowNotification("Created character info with fuel capacity of " + FuelCapacity, 20000);

                // Initial inventory scan
                // BUG: It sees oxygen tanks as hydrogen tanks because they have a subtype relationship
                ScanInventory();
                MyAPIGateway.Utilities.ShowNotification("Loaded your inventory and found " + InventoryBottles.Count + " hydrogen tanks.");
            }

            // Call before removing a character from the dictionary
            public void Close()
            {
                Inventory.InventoryContentChanged -= Inventory_InventoryContentChanged;
            }

            // Rescan the inventory if a hydrogen tank is added or removed
            // Since this doesn't appear to tell me whether it was added or removed, a full rescan is the only option.
            // TODO: detect added or removed items
            private void Inventory_InventoryContentChanged(MyInventoryBase arg1, MyPhysicalInventoryItem arg2, VRage.MyFixedPoint arg3)
            {
                var itemTypeID = arg2.Content.GetId();
                MyDefinitionId hydrogenTankID;
                MyDefinitionId.TryParse("MyObjectBuilder_GasContainerObject/HydrogenBottle", out hydrogenTankID);

                // Ignore anything that's not a fuel bottle
                if(HoldsFuel(arg2))
                {
                    ScanInventory();
                }
            }

            private void ScanInventory()
            {
                // Reset bottle list

                InventoryBottles.Clear();

                MyInventory inv = Inventory as MyInventory;
                List<MyPhysicalInventoryItem> items = inv.GetItems();
                foreach (MyPhysicalInventoryItem item in items)
                {
                    // Add gas bottles to list
                    if(HoldsFuel(item))
                    {
                        //if (HoldsHydrogen(item))
                            //MyAPIGateway.Utilities.ShowNotification("Found an item that holds hydrogen in your inventory.");
                        InventoryBottles.Add(new InventoryBottle(item));
                    }
                }
                //MyAPIGateway.Utilities.ShowNotification("Scanned your inventory and found " + InventoryBottles.Count + " hydrogen tanks.");
            }

            private bool HoldsFuel(MyPhysicalInventoryItem item)
            {
                // OPTIMIZATION to prevent unnecessary calls to GetPhysicalItemDefinition
                if (item.Content as MyObjectBuilder_GasContainerObject == null)
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
        Dictionary<IMyCharacter,SRCharacterInfo> m_characters = new Dictionary<IMyCharacter,SRCharacterInfo>();
        // List of characters to remove from dictionary this tick
        List<IMyCharacter> m_toRemove = new List<IMyCharacter>();

        // Game rules for fall damage - settings are in m/s/s
        const float DAMAGE_THRESHOLD = 750f;
        const float IGNORE_ABOVE = 1500f; // Should be roughly where vanilla damage starts
        const float DAMAGE_PER_MSS = 0.03f;

        // Defaults to restore
        private float m_defaultCharacterGravity;
        private float m_defaultWalkAcceleration;
        private float m_defaultWalkDeceleration;
        private float m_defaultSprintAcceleration;
        private float m_defaultSprintDeceleration;

        public override void LoadData()
        {
            // Hook entity add for character list
            MyEntities.OnEntityAdd += MyEntities_OnEntityAdd;

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

        }

        protected override void UnloadData()
        {
            // Unhook events
            MyEntities.OnEntityAdd -= MyEntities_OnEntityAdd;

            // Restore all defaults
            MyPerGameSettings.CharacterGravityMultiplier = m_defaultCharacterGravity;
            MyPerGameSettings.CharacterMovement.WalkAcceleration = m_defaultWalkAcceleration;
            MyPerGameSettings.CharacterMovement.WalkDecceleration = m_defaultWalkDeceleration;
            MyPerGameSettings.CharacterMovement.SprintAcceleration = m_defaultSprintAcceleration;
            MyPerGameSettings.CharacterMovement.SprintDecceleration = m_defaultSprintDeceleration;

            //Log
            MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings returned to defaults.");
        }

        private void MyEntities_OnEntityAdd(VRage.Game.Entity.MyEntity obj)
        {
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                // Add to dictionary
                m_characters.Add(character,new SRCharacterInfo(character));

                // Prepare to remove character from list when it's removed from world (Remember to unbind this when the character's removed from dictionary)
                character.OnMarkForClose += Character_OnMarkForClose;

                //MyAPIGateway.Utilities.ShowNotification("Added a character with " + m_characters[character].FuelCapacity + " fuel capacity", 20000);
                //MyAPIGateway.Utilities.ShowNotification("There are now " + m_characters.Count + " characters listed.");
            }
        }

        // Remove character from list when it's removed from the world.
        private void Character_OnMarkForClose(IMyEntity obj)
        {
            // Ensure this is a character. Ignore otherwise.
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                m_characters[character].Close();
                m_characters.Remove(character);
            }
            //MyAPIGateway.Utilities.ShowNotification("There are now " + m_characters.Count + " characters listed.");
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            // Apply game rules to all living, unparented characters
            foreach (KeyValuePair<IMyCharacter, SRCharacterInfo> pair in m_characters)
            {
                IMyCharacter character = pair.Key;
                SRCharacterInfo characterInfo = pair.Value;
                // Remove character from list if it's dead or its parent is not null (entered a seat or something)
                if(character.Parent != null || character.IsDead)
                {
                    // Can't remove while iterating or enumeration might fail, so do it afterward
                    m_toRemove.Add(character);
                    //MyAPIGateway.Utilities.ShowNotification("A character will be removed. There will be " + (m_characters.Count - 1) + " remaining.");
                    // Don't do anything else to this character. We are done with it.
                    continue;
                }

                // Check for jetpack changing state
                if(character.EnabledThrusts != characterInfo.JetPackOn)
                {
                    // When the jetpack is switched on, update the last known value of bottles to prevent refueling
                    if (character.EnabledThrusts)
                        foreach(var bottle in characterInfo.InventoryBottles)
                            bottle.lastKnownFillLevel = bottle.currentFillLevel;

                    characterInfo.JetPackOn = character.EnabledThrusts;
                }

                // Check for gas falling below threshold and begin checking for illegal refuels immediately.
                if (characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) < MyCharacterOxygenComponent.GAS_REFILL_RATION)
                    characterInfo.GasLow = true;

                // Prevent disallowed refueling.
                // OPTIMIZATION: Only check this when the jetpack is on, there are bottles in inventory, and fuel is low enough to attempt refueling
                if (character.EnabledThrusts && characterInfo.InventoryBottles.Count != 0 && characterInfo.GasLow)
                {
                    MyAPIGateway.Utilities.ShowNotification("Checking for illegal refills");
                    // Check for illegal refills
                    foreach (SRCharacterInfo.InventoryBottle bottle in characterInfo.InventoryBottles)
                    {
                        //MyLog.Default.WriteLine("Tank current capacity: " + bottle.currentFillLevel + ", last known capacity: " + bottle.lastKnownFillLevel);
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
                        }
                    }
                }

                // Delayed check for gas fill level. If this isn't delayed by one tick, the illegal refill will prevent the check that's meant to find it.
                if (characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) > MyCharacterOxygenComponent.GAS_REFILL_RATION)
                    characterInfo.GasLow = false;

                // Skip collision damage for this character until it moves. This ensures phsyics have been fully loaded.
                // This can affect characters that have just spawned from a seat, but characters usually get nudged a little bit, which will trip it.
                if (!characterInfo.CollisionDamageEnabled)
                {
                    if (character.Physics.LinearAcceleration.Length() > 0)
                    {
                        characterInfo.CollisionDamageEnabled = true;
                    }
                    // Enable collision damage on the next tick.
                    continue;
                }
                // If enabled, check for and do collision damage
                else
                {
                    float accel = character.Physics.LinearAcceleration.Length();
                    if (accel > DAMAGE_THRESHOLD)
                    {
                        float damage = Math.Min(IGNORE_ABOVE * DAMAGE_PER_MSS, DAMAGE_PER_MSS * (accel - DAMAGE_THRESHOLD));
                        character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        //MyLog.Default.WriteLine("SurvivalReborn: Did collision damage for " + accel + " m/s/s");
                        //MyAPIGateway.Utilities.ShowNotification("DAMAGE! " + accel + " m/s/s", 10000, "Red");
                    }
                }
            }

            // Remove characters from dictionary if needed
            foreach(var character in m_toRemove)
            {
                m_characters[character].Close();
                character.OnMarkForClose -= Character_OnMarkForClose;
                m_characters.Remove(character);
            }
            m_toRemove.Clear();

        }
    }

}
