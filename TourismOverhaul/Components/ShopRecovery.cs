using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// The over-built shop recovery's progress, held on a singleton so it survives a reload.
    ///
    /// Its presence is what says this city's recovery has been decided: it is created when a city
    /// loads over-built, and kept with <see cref="m_Finished"/> set once the ramp ends, so a
    /// recovery runs at most once per city. See ShopRecoverySystem.
    /// </summary>
    public struct ShopRecoveryData : IComponentData, ISerializable
    {
        /// <summary>Layout version, written first. Add fields at the end and bump it.</summary>
        private const int kVersion = 1;

        /// <summary>Simulation frame the recovery began on.</summary>
        public uint m_StartFrame;

        /// <summary>Set once the ramp has run its course; the cap no longer applies.</summary>
        public byte m_Finished;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_StartFrame);
            writer.Write(m_Finished);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out m_StartFrame);
            reader.Read(out m_Finished);
        }
    }
}
