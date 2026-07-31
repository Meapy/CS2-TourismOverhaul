using System.Runtime.InteropServices;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// Runtime-only marker placed on creature entities that this mod outlined, so the highlight
    /// system only ever removes highlights it added itself. Without it we would strip the
    /// <see cref="Game.Tools.Highlighted"/> component that selection and tools legitimately place
    /// on non-tourist pedestrians.
    ///
    /// Deliberately not serialized: outlines are a view concern and are rebuilt from scratch after
    /// a load.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    public struct TouristOutline : IComponentData, IQueryTypeParameter
    {
    }
}
