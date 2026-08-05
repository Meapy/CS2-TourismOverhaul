using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// The trailing-month arrival window, in a form the game will save.
    ///
    /// The "/mo." row is meant to read as distinct visitors who arrived over the last in-game
    /// month — a companion to "Tourists in city", which is who is standing in the city now. That
    /// needs a window that slides, not a counter that resets, and it has to survive a reload: a
    /// displayed month is one in-game day, over an hour of real play, so a figure that starts
    /// again from zero on load spends most of its life meaningless. Two earlier attempts at this
    /// row failed on exactly that.
    ///
    /// So the counts live on a singleton entity, which is what CS2 serializes. System fields are
    /// not persisted; entities and their components are.
    ///
    /// This component holds the cursor. The counts themselves are in the
    /// <see cref="TouristArrivalBucket"/> buffer on the same entity.
    /// </summary>
    public struct TouristArrivalWindowData : IComponentData, ISerializable
    {
        /// <summary>
        /// Layout version, written first and always.
        ///
        /// TourismLedger shipped without one and adding a field made every existing save throw
        /// ComponentSerializerException. Add fields at the end, bump this, and read them
        /// conditionally.
        /// </summary>
        private const int kVersion = 1;

        /// <summary>
        /// Absolute bucket index last written, i.e. frameIndex / kFramesPerBucket.
        ///
        /// Absolute rather than the ring position, because the gap between this and the current
        /// frame is what says how many buckets the window has slid past and must therefore clear.
        /// The ring position is this modulo the bucket count. Kept as long so it cannot wrap
        /// within a save's lifetime.
        ///
        /// Zero would be a real bucket index on a new city, so "nothing recorded yet" is -1.
        /// </summary>
        public long m_LastBucket;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_LastBucket);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out m_LastBucket);
        }
    }

    /// <summary>
    /// Arrivals in one slice of the trailing month, in citizens, split by the connection type they
    /// came through.
    ///
    /// One element per slice, held in ring order. Summing the whole buffer gives the month.
    /// Stored as four named fields rather than an int4 so the save format is explicit about what
    /// each number is, and so a fifth mode could be added at the end later.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct TouristArrivalBucket : IBufferElementData, ISerializable
    {
        public int m_Road;
        public int m_Train;
        public int m_Air;
        public int m_Ship;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Road);
            writer.Write(m_Train);
            writer.Write(m_Air);
            writer.Write(m_Ship);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Road);
            reader.Read(out m_Train);
            reader.Read(out m_Air);
            reader.Read(out m_Ship);
        }
    }
}
