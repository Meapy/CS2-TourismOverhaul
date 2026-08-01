using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// Opening-period marker on a hotel company.
    ///
    /// Presence means the hotel has been seen by <see cref="Systems.HotelWelcomeSystem"/>;
    /// <see cref="m_EndFrame"/> is the simulation frame its opening boost expires on. A value of 0
    /// means seen but never boosted, which is how existing hotels are recorded when a save is first
    /// loaded — otherwise every hotel in the city would look brand new and trigger a boost at once.
    ///
    /// Serialized so the opening period survives a save and reload rather than restarting.
    /// </summary>
    public struct HotelWelcome : IComponentData, IQueryTypeParameter, ISerializable
    {
        public uint m_EndFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_EndFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_EndFrame);
        }
    }
}
