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
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using VRage.Utils;
using VRageMath;

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
    /// TODO/OPTIMIZATION: Change collision damage checks each tick to compare acceleration LengthSquared to the square of the threshold.
    ///     When the threshold is passed, only then should I call .Length() which includes a square root.
    /// 
    /// Jetpack anti-refueling works regardless of the gas a specific character uses for fuel.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRSpacewalk : MySessionComponentBase
    {
        // Data structure for keeping track of info about players
        private class SRCharacterInfo
        {
            // VALUES FOR COLLISION DAMAGE RULE
            // If disabled, will skip checking for collision damage until enabled
            public bool CollisionDamageEnabled;
            // Force of a possible collision in m/s/s if one has occurred, otherwise == 0f
            //public float PossibleCollision;
            // Character's max movement speed
            public float MaxSpeed;
            // Max speed squared for optimized checks
            public float MaxSpeedSquared;
            // Linear velocity from the tick before collision damage was tripped
            public Vector3 lastLinearVelocity;

            // VALUES FOR JETPACK REFUELING RULE
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

            // Data structure for keeping track of gastanks in inventories and their last known values for no-refuel enforcement
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

                // Set max speed according to Keen's algorithm in MyCharacter.UpdateCharacterPhysics() since I apparently can't access this value directly.
                var maxShipSpeed = Math.Max(MyDefinitionManager.Static.EnvironmentDefinition.LargeShipMaxSpeed, MyDefinitionManager.Static.EnvironmentDefinition.SmallShipMaxSpeed);
                var maxDudeSpeed = Math.Max((character.Definition as MyCharacterDefinition).MaxSprintSpeed,
                    Math.Max((character.Definition as MyCharacterDefinition).MaxRunSpeed, (character.Definition as MyCharacterDefinition).MaxBackrunSpeed));
                MaxSpeed = maxShipSpeed + maxDudeSpeed;
                MaxSpeedSquared = MaxSpeed * MaxSpeed;

                //MyAPIGateway.Utilities.ShowNotification("Created character info with fuel capacity of " + FuelCapacity, 20000);

                // Initial inventory scan
                ScanInventory();
                //MyAPIGateway.Utilities.ShowNotification("Loaded your inventory and found " + InventoryBottles.Count + " hydrogen tanks.");
            }

            // Call before removing a character from the dictionary
            public void Close()
            {
                Inventory.InventoryContentChanged -= Inventory_InventoryContentChanged;
            }

            // Rescan the inventory if a hydrogen tank is added or removed
            // Since this doesn't appear to tell me whether it was added or removed, a full rescan is the only option. It's pretty fast anyway.
            private void Inventory_InventoryContentChanged(MyInventoryBase arg1, MyPhysicalInventoryItem arg2, VRage.MyFixedPoint arg3)
            {
                // Ignore anything that's not a fuel bottle
                if(HoldsFuel(arg2))
                    ScanInventory();
            }

            private void ScanInventory()
            {
                // Reset bottle list
                InventoryBottles.Clear();

                List<MyPhysicalInventoryItem> items = Inventory.GetItems();
                foreach (MyPhysicalInventoryItem item in items)
                {
                    // Add gas bottles to list
                    if(HoldsFuel(item))
                        InventoryBottles.Add(new InventoryBottle(item));
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
        const float DAMAGE_THRESHOLD_SQ = 562500f;
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

                // JETPACK REFUELING RULE

                // Check for jetpack changing state
                if(character.EnabledThrusts != characterInfo.JetPackOn)
                {
                    // When the jetpack is switched on, update the last known value of bottles to prevent refueling
                    if (character.EnabledThrusts)
                        foreach(var bottle in characterInfo.InventoryBottles)
                            bottle.lastKnownFillLevel = bottle.currentFillLevel;

                    characterInfo.JetPackOn = character.EnabledThrusts;
                    var vect = character.Physics.LinearVelocity;
                }

                // Check for gas falling below threshold and begin checking for illegal refuels immediately.
                if (characterInfo.OxygenComponent.GetGasFillLevel(characterInfo.FuelId) < MyCharacterOxygenComponent.GAS_REFILL_RATION)
                    characterInfo.GasLow = true;

                // Prevent disallowed refueling.
                // OPTIMIZATION: Only check this when the jetpack is on, there are bottles in inventory, and fuel is low enough to attempt refueling
                if (character.EnabledThrusts && characterInfo.InventoryBottles.Count != 0 && characterInfo.GasLow)
                {
                    //MyAPIGateway.Utilities.ShowNotification("Checking for illegal refills");
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

                // COLLISION DAMAGE RULE

                /// Skip collision damage for this character until it moves. This "hamfisted but genius" solution catches several edge cases and prevents false positives:
                /// 1. When leaving a seat
                /// 2. When respawning
                /// 3. On world load while moving and not in a seat
                /// The character receives a microscopic nudge to trip this as soon as physics are ready.
                var accelSquared = (60 * (characterInfo.lastLinearVelocity - character.Physics.LinearVelocity)).LengthSquared();

                if (!characterInfo.CollisionDamageEnabled)
                {
                    character.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE, 0.0001f * Vector3.Down, null, null);
                    if (character.Physics.LinearVelocity.LengthSquared() > 0)
                    {
                        characterInfo.CollisionDamageEnabled = true;
                        characterInfo.lastLinearVelocity = character.Physics.LinearVelocity; // Initialize for sanity on first movement.
                        MyAPIGateway.Utilities.ShowNotification("You moved. Collision damage enabled.");
                    }
                }
                // Trip collision damage on high G-force, but ignore if linear velocity is impossibly high
                else if (accelSquared > DAMAGE_THRESHOLD_SQ)
                //    && character.Physics.LinearVelocity.LengthSquared() < characterInfo.MaxSpeedSquared) // Running with safety off for debug reasons
                {
                    if(character.Physics.LinearVelocity.LengthSquared() < characterInfo.MaxSpeedSquared)
                    {
                        MyAPIGateway.Utilities.ShowNotification("Linear acceleration calculations appear to have glitched out.", 30000, "Red");
                        MyLog.Default.Error("SurvivalReborn: Linear acceleration calculations appear to have glitched out.");
                    }

                    // We definitely crashed into something. If you look reeeeeeally closely, you might see vanilla damage and this damage happen 1 tick apart.
                    float damage = DAMAGE_PER_MSS * Math.Max(0, (Math.Min(IGNORE_ABOVE, (float)Math.Sqrt(accelSquared)) - DAMAGE_THRESHOLD));
                    character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                    MyAPIGateway.Utilities.ShowNotification("Took " + damage + " collision damage.");
                }
                // Update lastLinearVelocity each tick
                    characterInfo.lastLinearVelocity = character.Physics.LinearVelocity;

                /*
                // If collision damage is tripped, perform sanity check and possibly damage.
                else if (characterInfo.PossibleCollision != 0f)
                {
                    // At this point, we are in the tick FOLLOWING the spike, and lastLinearVelocity is from BEFORE the spike.
                    // Now we compare LinearVelocity before and after the acceleration spike to see if the character snapped back to its original velocity in a teleport

                    // Acceleration squared over the past two phsyics ticks in m/s/s assuming 60 tps
                    var twoTickAccelerationSquared = (60 * (characterInfo.lastLinearVelocity - character.Physics.LinearVelocity)).LengthSquared();
                    if (twoTickAccelerationSquared > DAMAGE_THRESHOLD_SQ)
                    {
                        // We definitely crashed into something. If you look reeeeeeally closely, you might see vanilla damage and this damage happen 1 tick apart.
                        var damage = DAMAGE_PER_MSS * Math.Max(0, (Math.Min(IGNORE_ABOVE, characterInfo.PossibleCollision) - DAMAGE_THRESHOLD));
                        character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        MyAPIGateway.Utilities.ShowNotification("Took " + damage + " collision damage.");
                    }
                    // RESET possible collision
                    characterInfo.PossibleCollision = 0f;
                }
                // Trip collision damage on high G-force, but ignore if linear velocity is impossibly high
                else if (character.Physics.LinearAcceleration.LengthSquared() > DAMAGE_THRESHOLD_SQ && character.Physics.LinearVelocity.LengthSquared() < characterInfo.MaxSpeedSquared)
                {
                    // Multiply by 60 (ticks per second) to get m/s/s
                    characterInfo.PossibleCollision = character.Physics.LinearAcceleration.Length();
                    MyAPIGateway.Utilities.ShowNotification("Possible collision at " + characterInfo.PossibleCollision);

                    // Exploratory debug
                    var twoTickAccelerationSquared = (60 * (characterInfo.lastLinearVelocity - character.Physics.LinearVelocity)).LengthSquared();
                    if (twoTickAccelerationSquared > DAMAGE_THRESHOLD_SQ)
                    {
                        MyAPIGateway.Utilities.ShowNotification("This tick really did have insane acceleration");
                    }
                    else
                    {
                        MyAPIGateway.Utilities.ShowNotification("Tracking Linear acceleration would not have detected this.");
                    }
                }
                // If nothing's going on, just update lastLinearVelocity
                else if (characterInfo.CollisionDamageEnabled)
                {
                    characterInfo.lastLinearVelocity = character.Physics.LinearVelocity;
                }
                */
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
