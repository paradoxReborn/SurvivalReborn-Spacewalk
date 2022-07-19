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
    /// 
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRSpacewalk : MySessionComponentBase
    {
        // Ugly object-oriented way of doing this, but the nicer ways didn't play nicely.
        private class SRCharacterInfo
        {
            public SRCharacterInfo(IMyCharacter character)
            {
                //Character = character;
                //LastKnownParent = character.Parent;
                Inventory = (MyInventory)character.GetInventory();
                Inventory.ContentsAdded += Inventory_ContentsAdded; // Doesn't seem to work - maybe just rescan inventory on ContentsChanged
                Inventory.ContentsRemoved += Inventory_ContentsRemoved; 
                InventoryTanks = new List<InventoryTank>();
                OxygenComponent = character.Components.Get<MyCharacterOxygenComponent>();
                //LastHydrogenLevel = 1f; // init to safe value
                CollisionDamageEnabled = false; // disabled until character moves to prevent damage on world load on moving ship
                //CollisionDisabledForFrames = 2; // disabled for two frames to prevent damage on respawn on moving ship

                // Scan inventory for H2 tanks
                MyInventory inv = character.GetInventory() as MyInventory;
                List<MyPhysicalInventoryItem> items = inv.GetItems();
                foreach(MyPhysicalInventoryItem item in items)
                {
                    var gasItem = item.Content as MyObjectBuilder_GasContainerObject;
                    if (gasItem != null)
                    {
                        InventoryTanks.Add(new InventoryTank(item));
                    }
                }
                MyAPIGateway.Utilities.ShowNotification("Loaded your inventory and found " + InventoryTanks.Count + " hydrogen tanks.");
            }

            // When something's added to this inventory, check if it's a tank and list it if so.
            // BUG: This event doesn't fire when a player adds an item to inventory.
            // TODO: Switch to ContentsChanged and re-scan whole inventory. :(
            private void Inventory_ContentsAdded(MyPhysicalInventoryItem item, VRage.MyFixedPoint arg2)
            {
                MyAPIGateway.Utilities.ShowNotification("Called ContentsAdded");
                MyObjectBuilder_GasContainerObject gasItem = item.Content as MyObjectBuilder_GasContainerObject;
                if (gasItem != null)
                {
                    InventoryTanks.Add(new InventoryTank(item));
                }
                MyAPIGateway.Utilities.ShowNotification("There are now " + InventoryTanks.Count + " hydrogen tanks in your inventory.");
            }

            // Remove a tank from the list when it's removed from inventory.
            private void Inventory_ContentsRemoved(MyPhysicalInventoryItem item, VRage.MyFixedPoint arg2)
            {
                MyAPIGateway.Utilities.ShowNotification("Called ContentsRemoved");
                MyObjectBuilder_GasContainerObject gasItem = item.Content as MyObjectBuilder_GasContainerObject;
                if (gasItem != null)
                {
                    foreach (InventoryTank tank in InventoryTanks)
                    {
                        if (tank.Item.Equals(item))
                        {
                            // Found it - remove it and exit loop
                            InventoryTanks.Remove(tank);
                            break;
                        }
                    }
                    MyAPIGateway.Utilities.ShowNotification("There are now " + InventoryTanks.Count + " hydrogen tanks in your inventory.");
                }
            }

            //public IMyCharacter Character;
            // Last parent for detecting change of parent
            //public IMyEntity LastKnownParent;
            // If disabled, will skip checking for collision damage until enabled
            public bool CollisionDamageEnabled;
            // If greater than zero, will skip collision damage for this number of frames
            //public int CollisionDisabledForFrames;

            // Must monitor character's inventory for Hydrogen tanks
            public MyInventory Inventory;
            // Maintained list of hydrogen tanks in player's inventory
            public List<InventoryTank> InventoryTanks;
            // Character's oxygencomponent stores hydrogen, oxygen, etc.
            public MyCharacterOxygenComponent OxygenComponent;
            // Hydrogen level last tick for detecting changes
            //public float LastHydrogenLevel;
            // Jetpack state last tick for detecting changes
            //public bool JetpackOn;
        }

        // Small class for keeping track of gastanks in inventories and their last known values for no-refuel enforcement
        private struct InventoryTank
        {
            public MyPhysicalInventoryItem Item;
            public float lastKnownCapacity;
            public float currentCapacity { get { return (Item.Content as MyObjectBuilder_GasContainerObject).GasLevel; } }
            public InventoryTank(MyPhysicalInventoryItem item)
            {
                Item = item;
                lastKnownCapacity = (Item.Content as MyObjectBuilder_GasContainerObject).GasLevel;
            }

        }

        // List of characters to apply game rules to
        //List<SRCharacterInfo> m_characterInfos = new List<SRCharacterInfo>();
        Dictionary<IMyCharacter,SRCharacterInfo> m_characters = new Dictionary<IMyCharacter,SRCharacterInfo>();
        // List of character infos to remove at the end of this tick
        //List<SRCharacterInfo> m_toRemove = new List<SRCharacterInfo>();
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
                // Add to list
                // BUG: This stupid event fires any time I get out of a seat. Need to make sure list doesn't already have a character before adding it.
                // Better idea: just keep a list of characters that aren't in a seat.
                m_characters.Add(character,new SRCharacterInfo(character));

                // Prepare to remove character from list when it's removed from world (Remember to unbind this when the character's removed from list)
                character.OnMarkForClose += Character_OnMarkForClose;

                MyAPIGateway.Utilities.ShowNotification("There are now " + m_characters.Count + " characters listed.");
            }
        }

        // Remove character from list when it's removed from the world.
        private void Character_OnMarkForClose(IMyEntity obj)
        {
            // Ensure this is a character. Ignore otherwise.
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                m_characters.Remove(character);
            }
            MyAPIGateway.Utilities.ShowNotification("There are now " + m_characters.Count + " characters listed.");
        }

        /*
        private void RemoveInfoByCharacter(IMyCharacter character)
        {
            // Find and remove this character
            foreach (SRCharacterInfo info in m_characterInfos)
            {
                if (info.Character.Equals(character))
                {
                    // Found it. Remove it and exit loop.
                    m_characterInfos.Remove(info);
                    break;
                }
            }
            character.OnMarkForClose -= Character_OnMarkForClose;
        }
        */

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            // Apply game rules to all living, unparented characters
            //foreach (SRCharacterInfo info in m_characterInfos)
            foreach (KeyValuePair<IMyCharacter, SRCharacterInfo> pair in m_characters)
            {
                IMyCharacter character = pair.Key;
                SRCharacterInfo info = pair.Value;
                // Remove character from list if it's dead or its parent is not null (entered a seat or something)
                if(character.Parent != null || character.IsDead)
                {
                    // Can't remove while iterating or enumeration might fail, so do it afterward
                    m_toRemove.Add(character);
                    MyAPIGateway.Utilities.ShowNotification("A character will be removed. There will be " + (m_characters.Count - 1) + " remaining.");
                    // Don't do anything else to this character. We are done with it.
                    continue;
                }

                // Naive proof of concept. Still needs more optimization:
                // TODO: only check tanks if the fuel level's actually low enough to try to refuel.
                // TODO OPTIMIZATION: Once the first refuel attempt is made, schedule checks every 5000 (or is it 5001?) milliseconds when the refueling actually takes place.
                if(character.EnabledThrusts && info.InventoryTanks.Count != 0)
                {
                    // Check for illegal refills

                    // Undo illegal refills

                    // Schedule next check for illegal refills
                }

                // Skip collision damage for this character until it moves. This ensures phsyics have been fully loaded.
                // Bug? - This can affect characters that have just spawned from a seat. Small movements usually trip it, though.
                if (!info.CollisionDamageEnabled)
                {
                    if (character.Physics.LinearAcceleration.Length() > 0)
                    {
                        info.CollisionDamageEnabled = true;
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
                        MyLog.Default.WriteLine("SurvivalReborn: Did collision damage for " + accel + " m/s/s");
                        MyAPIGateway.Utilities.ShowNotification("DAMAGE! " + accel + " m/s/s", 10000, "Red");
                    }
                }
            }

            // Remove characters from list if needed
            foreach(var character in m_toRemove)
            {
                character.OnMarkForClose -= Character_OnMarkForClose;
                m_characters.Remove(character);
            }
            m_toRemove.Clear();

        }
    }

}
