using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Which ways in an outside connection can actually deliver a visitor.
    ///
    /// An outside connection's prefab declares its transfer types, and both the game's spawner and
    /// ours used to take that at face value: a map-edge air marker is an "air connection" whether
    /// or not anything flies there. But a visitor set down at an air or sea connection gets into
    /// the city only aboard a passenger line the player has drawn to it — the marker sits at the
    /// edge of the map with no lane or stop near it, so the hotel search has nowhere to start from
    /// and the visitor's own trips cannot leave. Arrivals rolled for an unserved air or sea
    /// connection were therefore never going to arrive: vanilla evicts them on the first failed
    /// search, and before this the mod stranded them there for hours with a room booked.
    ///
    /// So a connection's non-road types count only while a passenger line serves it. Road needs no
    /// line, since a road connection sits on the road. Train is held to the same rule as air and
    /// sea: a rail marker with no passenger train line is just as unreachable.
    /// </summary>
    internal static class ArrivalConnections
    {
        /// <summary>
        /// Sub-object depth to search for the connection's stops. The waypoint of a line drawn to
        /// a connection is attached to a stop or spawn location some levels below the building
        /// (WaypointConnectionSystem:1193); the game's own walk, BuildingUtils.GetNumberOfConnectedLines,
        /// is unbounded, and this bound only guards against a malformed prefab.
        /// </summary>
        private const int kMaxDepth = 4;

        /// <summary>The transfer types this connection can deliver arrivals by right now.</summary>
        public static OutsideConnectionTransferType UsableTypes(EntityManager entityManager, Entity connection)
        {
            return UsableTypes(entityManager, connection, out OutsideConnectionTransferType _);
        }

        /// <summary>
        /// The transfer types this connection can deliver arrivals by right now, and the ones its
        /// prefab declares — the difference is what no passenger line serves.
        /// </summary>
        public static OutsideConnectionTransferType UsableTypes(
            EntityManager entityManager, Entity connection, out OutsideConnectionTransferType declared)
        {
            declared = OutsideConnectionTransferType.None;

            if (!entityManager.HasComponent<PrefabRef>(connection))
            {
                return OutsideConnectionTransferType.None;
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(connection).m_Prefab;

            if (!entityManager.HasComponent<OutsideConnectionData>(prefab))
            {
                return OutsideConnectionTransferType.None;
            }

            declared = entityManager.GetComponentData<OutsideConnectionData>(prefab).m_Type
                       & OutsideConnectionTransferType.All;

            OutsideConnectionTransferType usable = declared & OutsideConnectionTransferType.Road;

            if ((declared & ~OutsideConnectionTransferType.Road) != 0)
            {
                usable |= declared & ServedTypes(entityManager, connection, 0);
            }

            return usable;
        }

        /// <summary>
        /// Where a visitor set down at this connection actually comes into the city: the city-side
        /// stop of a passenger line serving it, or Entity.Null for a connection that needs none.
        ///
        /// A road connection sits on the road, so a search can start from it directly. A rail, air
        /// or sea connection is a marker at the map edge with no walkable lane near it: a pathfind
        /// origin placed there finds nothing, which is why TouristNoTarget took about nine arrivals
        /// in ten once those visitors were no longer booked into a room without searching. The
        /// visitor's own journey from the connection is fine — the game routes it along the line —
        /// but a search for somewhere to stay has to start where that journey ends.
        /// </summary>
        public static Entity CityEntryStop(EntityManager entityManager, Entity connection)
        {
            UsableTypes(entityManager, connection, out OutsideConnectionTransferType declared);

            if ((declared & OutsideConnectionTransferType.Road) != 0)
            {
                return Entity.Null;
            }

            return FindCityStop(entityManager, connection, 0);
        }

        /// <summary>
        /// The first stop, on any passenger line serving this entity or below it, that is not
        /// itself part of an outside connection.
        /// </summary>
        private static Entity FindCityStop(EntityManager entityManager, Entity entity, int depth)
        {
            if (entityManager.HasBuffer<ConnectedRoute>(entity))
            {
                DynamicBuffer<ConnectedRoute> routes =
                    entityManager.GetBuffer<ConnectedRoute>(entity, isReadOnly: true);

                for (int i = 0; i < routes.Length; i++)
                {
                    Entity waypoint = routes[i].m_Waypoint;

                    if (LineType(entityManager, waypoint) == OutsideConnectionTransferType.None)
                    {
                        continue;
                    }

                    Entity stop = CityStopOnRoute(
                        entityManager, entityManager.GetComponentData<Owner>(waypoint).m_Owner);

                    if (stop != Entity.Null)
                    {
                        return stop;
                    }
                }
            }

            if (depth < kMaxDepth && entityManager.HasBuffer<Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects =
                    entityManager.GetBuffer<Game.Objects.SubObject>(entity, isReadOnly: true);

                for (int i = 0; i < subObjects.Length; i++)
                {
                    Entity stop = FindCityStop(entityManager, subObjects[i].m_SubObject, depth + 1);

                    if (stop != Entity.Null)
                    {
                        return stop;
                    }
                }
            }

            return Entity.Null;
        }

        /// <summary>The first stop on the route whose building is not an outside connection.</summary>
        private static Entity CityStopOnRoute(EntityManager entityManager, Entity route)
        {
            if (!entityManager.HasBuffer<RouteWaypoint>(route))
            {
                return Entity.Null;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                entityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (!entityManager.HasComponent<Connected>(waypoint))
                {
                    continue;
                }

                Entity stop = entityManager.GetComponentData<Connected>(waypoint).m_Connected;

                if (stop != Entity.Null && entityManager.Exists(stop) && !InOutsideConnection(entityManager, stop))
                {
                    return stop;
                }
            }

            return Entity.Null;
        }

        /// <summary>Whether the entity, or anything owning it, is an outside connection.</summary>
        private static bool InOutsideConnection(EntityManager entityManager, Entity entity)
        {
            for (int hop = 0; hop <= kMaxDepth && entity != Entity.Null; hop++)
            {
                if (entityManager.HasComponent<Game.Objects.OutsideConnection>(entity))
                {
                    return true;
                }

                entity = entityManager.HasComponent<Owner>(entity)
                    ? entityManager.GetComponentData<Owner>(entity).m_Owner
                    : Entity.Null;
            }

            return false;
        }

        /// <summary>
        /// Transfer types of the passenger lines whose stops are this entity or below it — the same
        /// walk as BuildingUtils.GetNumberOfConnectedLines, reduced to the modes those lines carry.
        /// </summary>
        private static OutsideConnectionTransferType ServedTypes(
            EntityManager entityManager, Entity entity, int depth)
        {
            OutsideConnectionTransferType served = OutsideConnectionTransferType.None;

            if (entityManager.HasBuffer<ConnectedRoute>(entity))
            {
                DynamicBuffer<ConnectedRoute> routes =
                    entityManager.GetBuffer<ConnectedRoute>(entity, isReadOnly: true);

                for (int i = 0; i < routes.Length; i++)
                {
                    served |= LineType(entityManager, routes[i].m_Waypoint);
                }
            }

            if (depth < kMaxDepth && entityManager.HasBuffer<Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects =
                    entityManager.GetBuffer<Game.Objects.SubObject>(entity, isReadOnly: true);

                for (int i = 0; i < subObjects.Length; i++)
                {
                    served |= ServedTypes(entityManager, subObjects[i].m_SubObject, depth + 1);
                }
            }

            return served;
        }

        /// <summary>The arrival mode a waypoint's line provides, or None if it carries no one.</summary>
        private static OutsideConnectionTransferType LineType(EntityManager entityManager, Entity waypoint)
        {
            if (waypoint == Entity.Null || !entityManager.HasComponent<Owner>(waypoint))
            {
                return OutsideConnectionTransferType.None;
            }

            Entity route = entityManager.GetComponentData<Owner>(waypoint).m_Owner;

            if (route == Entity.Null
                || !entityManager.Exists(route)
                || entityManager.HasComponent<Deleted>(route)
                || !entityManager.HasComponent<PrefabRef>(route))
            {
                return OutsideConnectionTransferType.None;
            }

            Entity prefab = entityManager.GetComponentData<PrefabRef>(route).m_Prefab;

            if (!entityManager.HasComponent<TransportLineData>(prefab))
            {
                return OutsideConnectionTransferType.None;
            }

            TransportLineData line = entityManager.GetComponentData<TransportLineData>(prefab);

            if (!line.m_PassengerTransport)
            {
                return OutsideConnectionTransferType.None;
            }

            switch (line.m_TransportType)
            {
                case TransportType.Airplane:
                    return OutsideConnectionTransferType.Air;
                case TransportType.Ship:
                case TransportType.Ferry:
                    return OutsideConnectionTransferType.Ship;
                case TransportType.Train:
                    return OutsideConnectionTransferType.Train;
                default:
                    return OutsideConnectionTransferType.None;
            }
        }
    }
}
