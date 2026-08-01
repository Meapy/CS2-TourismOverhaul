using System.Runtime.InteropServices;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// Marks a district as preferring hotels.
    ///
    /// Deliberately a marker on the district entity and nothing else — the zoning inside stays
    /// ordinary commercial zoning. That is what makes the feature safe to remove: the zone cells
    /// hold a stock commercial ZoneType throughout, so uninstalling the mod leaves normal
    /// commercial areas rather than cells referencing a zone that no longer exists.
    ///
    /// A custom zone could not offer that. ZoneSpawnSystem.cs:288 matches buildings to a zone by
    /// comparing BuildingSpawnGroupData.m_ZoneType — a shared component holding exactly one
    /// ZoneType per building prefab — against the zone's ZoneData.m_ZoneType, and cells store that
    /// ushort index in the save. A new zone would have no buildings and would leave unresolvable
    /// indices behind on uninstall.
    /// </summary>
    /// <remarks>
    /// Implements IEmptySerializable, not ISerializable. A marker has no fields, and an
    /// ISerializable component that writes nothing makes the save system throw
    /// "Nothing serialized for component null. Use IEmptySerializable instead" — it saves the
    /// component's presence and expects payload. IEmptySerializable is the contract for
    /// presence-only components, which is exactly what this is.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    public struct HotelDistrict : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }
}
