using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// Remembers which outside connection type a tourist household came through, until it is
    /// counted.
    ///
    /// Arrivals used to be tallied at dispatch, the moment the connection was resolved and before
    /// the household existed. That number was honest about what it measured and useless as a
    /// figure: most dispatched households never become visitors, so the panel reported roughly
    /// fifteen times more arrivals than the city could account for. A quarter of a million a month
    /// against eighteen thousand tourists, with under a thousand recorded departures — three
    /// numbers that cannot all be true of the same people.
    ///
    /// Carrying the mode on the household instead lets the count happen when the household is
    /// actually given citizens, so the panel reports visitors who arrived rather than attempts that
    /// were made. The component is removed once counted, which is also what marks it as counted.
    ///
    /// Serialized because a household can be created just before a save and initialised just after.
    /// </summary>
    public struct ArrivalMode : IComponentData, ISerializable
    {
        /// <summary>0 road, 1 train, 2 air, 3 ship — the order used by the panel rows.</summary>
        public byte m_Mode;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            byte mode = m_Mode;
            writer.Write(mode);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte mode);
            m_Mode = mode;
        }
    }
}
