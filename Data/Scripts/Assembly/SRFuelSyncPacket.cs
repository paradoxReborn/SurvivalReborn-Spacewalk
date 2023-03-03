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

using ProtoBuf;
using Sandbox.ModAPI;
using VRage;
using VRage.Utils;

namespace SurvivalReborn
{
    [ProtoContract]
    public class SRFuelSyncPacket
    {
        public SRFuelSyncPacket() { }

        public SRFuelSyncPacket(long entityId, float fuelCorrection)
        {
            EntityId = entityId;
            FuelAmount = fuelCorrection;
        }

        /// <summary>
        /// EntityId of the character to re-sync
        /// </summary>
        [ProtoMember(1)]
        public readonly long EntityId;

        /// <summary>
        /// Amount to be added or removed
        /// </summary>
        [ProtoMember(2)]
        public readonly float FuelAmount;
    }
}
