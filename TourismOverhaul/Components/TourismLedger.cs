using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// The spending ledger, in a form the game will save.
    ///
    /// Held on a singleton entity because that is what CS2 serializes: systems are not persisted,
    /// only entities and their components, so figures kept in system fields vanish on reload. The
    /// ledger reports a settled monthly total, and a month is a whole in-game day — long enough
    /// that losing it on every load would leave the panel blank far more often than not.
    ///
    /// Stored as int rather than long because a month of city-wide tourist spending fits
    /// comfortably, and int keeps the save format simple.
    /// </summary>
    public struct TourismLedgerData : IComponentData, ISerializable
    {
        /// <summary>
        /// Layout version, written first and always.
        ///
        /// The first version of this component had no version field, so when a fifth category was
        /// added the reader ran past the end of what older saves contained and threw
        /// ComponentSerializerException. Renaming the type is what makes that recoverable: the old
        /// name no longer resolves, so the stale data is skipped instead of misread, and those
        /// saves simply start their ledger again.
        ///
        /// From here on, add fields at the end and bump this, reading conditionally on the version
        /// so old saves stay loadable.
        /// </summary>
        private const int kVersion = 1;

        /// <summary>Spend in the month currently accumulating.</summary>
        public int m_Lodging;
        public int m_Goods;
        public int m_Fares;
        public int m_Leisure;
        public int m_Other;

        /// <summary>The last completed month, which is what the panel shows.</summary>
        public int m_LastLodging;
        public int m_LastGoods;
        public int m_LastFares;
        public int m_LastLeisure;
        public int m_LastOther;

        /// <summary>Whether a month has completed, so the panel knows which set to read.</summary>
        public bool m_HasCompleteMonth;

        /// <summary>Calendar month the accumulating figures belong to.</summary>
        public int m_Month;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Lodging);
            writer.Write(m_Goods);
            writer.Write(m_Fares);
            writer.Write(m_Leisure);
            writer.Write(m_Other);
            writer.Write(m_LastLodging);
            writer.Write(m_LastGoods);
            writer.Write(m_LastFares);
            writer.Write(m_LastLeisure);
            writer.Write(m_LastOther);
            writer.Write(m_HasCompleteMonth);
            writer.Write(m_Month);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out m_Lodging);
            reader.Read(out m_Goods);
            reader.Read(out m_Fares);
            reader.Read(out m_Leisure);
            reader.Read(out m_Other);
            reader.Read(out m_LastLodging);
            reader.Read(out m_LastGoods);
            reader.Read(out m_LastFares);
            reader.Read(out m_LastLeisure);
            reader.Read(out m_LastOther);
            reader.Read(out m_HasCompleteMonth);
            reader.Read(out m_Month);
        }
    }
}
