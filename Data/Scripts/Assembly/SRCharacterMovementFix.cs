using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using Sandbox.ModAPI;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Components;
using VRage.ObjectBuilders;
using VRage.Game;
using VRage.Utils;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Entities.Character.Components;

namespace SurvivalReborn
{
    /// <summary>
    /// Fixes some aspects of character movement that could not be set through sbc definitions:
    /// - Remove supergravity
    /// - Lower fall/collision damage threshold
    /// - Smooth out player movement a bit
    /// After playing with these settings, vanilla may feel a bit "jerky" or jarring.
    /// Defaults are restored on world close.
    /// 
    /// TODO: I want to add "recoil" on damage both from impacts and attacks.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    class SRCharacterMovementFix : MySessionComponentBase
    {
        // Ugly object-oriented way of doing this, but the nicer ways didn't play nicely.
        private class SRCharacterInfo
        {
            public SRCharacterInfo(IMyCharacter character)
            {
                LastKnownParent = character.Parent;
                CollisionDamageDisabled = true; // disabled until character moves to prevent damage on world load on moving ship
                CollisionDisabledForFrames = 2; // disabled for two frames to prevent damage on respawn on moving ship
            }

            public IMyEntity LastKnownParent;
            public bool CollisionDamageDisabled;
            public int CollisionDisabledForFrames;
        }

        // List of characters to apply fall damage game rule to
        Dictionary<IMyCharacter, SRCharacterInfo> charactersInWorld = new Dictionary<IMyCharacter,SRCharacterInfo>();
        List<IMyCharacter> charactersToUpdate = new List<IMyCharacter>();

        // Game rules for fall damage - settings should be in m/s/s
        const float DAMAGE_THRESHOLD = 750f;
        const float IGNORE_ABOVE = 1500f; // Should be roughly where vanilla damage starts
        const float DAMAGE_PER_MSS = 0.04f;

        public override void LoadData()
        {
            // Hook entity add for character list
            MyEntities.OnEntityAdd += MyEntities_OnEntityAdd;

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

            // Restore defaults
            MyPerGameSettings.CharacterGravityMultiplier = 2f;
            MyPerGameSettings.CharacterMovement.WalkAcceleration = 50f;
            MyPerGameSettings.CharacterMovement.WalkDecceleration = 10f;
            MyPerGameSettings.CharacterMovement.SprintAcceleration = 100f;
            MyPerGameSettings.CharacterMovement.SprintDecceleration = 20f;

            //Log
            MyLog.Default.WriteLine("SurvivalReborn: MyPerGameSettings returned to defaults.");
        }

        private void MyEntities_OnEntityAdd(VRage.Game.Entity.MyEntity obj)
        {
            IMyCharacter character = obj as IMyCharacter;
            if (character != null && !charactersInWorld.ContainsKey(character)) // This could be inefficient
            {
                //Add to list
                charactersInWorld.Add(character, new SRCharacterInfo(character));
                //charsInWorld.Add(new MyCharacterWrapper(character));
                character.OnMarkForClose += Character_OnMarkForClose;
            }
        }

        private void Character_OnMarkForClose(IMyEntity obj)
        {
            //Remove from list
            IMyCharacter character = obj as IMyCharacter;
            if (character != null)
            {
                charactersInWorld.Remove(character);
                //charsInWorld.Remove(character);
                character.OnMarkForClose -= Character_OnMarkForClose;
            }
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            //if (MyAPIGateway.Session.Player.Character.Physics.LinearAcceleration.Length() > 500f) MyAPIGateway.Utilities.ShowNotification("OOF");
            foreach (KeyValuePair<IMyCharacter, SRCharacterInfo> entry in charactersInWorld)
            {
                IMyCharacter character = entry.Key;
                SRCharacterInfo info = entry.Value;

                if (info.CollisionDamageDisabled)
                {
                    if(character.Physics.LinearAcceleration.Length() > 0)
                    {
                        info.CollisionDamageDisabled = false;
                    }
                    continue;
                }

                // If parent changed this frame, ignore collision damage and update last known parent.
                if (info.LastKnownParent != character.Parent)
                {
                    charactersToUpdate.Add(character);
                }
                // If parent didn't change this frame, go ahead with possible collision damage unless it's disabled
                //else if (!info.CollisionDamageDisabled)
                else if (info.CollisionDisabledForFrames <= 0)
                {
                    float accel = character.Physics.LinearAcceleration.Length();
                    if (accel > DAMAGE_THRESHOLD)
                    {
                        float damage = Math.Min(IGNORE_ABOVE * DAMAGE_PER_MSS, DAMAGE_PER_MSS * (accel - DAMAGE_THRESHOLD));
                        character.DoDamage(damage, MyStringHash.GetOrCompute("Environment"), true);
                        MyLog.Default.WriteLine("Did collision damage for " + accel + " m/s/s");
                        MyAPIGateway.Utilities.ShowNotification("OOF! " + accel + " m/s/s", 5000, "Red");
                    }
                }
                else
                {
                    //info.CollisionDamageDisabled = false;
                    info.CollisionDisabledForFrames--;
                }
            }

            // Can't modify charactersInWorld while enumerating through it, so do it here.
            foreach (IMyCharacter character in charactersToUpdate)
            {
                var info = charactersInWorld[character];

                // Update parent and disable collision damage for the next frame.
                // (Damage would normally occur on the following frame)
                info.LastKnownParent = character.Parent;
                //info.CollisionDamageDisabled = true;
                info.CollisionDisabledForFrames = 2;
                MyAPIGateway.Utilities.ShowNotification("Character parent changed to " + character.Parent);
            }
            charactersToUpdate.Clear();
        }
    }
    
}
