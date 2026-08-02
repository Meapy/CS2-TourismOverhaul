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
    public struct TourismLedger : IComponentData, ISerializable
    {
        /// <summary>Spend in the month currently accumulating.</summary>
        public int m_Lodging;
        public int m_Goods;
        public int m_Fares;
        public int m_Other;

        /// <summary>The last completed month, which is what the panel shows.</summary>
        public int m_LastLodging;
        public int m_LastGoods;
        public int m_LastFares;
        public int m_LastOther;

        /// <summary>Whether a month has completed, so the panel knows which set to read.</summary>
        public bool m_HasCompleteMonth;

        /// <summary>Calendar month the accumulating figures belong to.</summary>
        public int m_Month;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Lodging);
            writer.Write(m_Goods);
            writer.Write(m_Fares);
            writer.Write(m_Other);
            writer.Write(m_LastLodging);
            writer.Write(m_LastGoods);
            writer.Write(m_LastFares);
            writer.Write(m_LastOther);
            writer.Write(m_HasCompleteMonth);
            writer.Write(m_Month);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Lodging);
            reader.Read(out m_Goods);
            reader.Read(out m_Fares);
            reader.Read(out m_Other);
            reader.Read(out m_LastLodging);
            reader.Read(out m_LastGoods);
            reader.Read(out m_LastFares);
            reader.Read(out m_LastOther);
            reader.Read(out m_HasCompleteMonth);
            reader.Read(out m_Month);
        }
    }
}
