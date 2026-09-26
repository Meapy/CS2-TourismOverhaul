using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Routes;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    public partial class CruiseVoyageSystem
    {
        /// <summary>
        /// Everything the shore-party code reads, as lookups, plus the helpers the vessel code shares
        /// with it. One implementation for both: the sweep job carries a copy, and the main thread
        /// builds one through <see cref="ShoreAccess"/> after waiting for the types it reads.
        /// </summary>
        private struct ShorePartyAccess
        {
            [ReadOnly] public EntityStorageInfoLookup m_Entities;
            [ReadOnly] public ComponentLookup<HouseholdNeed> m_HouseholdNeeds;
            [ReadOnly] public ComponentLookup<Components.ExpectsPurchase> m_ExpectsPurchases;
            [ReadOnly] public ComponentLookup<Game.Pathfind.PathInformation> m_PathInformations;
            [ReadOnly] public BufferLookup<Game.Pathfind.PathElement> m_PathElements;
            [ReadOnly] public ComponentLookup<TravelPurpose> m_TravelPurposes;
            [ReadOnly] public ComponentLookup<LodgingSeeker> m_LodgingSeekers;
            [ReadOnly] public ComponentLookup<Target> m_Targets;
            [ReadOnly] public ComponentLookup<LodgingProvider> m_LodgingProviders;
            [ReadOnly] public ComponentLookup<TouristHousehold> m_TouristHouseholds;
            [ReadOnly] public ComponentLookup<CurrentTransport> m_CurrentTransports;
            [ReadOnly] public ComponentLookup<CurrentBuilding> m_CurrentBuildings;
            [ReadOnly] public ComponentLookup<Game.Creatures.CurrentVehicle> m_CurrentVehicles;
            [ReadOnly] public ComponentLookup<Game.Creatures.Human> m_Humans;
            [ReadOnly] public ComponentLookup<Components.CruiseCall> m_CruiseCalls;
            [ReadOnly] public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;
            [ReadOnly] public BufferLookup<TripNeeded> m_TripNeeded;
            [ReadOnly] public ComponentLookup<CurrentRoute> m_CurrentRoutes;
            [ReadOnly] public BufferLookup<RouteWaypoint> m_RouteWaypoints;
            [ReadOnly] public ComponentLookup<Connected> m_Connected;
            [ReadOnly] public ComponentLookup<Owner> m_Owners;
            [ReadOnly] public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;
            [ReadOnly] public ComponentLookup<StorageProperty> m_StorageProperties;
            [ReadOnly] public BufferLookup<Renter> m_Renters;
            [ReadOnly] public ComponentLookup<Deleted> m_Deleted;

            /// <summary>Rooms written on a stand-in provider: the configured ship capacity.</summary>
            public int m_TerminalRooms;

            /// <summary>Event + ResetTrip, the archetype the game's own systems use to re-target a body.</summary>
            public EntityArchetype m_ResetTripArchetype;

            /// <summary>
            /// Takes a party off a journey to a hotel: its need, its shopping mark, its lodging
            /// search, and every citizen's purpose and queued trips.
            ///
            /// Removing rather than rewriting throughout: TravelPurpose is added when a trip is issued
            /// (TripNeededSystem:1621) and its absence is the state a citizen with nothing to do is
            /// in. TripNeeded is emptied, not trimmed, because CitizenBehaviorSystem serves the next
            /// entry as soon as the current purpose is gone — cancelling only the trip in progress let
            /// a party pick its lodging trip straight back up. The shopping mark goes so the spending
            /// ledger stops attributing this party's wallet drops to goods. A finished or in-flight
            /// lodging search would otherwise be read back and acted on; TouristTargetSearchSystem
            /// drops PathInformation to restart a search, so its absence is the neutral state.
            ///
            /// Afterwards the ordinary tourist behaviour fills the trips again with whatever a visitor
            /// with a bed already booked would do: shopping, leisure, attractions. The bed is the
            /// terminal: TouristHousehold.m_Hotel names it, it carries a zero-price LodgingProvider,
            /// and HouseholdBehaviorSystem:243-251 only re-marks a household LodgingSeeker when that
            /// stops being true.
            /// </summary>
            public void CancelHotelTrip(Entity household, EntityCommandBuffer commandBuffer)
            {
                if (m_HouseholdNeeds.HasComponent(household))
                {
                    commandBuffer.SetComponent(household, new HouseholdNeed
                    {
                        m_Resource = Resource.NoResource,
                        m_Amount = 0
                    });
                }

                if (m_ExpectsPurchases.HasComponent(household))
                {
                    commandBuffer.RemoveComponent<Components.ExpectsPurchase>(household);
                }

                if (m_PathInformations.HasComponent(household))
                {
                    commandBuffer.RemoveComponent<Game.Pathfind.PathInformation>(household);
                }

                if (!m_HouseholdCitizens.HasBuffer(household))
                {
                    return;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null || !m_Entities.Exists(citizen))
                    {
                        continue;
                    }

                    if (m_TravelPurposes.HasComponent(citizen))
                    {
                        commandBuffer.RemoveComponent<TravelPurpose>(citizen);
                    }

                    if (m_TripNeeded.HasBuffer(citizen))
                    {
                        commandBuffer.SetBuffer<TripNeeded>(citizen);
                    }
                }
            }

            /// <summary>
            /// Gives the terminal a zero-price stand-in LodgingProvider, if it has none of its own,
            /// and the utility guard in every case.
            ///
            /// Zero price is load-bearing: TouristLeaveSystem evicts when the wallet is below the
            /// provider's m_Price, so zero can never trigger it, and a passenger is immune to both the
            /// no-hotel and no-money evictions while ashore.
            ///
            /// The utility guard (StorageProperty) goes on first and on every harbour. A building's
            /// electricity and water demand is multiplied by its renters
            /// (AdjustElectricityConsumptionSystem:155-160, the water system :138-146), so a shore
            /// party of hundreds took a port rated in the low megawatts to a thousand.
            /// StorageProperty is read only by those two systems (:121, :135) and only switches the
            /// multiplier off; it is an empty serializable tag with no other reader. Release takes it
            /// back off.
            ///
            /// A terminal that already has a LodgingProvider keeps it — overwriting a real provider
            /// would misprice a genuine hotel — and is marked equipped without CruiseTerminalProvider,
            /// which means "the provider here is ours". A Renter buffer is added (and marked) when
            /// missing, because TouristHouseholdBehaviorSystem:74 nulls m_Hotel for a hotel without
            /// one; nothing bills those renters, since every billing query also requires
            /// PropertyRenter and ProcessingCompany. The markers say what to take back on release.
            /// </summary>
            public void EquipTerminalWithLodging(Entity terminal, EntityCommandBuffer commandBuffer)
            {
                if (!m_StorageProperties.HasComponent(terminal))
                {
                    commandBuffer.AddComponent<StorageProperty>(terminal);
                    commandBuffer.AddComponent<Components.CruiseTerminalUtilityGuard>(terminal);
                }

                if (m_LodgingProviders.HasComponent(terminal))
                {
                    commandBuffer.AddComponent<Components.CruiseTerminalEquipped>(terminal);
                    commandBuffer.AddComponent<Components.CruiseTerminalLodging>(terminal);
                    return;
                }

                // m_FreeRooms is never read for this provider; it is set truthfully anyway.
                commandBuffer.AddComponent(terminal, new LodgingProvider
                {
                    m_Price = 0,
                    m_FreeRooms = m_TerminalRooms
                });

                if (!m_Renters.HasBuffer(terminal))
                {
                    commandBuffer.AddBuffer<Renter>(terminal);
                    commandBuffer.AddComponent<Components.CruiseTerminalRenters>(terminal);
                }

                commandBuffer.AddComponent<Components.CruiseTerminalProvider>(terminal);
                commandBuffer.AddComponent<Components.CruiseTerminalEquipped>(terminal);
                commandBuffer.AddComponent<Components.CruiseTerminalLodging>(terminal);
            }

            /// <summary>
            /// Clears a citizen's own destination and path before a new trip is written on top.
            ///
            /// TripNeededSystem:981-992 empties the whole TripNeeded buffer of a citizen whose
            /// finished PathInformation has no Target to go with it, which is exactly the state of a
            /// citizen who has just finished an errand — most of a shore party at last call. All three
            /// go, so the citizen starts from the state TripNeededSystem treats as new: it reads the
            /// trip's target agent, sets Target from it (:1355-1361) and issues a fresh search.
            /// This is the citizen's path state; the household's is cleared separately.
            /// </summary>
            public void ClearCitizenPathState(Entity citizen, EntityCommandBuffer commandBuffer)
            {
                if (m_Targets.HasComponent(citizen))
                {
                    commandBuffer.RemoveComponent<Target>(citizen);
                }

                if (m_PathInformations.HasComponent(citizen))
                {
                    commandBuffer.RemoveComponent<Game.Pathfind.PathInformation>(citizen);
                }

                if (m_PathElements.HasBuffer(citizen))
                {
                    commandBuffer.RemoveComponent<Game.Pathfind.PathElement>(citizen);
                }
            }

            /// <summary>The entity in a stop's owner chain that carries OutsideConnection.</summary>
            public Entity OutsideConnectionOf(Entity stop)
            {
                Entity walk = stop;

                for (int hop = 0; hop < 8; hop++)
                {
                    if (m_OutsideConnections.HasComponent(walk))
                    {
                        return walk;
                    }

                    if (!m_Owners.HasComponent(walk))
                    {
                        return Entity.Null;
                    }

                    Entity owner = m_Owners[walk].m_Owner;

                    if (owner == Entity.Null || !m_Entities.Exists(owner))
                    {
                        return Entity.Null;
                    }

                    walk = owner;
                }

                return Entity.Null;
            }

            /// <summary>
            /// The ship's map-edge connection, from its route: the first waypoint whose stop sits in
            /// an outside connection. The connection-finding half of TryGetOutsideConnection.
            /// </summary>
            public Entity SeawardConnectionOf(Entity ship)
            {
                if (!m_CurrentRoutes.HasComponent(ship))
                {
                    return Entity.Null;
                }

                Entity route = m_CurrentRoutes[ship].m_Route;

                if (route == Entity.Null || !m_Entities.Exists(route) || !m_RouteWaypoints.HasBuffer(route))
                {
                    return Entity.Null;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_RouteWaypoints[route];

                for (int i = 0; i < waypoints.Length; i++)
                {
                    Entity waypoint = waypoints[i].m_Waypoint;

                    if (waypoint == Entity.Null || !m_Entities.Exists(waypoint) || !m_Connected.HasComponent(waypoint))
                    {
                        continue;
                    }

                    Entity stop = m_Connected[waypoint].m_Connected;

                    if (stop == Entity.Null || !m_Entities.Exists(stop))
                    {
                        continue;
                    }

                    Entity connection = OutsideConnectionOf(stop);

                    if (connection != Entity.Null)
                    {
                        return connection;
                    }
                }

                return Entity.Null;
            }
        }

        /// <summary>
        /// Where a shore passenger was last seen indoors, and whether it has already been put back there.
        /// See ShorePartyJob.PlaceIfNowhere.
        /// </summary>
        private struct LastPlace
        {
            public Entity m_Building;

            /// <summary>0: never put back. 1: put back at m_Building once. 2: put back at the terminal.</summary>
            public byte m_Placed;
        }

        /// <summary>Indices into the sweep's result array, which the main thread logs on its next update.</summary>
        private enum SweepResult
        {
            Recalled,
            Boarded,
            Stranded,
            LeftOtherWay,
            Walking,
            WalkingToShip,
            RidingOther,
            WalkingElsewhere,
            Queued,
            AwaitingPath,
            BusyIndoors,
            NowhereAtAll,
            Idle,
            Swept,
            AnchorsRestored,
            Redirected,
            Rehomed,
            RehomedAtTerminal,
            Frame,
            Ran,
            Count
        }

        /// <summary>
        /// The shore-party sweep, run in a Burst job scheduled after the jobs that write what it
        /// reads. It replaced a main-thread loop that had to wait for those jobs every update.
        ///
        /// On the main thread it had to wait for those jobs first — the frame's creature and vehicle
        /// jobs, which write Target, CurrentTransport, CurrentVehicle and Resident — and that wait
        /// was 5-8 ms per update against well under a millisecond of work. A job scheduled with the
        /// system's dependency runs after the same writers, so it reads the same world, and its
        /// commands go into an EndFrameBarrier buffer created at the same point in the update as
        /// before, so they play back in the same order. Only the summary log moves: it is written
        /// from the results on the next update.
        ///
        /// Chunks are walked in query order on one thread, the order the main-thread loop used.
        /// </summary>
        [BurstCompile]
        private struct ShorePartyJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public ComponentTypeHandle<Components.CruisePassenger> m_PassengerType;
            public ShorePartyAccess m_Access;
            public EntityCommandBuffer m_CommandBuffer;
            public NativeArray<int> m_Results;
            public NativeParallelHashMap<Entity, LastPlace> m_LastPlaces;
            public uint m_Frame;
            public uint m_LastCall;
            public uint m_UpdateInterval;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Components.CruisePassenger> passengers = chunk.GetNativeArray(ref m_PassengerType);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity household = entities[i];
                    Components.CruisePassenger passenger = passengers[i];

                    // Still at sea, inbound: no port has been chosen yet, so there is no deadline to
                    // be past. The terminal is the sentinel, not the frame — m_ReboardFrame is zero
                    // until the ship docks, and zero is a real frame every later frame is past.
                    if (passenger.m_Terminal == Entity.Null)
                    {
                        continue;
                    }

                    Count(SweepResult.Swept);
                    RememberWhereTheyAre(household, passenger.m_Homeward != 0);
                    KeepOffTheHotels(household, passenger.m_Terminal);

                    // Aboard already. They keep the lodging protection above, but nothing below
                    // applies: handing a passenger on the ship another trip would take them off it.
                    if (passenger.m_Homeward != 0)
                    {
                        continue;
                    }

                    // The ship's deadline, not the party's. The party's m_ReboardFrame is when it is
                    // due back, a boarding grace short of the vessel's departure; this is when it is
                    // too late. Using the party's figure for both wrote parties off early.
                    uint sailingFrame = SailingFrameFor(passenger.m_Ship, passenger.m_ReboardFrame);

                    // Aboard: a citizen's body carries CurrentVehicle naming this ship, which is what
                    // "boarded" means to the game and what PassengerSystem rebuilds the manifest from.
                    if (passenger.m_Recalled != 0 && PartyIsAboard(household, passenger.m_Ship))
                    {
                        // m_Terminal stays set: it holds the lodging anchor for the crossing.
                        passenger.m_Homeward = 1;
                        passengers[i] = passenger;
                        Count(SweepResult.Boarded);
                        continue;
                    }

                    if (passenger.m_Recalled != 0)
                    {
                        // Already at the sea connection by some other way — an ordinary ferry to the
                        // same connection is far cheaper to the pathfinder than the cruise pier. The
                        // recall's trip is complete and the party will never board, so it leaves.
                        Entity seawardNow = m_Access.SeawardConnectionOf(passenger.m_Ship);

                        if (seawardNow != Entity.Null && PartyIsAt(household, seawardNow))
                        {
                            ReleaseParty(household, passenger, seawardNow);
                            Count(SweepResult.LeftOtherWay);
                            continue;
                        }

                        // Called back, not aboard: say where they actually are.
                        ClassifyRecalledParty(household, out bool hasBody, out bool hasTrip);

                        if (hasBody)
                        {
                            Count(SweepResult.Walking);

                            switch (WhereIsParty(household, m_Access.SeawardConnectionOf(passenger.m_Ship), passenger.m_Ship))
                            {
                                case 1: Count(SweepResult.WalkingToShip); break;
                                case 2: Count(SweepResult.RidingOther); break;
                                default: Count(SweepResult.WalkingElsewhere); break;
                            }
                        }
                        else if (hasTrip)
                        {
                            Count(SweepResult.Queued);

                            if (PartyAwaitsPath(household))
                            {
                                Count(SweepResult.AwaitingPath);
                            }
                            else if (PartyIsBusyIndoors(household, out bool nowhere))
                            {
                                Count(SweepResult.BusyIndoors);
                            }
                            else if (nowhere)
                            {
                                Count(SweepResult.NowhereAtAll);
                            }
                        }
                        else
                        {
                            Count(SweepResult.Idle);
                        }
                    }

                    if (m_Frame >= sailingFrame)
                    {
                        // Last look for the gangway: boarding and this deadline can land on the same
                        // sweep, and a party that walked aboard between passes must not be pulled off.
                        if (PartyIsAboard(household, passenger.m_Ship))
                        {
                            passenger.m_Homeward = 1;
                            passengers[i] = passenger;
                            Count(SweepResult.Boarded);
                            continue;
                        }

                        m_CommandBuffer.RemoveComponent<Components.CruisePassenger>(household);
                        m_CommandBuffer.AddComponent(household, new Game.Agents.MovingAway
                        {
                            m_Target = m_Access.SeawardConnectionOf(passenger.m_Ship),
                            m_Reason = Game.Agents.MoveAwayReason.None
                        });

                        Count(SweepResult.Stranded);
                        continue;
                    }

                    // Not yet last call: they are still out seeing the city.
                    if (m_Frame + m_LastCall < passenger.m_ReboardFrame)
                    {
                        continue;
                    }

                    // Last call. The full recall (purpose, queued trips, needs, path) on the first
                    // pass and every kRecallRefreshFrames after, catching anyone who finished one
                    // errand and started another; the cheap nudge of idle citizens in between.
                    bool reassert = passenger.m_Recalled == 0 || m_Frame % kRecallRefreshFrames < m_UpdateInterval;

                    // To the ship's own map-edge connection, not the quay — see RecallToHarbour. The
                    // terminal is the fallback if the connection cannot be resolved.
                    Entity seaward = m_Access.SeawardConnectionOf(passenger.m_Ship);
                    RecallToHarbour(household, seaward != Entity.Null ? seaward : passenger.m_Terminal, reassert, passenger.m_Terminal);

                    if (passenger.m_Recalled == 0)
                    {
                        passenger.m_Recalled = 1;
                        passengers[i] = passenger;
                        Count(SweepResult.Recalled);
                    }
                }

                m_Results[(int)SweepResult.Frame] = (int)m_Frame;
                m_Results[(int)SweepResult.Ran] = 1;
            }

            private void Count(SweepResult result) => m_Results[(int)result]++;

            /// <summary>
            /// Records the building each of the party's citizens is in, if any; forgets them once the
            /// party is aboard for home, so the map holds only people still ashore.
            /// </summary>
            private void RememberWhereTheyAre(Entity household, bool aboardForHome)
            {
                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (aboardForHome)
                    {
                        m_LastPlaces.Remove(citizen);
                        continue;
                    }

                    if (!m_Access.m_CurrentBuildings.HasComponent(citizen))
                    {
                        continue;
                    }

                    Entity building = m_Access.m_CurrentBuildings[citizen].m_CurrentBuilding;

                    if (building == Entity.Null)
                    {
                        continue;
                    }

                    m_LastPlaces.TryGetValue(citizen, out LastPlace place);
                    place.m_Building = building;
                    m_LastPlaces[citizen] = place;
                }
            }

            /// <summary>
            /// Keeps a cruise passenger out of the hotel system, whatever else has gone wrong: strips a
            /// LodgingSeeker marker, cancels a journey to a lodging building other than the terminal,
            /// re-equips the terminal if its provider has gone, and re-points m_Hotel at the terminal.
            ///
            /// The marker keeps coming back — TouristHouseholdBehaviorSystem:74 nulls m_Hotel whenever
            /// the anchor has no Renter buffer — and TouristTargetSearchSystem can find a hotel and send
            /// them walking before the next sweep, so the target is cancelled too. Anything else they
            /// might be heading for (a shop, a venue, an attraction) is left alone. The terminal's
            /// renter list is deliberately left alone as well: StorageProperty now keeps its utilities
            /// at the rated figure, and the renters are what make the anchor believable (:74-92).
            /// </summary>
            private void KeepOffTheHotels(Entity household, Entity terminal)
            {
                if (m_Access.m_LodgingSeekers.HasComponent(household))
                {
                    m_CommandBuffer.RemoveComponent<LodgingSeeker>(household);
                }

                if (m_Access.m_Targets.HasComponent(household))
                {
                    Entity going = m_Access.m_Targets[household].m_Target;

                    if (going != terminal && going != Entity.Null && m_Access.m_LodgingProviders.HasComponent(going))
                    {
                        m_CommandBuffer.RemoveComponent<Target>(household);
                        m_Access.CancelHotelTrip(household, m_CommandBuffer);
                    }
                }

                if (terminal == Entity.Null || !m_Access.m_Entities.Exists(terminal))
                {
                    return;
                }

                if (!m_Access.m_LodgingProviders.HasComponent(terminal))
                {
                    m_Access.EquipTerminalWithLodging(terminal, m_CommandBuffer);
                }

                if (!m_Access.m_TouristHouseholds.HasComponent(household))
                {
                    return;
                }

                TouristHousehold tourist = m_Access.m_TouristHouseholds[household];

                if (tourist.m_Hotel != terminal)
                {
                    tourist.m_Hotel = terminal;
                    m_CommandBuffer.SetComponent(household, tourist);
                    Count(SweepResult.AnchorsRestored);
                }
            }

            /// <summary>
            /// When this party's ship actually leaves: the open call's reboard frame, or the party's
            /// own figure if the call has closed or the ship is gone.
            /// </summary>
            private uint SailingFrameFor(Entity ship, uint ownDeadline)
            {
                if (ship != Entity.Null && m_Access.m_Entities.Exists(ship) && m_Access.m_CruiseCalls.HasComponent(ship))
                {
                    return m_Access.m_CruiseCalls[ship].m_ReboardFrame;
                }

                return ownDeadline;
            }

            /// <summary>
            /// Whether any of a party's citizens is on this vessel: citizen -> CurrentTransport -> body
            /// -> CurrentVehicle. A party is a party once one member is aboard; holding it until the
            /// last straggler would strand the ones already on the ship.
            /// </summary>
            private bool PartyIsAboard(Entity household, Entity ship)
            {
                if (ship == Entity.Null || !m_Access.m_Entities.Exists(ship) || !m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return false;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null || !m_Access.m_Entities.Exists(citizen) || !m_Access.m_CurrentTransports.HasComponent(citizen))
                    {
                        continue;
                    }

                    Entity creature = m_Access.m_CurrentTransports[citizen].m_CurrentTransport;

                    if (creature == Entity.Null || !m_Access.m_Entities.Exists(creature) || !m_Access.m_CurrentVehicles.HasComponent(creature))
                    {
                        continue;
                    }

                    if (m_Access.m_CurrentVehicles[creature].m_Vehicle == ship)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Whether the party is standing at an outside connection with nobody out in the world —
            /// it has reached the edge of the map without the ship.
            /// </summary>
            private bool PartyIsAt(Entity household, Entity connection)
            {
                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return false;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];
                bool atEdge = false;

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null || !m_Access.m_Entities.Exists(citizen))
                    {
                        continue;
                    }

                    if (m_Access.m_CurrentTransports.HasComponent(citizen))
                    {
                        return false;
                    }

                    if (m_Access.m_CurrentBuildings.HasComponent(citizen))
                    {
                        Entity building = m_Access.m_CurrentBuildings[citizen].m_CurrentBuilding;

                        atEdge |= building == connection
                                  || (building != Entity.Null && m_Access.m_OutsideConnections.HasComponent(building));
                    }
                }

                return atEdge;
            }

            /// <summary>
            /// Ends a party's shore leave without the ship: the tag and the harbour anchor come off, and
            /// the household leaves through the connection it is already standing at.
            /// </summary>
            private void ReleaseParty(Entity household, Components.CruisePassenger passenger, Entity connection)
            {
                m_CommandBuffer.RemoveComponent<Components.CruisePassenger>(household);

                if (m_Access.m_TouristHouseholds.HasComponent(household))
                {
                    TouristHousehold tourist = m_Access.m_TouristHouseholds[household];

                    if (tourist.m_Hotel == passenger.m_Terminal)
                    {
                        tourist.m_Hotel = Entity.Null;
                        m_CommandBuffer.SetComponent(household, tourist);
                    }
                }

                m_CommandBuffer.AddComponent(household, new Game.Agents.MovingAway
                {
                    m_Target = connection,
                    m_Reason = Game.Agents.MoveAwayReason.None
                });
            }

            /// <summary>
            /// Where one recalled party has got to. A body in the world (CurrentTransport) means the
            /// journey is under way; no body but a queued TripNeeded means it is issued and waiting;
            /// neither means something took the trip away. Diagnostic only.
            /// </summary>
            private void ClassifyRecalledParty(Entity household, out bool hasBody, out bool hasTrip)
            {
                hasBody = false;
                hasTrip = false;

                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null || !m_Access.m_Entities.Exists(citizen))
                    {
                        continue;
                    }

                    if (m_Access.m_CurrentTransports.HasComponent(citizen))
                    {
                        hasBody = true;
                        return;
                    }

                    if (!hasTrip && m_Access.m_TripNeeded.HasBuffer(citizen) && m_Access.m_TripNeeded[citizen].Length > 0)
                    {
                        hasTrip = true;
                    }
                }
            }

            /// <summary>
            /// For a party with someone out in the world: 1 if that body is heading for the ship's
            /// connection, 2 if it is aboard a vehicle other than the ship, 0 for anywhere else.
            /// </summary>
            private int WhereIsParty(Entity household, Entity destination, Entity ship)
            {
                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return 0;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null || !m_Access.m_CurrentTransports.HasComponent(citizen))
                    {
                        continue;
                    }

                    Entity creature = m_Access.m_CurrentTransports[citizen].m_CurrentTransport;

                    if (creature != Entity.Null && m_Access.m_CurrentVehicles.HasComponent(creature))
                    {
                        if (m_Access.m_CurrentVehicles[creature].m_Vehicle != ship)
                        {
                            return 2;
                        }
                    }

                    return destination != Entity.Null && HeadedFor(citizen, destination) ? 1 : 0;
                }

                return 0;
            }

            /// <summary>Whether any of the party is waiting on an unanswered pathfind.</summary>
            private bool PartyAwaitsPath(Entity household)
            {
                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return false;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen != Entity.Null
                        && m_Access.m_PathInformations.HasComponent(citizen)
                        && (m_Access.m_PathInformations[citizen].m_State & Game.Pathfind.PathFlags.Pending) != 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// For a queued party with nobody out in the world: whether a member is held by an activity
            /// in progress (TripNeededSystem will not look at them until it is cleared), and whether a
            /// member is in neither a building nor the world.
            /// </summary>
            private bool PartyIsBusyIndoors(Entity household, out bool nowhere)
            {
                nowhere = false;

                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return false;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null || !m_Access.m_Entities.Exists(citizen))
                    {
                        continue;
                    }

                    if (m_Access.m_TravelPurposes.HasComponent(citizen))
                    {
                        return true;
                    }

                    if (!m_Access.m_CurrentBuildings.HasComponent(citizen))
                    {
                        nowhere = true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Points one party at the ship's map-edge connection and takes away its reasons to stop.
            ///
            /// The destination is the connection, not the quay: from a city pier the only way to a sea
            /// connection is the vessel serving it, so one trip walks them across the city, waits at
            /// the terminal and boards. The old two-step relay (walk to the terminal, then a second trip
            /// once a sweep caught them inside it) lost most of a complement at step two.
            ///
            /// An ordinary Leisure trip, issued to each citizen: a Target on the household makes nobody
            /// walk (TouristHouseholdBehaviorSystem:59-66 treats it as sorted), and MovingAway can be
            /// satisfied by placing the citizen at the destination (TripNeededSystem:1583-1599), the
            /// teleport that once made a whole shore party vanish. Leisure always travels.
            ///
            /// Everything that could divert them goes at the same moment — the household's need and
            /// shopping mark, its stale PathInformation, each citizen's purpose and path — except for
            /// a citizen already headed there (<see cref="HeadedFor"/>): re-clearing those restarted
            /// walks and pathfinds that took longer than the refresh, and 678 of 1,070 parties were
            /// still ashore an hour before sailing.
            /// </summary>
            private void RecallToHarbour(Entity household, Entity destination, bool firstCall, Entity terminal)
            {
                if (destination == Entity.Null || !m_Access.m_Entities.Exists(destination))
                {
                    return;
                }

                if (!firstCall)
                {
                    NudgeIdleCitizens(household, destination, terminal);
                    return;
                }

                if (m_Access.m_HouseholdNeeds.HasComponent(household))
                {
                    m_CommandBuffer.SetComponent(household, new HouseholdNeed
                    {
                        m_Resource = Resource.NoResource,
                        m_Amount = 0
                    });
                }

                if (m_Access.m_ExpectsPurchases.HasComponent(household))
                {
                    m_CommandBuffer.RemoveComponent<Components.ExpectsPurchase>(household);
                }

                if (m_Access.m_PathInformations.HasComponent(household))
                {
                    m_CommandBuffer.RemoveComponent<Game.Pathfind.PathInformation>(household);
                }

                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null
                        || !m_Access.m_Entities.Exists(citizen)
                        || !m_Access.m_TripNeeded.HasBuffer(citizen)
                        || HeadedFor(citizen, destination))
                    {
                        continue;
                    }

                    // Out in the world on foot: turned round where they stand, not after the errand.
                    if (m_Access.m_CurrentTransports.HasComponent(citizen) && RedirectWalker(citizen, destination))
                    {
                        continue;
                    }

                    if (m_Access.m_TravelPurposes.HasComponent(citizen))
                    {
                        m_CommandBuffer.RemoveComponent<TravelPurpose>(citizen);
                    }

                    m_Access.ClearCitizenPathState(citizen, m_CommandBuffer);
                    PlaceIfNowhere(citizen, terminal);
                    IssueTrip(citizen, destination);
                }
            }

            /// <summary>
            /// Puts a recalled citizen that is nowhere — no building, no body — at the harbour terminal,
            /// so its trip to the ship can start.
            ///
            /// TripNeededSystem only serves citizens with a CurrentBuilding (its query requires it), so a
            /// citizen without one never gets a trip however often the recall queues it: measured as 435
            /// parties, about 1,300 people, still "queued, nowhere" at the deadline, and the count grew
            /// through last call. A citizen ends up there when its body is deleted by a path other than
            /// arriving — ResidentAISystem.ReturnHome deletes a body that has no path home (NoPathToHome),
            /// and ReferencesSystem then drops the citizen's CurrentTransport without giving it a building.
            ///
            /// It is put back in the last building it was seen in (<see cref="RememberWhereTheyAre"/>),
            /// so the player sees it come out and walk to the ship: TripNeededSystem spawns the body at
            /// that building and routes it from there. Nothing vanishes, because there was no body.
            ///
            /// Only once. A citizen that goes nowhere again after that evidently has no path from there
            /// — which is what deleted its body the first time — so the second time, or when the building
            /// has gone, it is put at the harbour terminal, from where the walk aboard is short and sure.
            /// </summary>
            private void PlaceIfNowhere(Entity citizen, Entity terminal)
            {
                if (m_Access.m_CurrentBuildings.HasComponent(citizen)
                    || m_Access.m_CurrentTransports.HasComponent(citizen))
                {
                    return;
                }

                m_LastPlaces.TryGetValue(citizen, out LastPlace place);

                if (place.m_Placed == 0
                    && place.m_Building != Entity.Null
                    && m_Access.m_Entities.Exists(place.m_Building)
                    && !m_Access.m_Deleted.HasComponent(place.m_Building))
                {
                    m_CommandBuffer.AddComponent(citizen, new CurrentBuilding { m_CurrentBuilding = place.m_Building });
                    place.m_Placed = 1;
                    m_LastPlaces[citizen] = place;
                    Count(SweepResult.Rehomed);
                    return;
                }

                if (terminal == Entity.Null || !m_Access.m_Entities.Exists(terminal))
                {
                    return;
                }

                m_CommandBuffer.AddComponent(citizen, new CurrentBuilding { m_CurrentBuilding = terminal });
                place.m_Building = terminal;
                place.m_Placed = 2;
                m_LastPlaces[citizen] = place;
                Count(SweepResult.RehomedAtTerminal);
            }

            /// <summary>
            /// Gives the harbour trip back to any citizen of a recalled party that is not travelling.
            ///
            /// "Not travelling" is the whole test: no CurrentTransport, the citizen's link to a body in
            /// the world. Narrower tests (no TravelPurpose; in a CurrentBuilding) both left people
            /// behind, because the purpose is the thing they are busy doing and attractions hold their
            /// visitors differently. The one thing this must not do is interrupt a walk back.
            /// </summary>
            private void NudgeIdleCitizens(Entity household, Entity destination, Entity terminal)
            {
                if (!m_Access.m_HouseholdCitizens.HasBuffer(household))
                {
                    return;
                }

                DynamicBuffer<HouseholdCitizen> citizens = m_Access.m_HouseholdCitizens[household];

                for (int i = 0; i < citizens.Length; i++)
                {
                    Entity citizen = citizens[i].m_Citizen;

                    if (citizen == Entity.Null
                        || !m_Access.m_Entities.Exists(citizen)
                        || !m_Access.m_TripNeeded.HasBuffer(citizen))
                    {
                        continue;
                    }

                    if (HeadedFor(citizen, destination))
                    {
                        continue;
                    }

                    // A body out in the world is turned round now; one riding a vehicle is left to
                    // step off, and the next sweep — 64 frames later — turns it round then.
                    if (m_Access.m_CurrentTransports.HasComponent(citizen))
                    {
                        RedirectWalker(citizen, destination);
                        continue;
                    }

                    if (m_Access.m_TravelPurposes.HasComponent(citizen))
                    {
                        m_CommandBuffer.RemoveComponent<TravelPurpose>(citizen);
                    }

                    m_Access.ClearCitizenPathState(citizen, m_CommandBuffer);
                    PlaceIfNowhere(citizen, terminal);
                    IssueTrip(citizen, destination);
                }
            }

            /// <summary>
            /// One Leisure trip naming the destination, and a run if the citizen has a body out in the
            /// world — the game's own sign of urgency (ResidentAISystem:996).
            /// </summary>
            private void IssueTrip(Entity citizen, Entity destination)
            {
                DynamicBuffer<TripNeeded> trips = m_CommandBuffer.SetBuffer<TripNeeded>(citizen);

                trips.Add(new TripNeeded
                {
                    m_TargetAgent = destination,
                    m_Purpose = Purpose.Leisure,
                    m_Resource = Resource.NoResource,
                    m_Priority = byte.MaxValue
                });

                if (m_Access.m_CurrentTransports.HasComponent(citizen))
                {
                    MakeRun(m_Access.m_CurrentTransports[citizen].m_CurrentTransport);
                }
            }

            private void MakeRun(Entity creature)
            {
                if (creature == Entity.Null || !m_Access.m_Entities.Exists(creature) || !m_Access.m_Humans.HasComponent(creature))
                {
                    return;
                }

                Game.Creatures.Human human = m_Access.m_Humans[creature];

                if ((human.m_Flags & Game.Creatures.HumanFlags.Run) != 0)
                {
                    return;
                }

                human.m_Flags |= Game.Creatures.HumanFlags.Run;
                m_CommandBuffer.SetComponent(creature, human);
            }

            /// <summary>
            /// Turns a recalled citizen's body round where it stands and sends it to the ship, whatever
            /// it was doing: a shop, a venue, a detour, a queue for another line.
            ///
            /// This is the game's own mechanism, not a hand-move. A ResetTrip event (Game.Creatures)
            /// is what TripNeededSystem.ResetTrip emits when a citizen with a body out in the world is
            /// given a new trip, and what HealthProblemSystem and HospitalAISystem emit to stop one.
            /// TripResetSystem then drops any Divert, clears the arrived and hang-around flags, marks
            /// the path obsolete, sets the new Target and the citizen's TravelPurpose; the body re-paths
            /// through ResidentAISystem.FindNewPath with pedestrian, taxi and public transport
            /// methods, so it walks to the pier and boards like any passenger. Nothing is placed.
            ///
            /// Before this the recall only queued a trip, which the game serves when the current errand
            /// ends — measured at the deadline as ~150 parties still finishing errands across the city.
            ///
            /// A body riding a vehicle is left alone, as every game system that emits ResetTrip leaves
            /// it: a passenger is the vehicle's to carry. It is turned round on the first sweep after it
            /// steps off.
            /// </summary>
            private bool RedirectWalker(Entity citizen, Entity destination)
            {
                Entity creature = m_Access.m_CurrentTransports[citizen].m_CurrentTransport;

                if (creature == Entity.Null
                    || !m_Access.m_Entities.Exists(creature)
                    || !m_Access.m_Targets.HasComponent(creature)
                    || m_Access.m_CurrentVehicles.HasComponent(creature))
                {
                    return false;
                }

                if (m_Access.m_Targets[creature].m_Target != destination)
                {
                    Entity reset = m_CommandBuffer.CreateEntity(m_Access.m_ResetTripArchetype);
                    m_CommandBuffer.SetComponent(reset, new Game.Creatures.ResetTrip
                    {
                        m_Creature = creature,
                        m_Target = destination,
                        m_TravelPurpose = Purpose.Leisure
                    });

                    // And the errands queued behind the one being cut short, so nothing is waiting to
                    // pull them off again: the recall always emptied the buffer.
                    m_CommandBuffer.SetBuffer<TripNeeded>(citizen);

                    Count(SweepResult.Redirected);
                }

                MakeRun(creature);
                return true;
            }

            /// <summary>
            /// Whether a citizen is already on its way to the recall destination.
            ///
            /// Out in the world, the body's own target is the honest answer. Indoors with an activity
            /// in progress (TravelPurpose), or in no building, the trip cannot start whatever it says —
            /// TripNeededSystem excludes TravelPurpose outright — so the recall has to reach them.
            /// Otherwise: the citizen's Target, or a queued trip naming the destination.
            /// </summary>
            private bool HeadedFor(Entity citizen, Entity destination)
            {
                if (m_Access.m_CurrentTransports.HasComponent(citizen))
                {
                    Entity walker = m_Access.m_CurrentTransports[citizen].m_CurrentTransport;

                    return walker != Entity.Null
                        && m_Access.m_Entities.Exists(walker)
                        && m_Access.m_Targets.HasComponent(walker)
                        && m_Access.m_Targets[walker].m_Target == destination;
                }

                if (m_Access.m_TravelPurposes.HasComponent(citizen) || !m_Access.m_CurrentBuildings.HasComponent(citizen))
                {
                    return false;
                }

                if (m_Access.m_Targets.HasComponent(citizen) && m_Access.m_Targets[citizen].m_Target == destination)
                {
                    return true;
                }

                if (m_Access.m_TripNeeded.HasBuffer(citizen))
                {
                    DynamicBuffer<TripNeeded> trips = m_Access.m_TripNeeded[citizen];

                    for (int i = 0; i < trips.Length; i++)
                    {
                        if (trips[i].m_TargetAgent == destination)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }
}
