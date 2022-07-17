using System;
using System.Collections.Generic;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using Sandbox.ModAPI;
using Sandbox.Game;
using Sandbox.Game.Entities;
using VRage.Utils;

namespace SurvivalReborn
{
    /// <summary>
    /// Fixes some aspects of character movement that could not be set through sbc definitions:
    /// - Remove supergravity
    /// - Lower fall/collision damage threshold
    /// - Smooth out player movement a bit
    /// After playing with these settings, vanilla may feel a bit "jerky" or jarring.
    /// Defaults are restored on world close in case the mod is removed.
    /// This does not save any data to the game's save file.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRCharacterMovementFix : MySessionComponentBase
    {
        // Ugly object-oriented way of doing this, but the nicer ways didn't play nicely.
        private class SRCharacterInfo
        {
            public SRCharacterInfo(IMyCharacter character)
            {
                Character = character;
                LastKnownParent = character.Parent;
                CollisionDamageEnabled = false; // disabled until character moves to prevent damage on world load on moving ship
                CollisionDisabledForFrames = 2; // disabled for two frames to prevent damage on respawn on moving ship
            }

            public IMyCharacter Character;
            public IMyEntity LastKnownParent;
            public bool CollisionDamageEnabled;
            public int CollisionDisabledForFrames;
        }

        // List of characters to apply fall damage game rule to
        List<SRCharacterInfo> m_characterInfos = new List<SRCharacterInfo>();

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
                m_characterInfos.Add(new SRCharacterInfo(character));
                // Prepare to remove character from list when it's removed from world
                character.OnMarkForClose += Character_OnMarkForClose;
            }
        }

        // Remove character from list when it's removed from the world.
        private void Character_OnMarkForClose(IMyEntity obj)
        {
            // Ensure this is a character. Ignore otherwise.
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                // Remove character and log error if it's not present
                if (!RemoveCharacterFromInfos(character))
                    MyLog.Default.Error("SurvivalReborn: Attempted to remove a nonexistent character from characterInfos, or removal failed.");

                character.OnMarkForClose -= Character_OnMarkForClose;
            }
        }

        // Remove character from infos if present and return true. Return false on failure.
        // This isn't terribly efficient, but only needs to execute once per character and SE doesn't support huge player counts.
        private bool RemoveCharacterFromInfos(IMyCharacter character)
        {
            foreach (SRCharacterInfo info in m_characterInfos)
            {
                if (info.Character.Equals(character))
                {
                    m_characterInfos.Remove(info);
                    return true;
                }
            }
            return false;
        }


        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            foreach (SRCharacterInfo info in m_characterInfos)
            {
                // Skip updating this character until it moves. This ensures phsyics have been fully loaded.
                if (!info.CollisionDamageEnabled)
                {
                    if (info.Character.Physics.LinearAcceleration.Length() > 0)
                    {
                        info.CollisionDamageEnabled = true;
                    }
                    continue;
                }

                // If parent changed this frame, ignore collision damage and update last known parent.
                if (info.LastKnownParent != info.Character.Parent)
                {
                    // Update parent and disable collision damage for the next frame.
                    // (Damage would normally occur on the following frame)
                    info.LastKnownParent = info.Character.Parent;
                    info.CollisionDisabledForFrames = 2;
                    MyLog.Default.WriteLine("SurvivalReborn: Character parent changed to " + info.Character.Parent + ". collision damage disabled for " + info.CollisionDisabledForFrames + " frames.");
                    //MyAPIGateway.Utilities.ShowNotification("Character parent changed to " + character.Parent);
                }
                // If parent didn't change this frame, go ahead with possible collision damage unless it's disabled
                else if (info.CollisionDisabledForFrames <= 0)
                {
                    float accel = info.Character.Physics.LinearAcceleration.Length();
                    if (accel > DAMAGE_THRESHOLD)
                    {
                        float damage = Math.Min(IGNORE_ABOVE * DAMAGE_PER_MSS, DAMAGE_PER_MSS * (accel - DAMAGE_THRESHOLD));
                        info.Character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        MyLog.Default.WriteLine("SurvivalReborn: Did collision damage for " + accel + " m/s/s");
                        //MyAPIGateway.Utilities.ShowNotification("DAMAGE! " + accel + " m/s/s", 10000, "Red");
                    }
                }
                // If collision damage is still disabled for some number of frames, decrement that number.
                else
                {
                    info.CollisionDisabledForFrames--;
                }
            }
        }
    }

}
