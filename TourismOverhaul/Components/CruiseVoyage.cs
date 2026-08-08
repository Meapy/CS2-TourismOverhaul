using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// A cruise ship's port call, held on the ship itself.
    ///
    /// The game has no concept of a passenger cohort — a citizen boards a vehicle because that
    /// vehicle's route serves the destination their pathfinding chose, and nothing anywhere says
    /// "these people belong to this ship". So the cohort is mod-owned state, and this is the ship
    /// half of it. See docs/CRUISE-LINE-PLAN.md.
    ///
    /// The passenger half lives on the households as <see cref="CruisePassenger"/>, and that is the
    /// authoritative side: Game.Vehicles.Passenger is IEmptySerializable and rebuilt after load from
    /// each citizen's CurrentVehicle, so anything stored only on the vehicle would not survive a
    /// reload.
    /// </summary>
    public struct CruiseCall : IComponentData, ISerializable
    {
        /// <summary>
        /// Layout version, written first and always. See TourismLedgerData for why.
        ///
        /// Version 2 added <see cref="m_Escaped"/>, version 3 <see cref="m_TargetPassengers"/>,
        /// both at the end.
        /// </summary>
        private const int kVersion = 3;

        /// <summary>The building the ship is docked at, and that its passengers return to.</summary>
        public Entity m_Terminal;

        /// <summary>Frame the passengers came ashore.</summary>
        public uint m_DisembarkedFrame;

        /// <summary>Frame shore leave ends and the ship may sail.</summary>
        public uint m_ReboardFrame;

        /// <summary>Households put ashore by this call, in parties rather than people.</summary>
        public int m_PartyCount;

        /// <summary>
        /// Set once the vessel is found away from its own quay before shore leave ended.
        ///
        /// Purely so the diagnostic is written once per call rather than on every update. It says
        /// the hold stopped applying — which is a different fault from the hold never having been
        /// set, and CruiseVoyageSystem.ReportEscape logs the readings that tell those apart.
        /// </summary>
        public byte m_Escaped;

        /// <summary>
        /// Passengers this call is meant to land, in people.
        ///
        /// Held on the call because the shortfall is corrected over several updates rather than in
        /// one go — households are not given citizens the instant they are created, so the only
        /// honest way to hit a head count is to keep counting who actually arrived and top up. See
        /// CruiseVoyageSystem.TopUpCall.
        /// </summary>
        public int m_TargetPassengers;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Terminal);
            writer.Write(m_DisembarkedFrame);
            writer.Write(m_ReboardFrame);
            writer.Write(m_PartyCount);
            writer.Write(m_Escaped);
            writer.Write(m_TargetPassengers);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out m_Terminal);
            reader.Read(out m_DisembarkedFrame);
            reader.Read(out m_ReboardFrame);
            reader.Read(out m_PartyCount);

            m_Escaped = 0;
            m_TargetPassengers = 0;

            if (version >= 2)
            {
                reader.Read(out m_Escaped);
            }

            if (version >= 3)
            {
                reader.Read(out m_TargetPassengers);
            }
        }
    }

    /// <summary>
    /// Marks a tourist household as belonging to a cruise ship's shore party.
    ///
    /// This is what makes "the same passengers go back aboard" true: the mod owns the cohort from
    /// the moment it comes ashore until it reboards, so the set cannot drift. It is also the side
    /// that persists, since CS2 saves entities and the households are entities.
    ///
    /// Note what this deliberately does *not* do: it does not suppress anything by itself. Cruise
    /// passengers avoid the evening eviction because their TouristHousehold.m_Hotel names a
    /// zero-price LodgingProvider on the terminal, which is the game's own condition at
    /// TouristLeaveSystem:68 rather than a special case bolted beside it.
    /// </summary>
    public struct CruisePassenger : IComponentData, ISerializable
    {
        /// <summary>
        /// Version 2 added <see cref="m_Recalled"/>, 3 <see cref="m_Aboard"/>, 4
        /// <see cref="m_Homeward"/>, each at the end. Older saves default them to zero, which reads
        /// as "ashore, not yet recalled, not going home" — the safe interpretation, since the worst
        /// case is a party sent to the quay twice.
        /// </summary>
        private const int kVersion = 4;

        /// <summary>The ship this party came off and will leave on.</summary>
        public Entity m_Ship;

        /// <summary>
        /// The building they came ashore at and must return to.
        ///
        /// Entity.Null while the party is still aboard and inbound, and that is the sentinel every
        /// shore-side check must test — not <see cref="m_ReboardFrame"/>, which is zero until a
        /// port is chosen. Zero is a real frame that every later frame is past, so treating it as
        /// "not set" reads a whole inbound complement as overdue.
        /// </summary>
        public Entity m_Terminal;

        /// <summary>Frame they are due back aboard. Meaningless until m_Terminal is set.</summary>
        public uint m_ReboardFrame;

        /// <summary>
        /// Non-zero while the party's citizens are physically aboard the ship.
        ///
        /// Set when they are loaded at the map edge and cleared when they walk off at the quay, so
        /// the disembark step knows which parties still need taking off the vessel and the shore
        /// systems know to leave the ones at sea alone.
        /// </summary>
        public byte m_Aboard;

        /// <summary>
        /// Non-zero once the party has reboarded and is riding home.
        ///
        /// Needed because "aboard with no terminal" describes two opposite situations — a
        /// complement sailing in that must be landed, and one sailing out that must be released at
        /// the map edge. Without this flag the ship would try to disembark its homeward passengers
        /// at the next city and land the same people twice.
        /// </summary>
        public byte m_Homeward;

        /// <summary>
        /// Set once the party has been sent back to the quay, so the recall happens once.
        ///
        /// Without it every update inside the last-call window would re-target a household that is
        /// already walking back, which cancels the walk it is halfway through.
        /// </summary>
        public byte m_Recalled;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Ship);
            writer.Write(m_Terminal);
            writer.Write(m_ReboardFrame);
            writer.Write(m_Recalled);
            writer.Write(m_Aboard);
            writer.Write(m_Homeward);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out m_Ship);
            reader.Read(out m_Terminal);
            reader.Read(out m_ReboardFrame);

            m_Recalled = 0;
            m_Aboard = 0;
            m_Homeward = 0;

            if (version >= 2)
            {
                reader.Read(out m_Recalled);
            }

            if (version >= 3)
            {
                reader.Read(out m_Aboard);
            }

            if (version >= 4)
            {
                reader.Read(out m_Homeward);
            }
        }
    }

    /// <summary>
    /// A complement of passengers loaded at the map edge and riding the ship in.
    ///
    /// Held on the ship between leaving the outside connection and docking at a city terminal, so
    /// the vessel genuinely arrives carrying people rather than conjuring them at the quay.
    ///
    /// Loading cannot be instantaneous, and that is what this component exists to track. A
    /// household created this frame has no citizens for several updates — HouseholdInitializeSystem
    /// runs on its own cadence — so there is nothing to put aboard until it has caught up. The ship
    /// therefore waits at the map edge until its complement is aboard or the deadline passes,
    /// whichever comes first.
    /// </summary>
    public struct CruiseManifest : IComponentData, ISerializable
    {
        private const int kVersion = 1;

        /// <summary>Passengers this sailing means to carry, in people.</summary>
        public int m_TargetPassengers;

        /// <summary>Frame after which the ship sails whether or not it filled.</summary>
        public uint m_LoadDeadline;

        /// <summary>Set once the complement is aboard, or the deadline forced the sailing.</summary>
        public byte m_Loaded;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_TargetPassengers);
            writer.Write(m_LoadDeadline);
            writer.Write(m_Loaded);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out m_TargetPassengers);
            reader.Read(out m_LoadDeadline);
            reader.Read(out m_Loaded);
        }
    }

    /// <summary>
    /// Marks a building the mod has given a stand-in <c>LodgingProvider</c> so that cruise
    /// passengers ashore have somewhere the game considers them lodged.
    ///
    /// Kept as its own marker so the component can be removed again cleanly. Without it there is no
    /// way to tell a terminal we equipped from a building that legitimately provides lodging, and
    /// stripping the wrong one would evict real hotel guests.
    /// </summary>
    public struct CruiseTerminalLodging : IComponentData, IEmptySerializable
    {
    }
}
