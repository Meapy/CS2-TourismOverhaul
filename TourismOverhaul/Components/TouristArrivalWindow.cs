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
    /// each number is.
    /// </summary>
    /// <summary>
    /// IEmptySerializable, not ISerializable, and this one is a crash fix.
    ///
    /// A hand-written Serialize on a buffer element took the game down whenever a save was written:
    ///
    ///     NullReferenceException
    ///       at Colossal.Serialization.Entities.BinaryWriter.Write (System.UInt32 value)
    ///       at TourismOverhaul.Components.TouristArrivalBucket.Serialize[TWriter] (TWriter writer)
    ///       at Colossal.Serialization.Entities.BinaryWriter.Write[TSerializable] (NativeArray&lt;T&gt;)
    ///       at BufferElementDataSerializer`1+SerializeBufferElementDataJob`1[
    ///             TouristArrivalBucket, BinaryWriter].Execute ()
    ///       at (wrapper delegate-invoke) ...
    ///
    /// followed by a native Mono crash with no managed stack, because the exception escaped a job's
    /// Execute and left the job system unrecoverable.
    ///
    /// The throw is inside the game's writer, not in the four lines this used to have — they only
    /// called it. What that last frame says is the important part: (wrapper delegate-invoke) is the
    /// managed job fallback. Burst cannot compile a job parameterised on a type from an assembly
    /// loaded at runtime, so SerializeBufferElementDataJob&lt;OurType, BinaryWriter&gt; runs managed,
    /// while the same job for every one of the game's own buffer elements is Burst-compiled. The
    /// shape is not the problem — Game.City.CityModifier is IBufferElementData, ISerializable with
    /// InternalBufferCapacity(0), exactly like this was — the problem is that a mod type is the only
    /// thing that ever reaches that code path managed.
    ///
    /// And this type gained nothing from being there. The game reserves a hand-written serializer on
    /// a buffer element for work the plain path cannot do: CityStatistic migrates old saves across
    /// three field widths, Resources maps a resource to a stable index, TripNeeded narrows enums.
    /// This wrote four ints in declaration order, which is precisely what the blittable path writes
    /// by itself — so the custom serializer was never buying anything, and it was the only thing in
    /// this mod on that path. Every other serialized component here is IComponentData, which goes
    /// through a different job and has saved cleanly all along.
    ///
    /// The bytes are unchanged: four int32s per element, in the same order, with the element count
    /// written by the framework either way. Existing saves read back as they did before. If a save
    /// ever does disagree, GetOrCreateArrivalWindow already resets a window whose length is wrong,
    /// and the cost is a trailing-month figure that starts from zero and refills within an in-game
    /// day.
    ///
    /// Do not give this type a Serialize method again. Adding a field means changing the layout,
    /// which needs a version — and a version needs the custom path. Put the new field on
    /// <see cref="TouristArrivalWindowData"/> instead, which is a component and already versioned.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct TouristArrivalBucket : IBufferElementData, IEmptySerializable
    {
        public int m_Road;
        public int m_Train;
        public int m_Air;
        public int m_Ship;
    }
}
