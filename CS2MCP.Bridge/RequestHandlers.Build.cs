using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    /// <summary>
    /// Construction endpoints: prefab search, building placement (via
    /// BridgeToolSystem), placed-building listing and demolition.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_BuildingPrefabQuery;
        private bool m_BuildingPrefabQueryCreated;
        private EntityQuery m_RoadPrefabQuery;
        private bool m_RoadPrefabQueryCreated;
        private EntityQuery m_PlacedBuildingQuery;
        private bool m_PlacedBuildingQueryCreated;
        private EntityQuery m_PlacedRoadQuery;
        private bool m_PlacedRoadQueryCreated;
        private EntityQuery m_NetPrefabQuery;
        private bool m_NetPrefabQueryCreated;
        private EntityQuery m_TreePrefabQuery;
        private bool m_TreePrefabQueryCreated;

        private EntityQuery NetPrefabQuery
        {
            get
            {
                if (!m_NetPrefabQueryCreated)
                {
                    m_NetPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<NetGeometryData>());
                    m_NetPrefabQueryCreated = true;
                }
                return m_NetPrefabQuery;
            }
        }

        private EntityQuery m_RoutePrefabQuery;
        private bool m_RoutePrefabQueryCreated;

        private EntityQuery RoutePrefabQuery
        {
            get
            {
                if (!m_RoutePrefabQueryCreated)
                {
                    m_RoutePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<TransportLineData>());
                    m_RoutePrefabQueryCreated = true;
                }
                return m_RoutePrefabQuery;
            }
        }

        private EntityQuery TreePrefabQuery
        {
            get
            {
                if (!m_TreePrefabQueryCreated)
                {
                    m_TreePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<TreeData>());
                    m_TreePrefabQueryCreated = true;
                }
                return m_TreePrefabQuery;
            }
        }

        private EntityQuery BuildingPrefabQuery
        {
            get
            {
                if (!m_BuildingPrefabQueryCreated)
                {
                    m_BuildingPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<BuildingData>());
                    m_BuildingPrefabQueryCreated = true;
                }
                return m_BuildingPrefabQuery;
            }
        }

        private EntityQuery RoadPrefabQuery
        {
            get
            {
                if (!m_RoadPrefabQueryCreated)
                {
                    m_RoadPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<RoadData>());
                    m_RoadPrefabQueryCreated = true;
                }
                return m_RoadPrefabQuery;
            }
        }

        private EntityQuery PlacedBuildingQuery
        {
            get
            {
                if (!m_PlacedBuildingQueryCreated)
                {
                    m_PlacedBuildingQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Buildings.Building>(),
                            ComponentType.ReadOnly<Transform>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_PlacedBuildingQueryCreated = true;
                }
                return m_PlacedBuildingQuery;
            }
        }

        private EntityQuery PlacedRoadQuery
        {
            get
            {
                if (!m_PlacedRoadQueryCreated)
                {
                    m_PlacedRoadQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Net.Edge>(),
                            ComponentType.ReadOnly<Game.Net.Curve>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                            ComponentType.ReadOnly<Game.Common.Owner>(),
                        },
                    });
                    m_PlacedRoadQueryCreated = true;
                }
                return m_PlacedRoadQuery;
            }
        }

        private BridgeResponse ListRoads(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            request.Query.TryGetValue("query", out string search);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 500) : 100;
            int offset = request.TryGetInt("offset", out int rawOffset) ? math.max(rawOffset, 0) : 0;
            bool sortByTraffic = request.Query.TryGetValue("sort", out string sortMode)
                && string.Equals(sortMode, "traffic", StringComparison.OrdinalIgnoreCase);
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 250f;
            float2 center = new float2(x, z);

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heightData = terrain.GetHeightData();
            List<RoadRecord> matches = CollectRoads(search, hasCenter, center, radius);

            if (sortByTraffic)
            {
                matches.Sort((a, b) => b.JamScore.CompareTo(a.JamScore));
            }

            var results = new List<object>();
            for (int i = offset; i < matches.Count && results.Count < limit; i++)
            {
                RoadRecord r = matches[i];
                results.Add(DescribeRoad(r));
            }

            return BridgeResponse.Json(new
            {
                totalMatches = matches.Count,
                returned = results.Count,
                offset,
                nextOffset = offset + results.Count < matches.Count ? (int?)(offset + results.Count) : null,
                note = "one entry per road segment (edge); use entity index+version with /build/demolish "
                    + "or /build/upgrade; elevation feeds e1/e2 and mid feeds cx/cz of /build/road. "
                    + "traffic.volume is accumulated vehicle-time on the edge, congestion is "
                    + "1 - averageSpeed/speedLimit, bottleneck is the game's own jam marker; "
                    + "pass sort=traffic to rank jams worst-first or see /city/traffic for a summary.",
                roads = results,
            });
        }

        private List<RoadRecord> CollectRoads(string search, bool hasCenter, float2 center, float radius)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heightData = terrain.GetHeightData();
            var matches = new List<RoadRecord>();
            using (NativeArray<Entity> entities = PlacedRoadQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(entity);
                    float2 midpoint = (curve.m_Bezier.a.xz + curve.m_Bezier.d.xz) * 0.5f;
                    if (hasCenter && math.distance(midpoint, center) > radius)
                    {
                        continue;
                    }
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(prefabRef.m_Prefab);
                    string name = prefab != null ? prefab.name : "<unknown>";
                    if (!string.IsNullOrEmpty(search)
                        && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var record = new RoadRecord
                    {
                        Entity = entity,
                        Prefab = name,
                        Curve = curve.m_Bezier,
                        Length = curve.m_Length,
                        StartElevation = curve.m_Bezier.a.y - TerrainUtils.SampleHeight(ref heightData, curve.m_Bezier.a),
                        EndElevation = curve.m_Bezier.d.y - TerrainUtils.SampleHeight(ref heightData, curve.m_Bezier.d),
                        SpeedLimit = -1f,
                    };
                    ReadTraffic(entity, prefabRef.m_Prefab, record);
                    ReadBottleneck(entity, record);
                    ReadOutsideConnection(entity, record);
                    if (EntityManager.HasComponent<Game.Net.Density>(entity))
                    {
                        record.HasDensity = true;
                        record.Density = EntityManager.GetComponentData<Game.Net.Density>(entity).m_Density;
                    }
                    matches.Add(record);
                }
            }

            return matches;
        }

        private object DescribeRoad(RoadRecord r)
        {
            return new
                {
                    entity = new { index = r.Entity.Index, version = r.Entity.Version },
                    prefab = r.Prefab,
                    start = new
                    {
                        x = r.Curve.a.x,
                        z = r.Curve.a.z,
                        y = r.Curve.a.y,
                        elevation = r.StartElevation,
                    },
                    end = new
                    {
                        x = r.Curve.d.x,
                        z = r.Curve.d.z,
                        y = r.Curve.d.y,
                        elevation = r.EndElevation,
                    },
                    // Point on the curve at t=0.5: feed back as cx/cz to /build/road
                    // to reproduce the bend instead of a straight chord.
                    mid = new
                    {
                        x = MathUtils.Position(r.Curve, 0.5f).x,
                        z = MathUtils.Position(r.Curve, 0.5f).z,
                    },
                    length = r.Length,
                    traffic = r.HasTraffic
                        ? new
                        {
                            volume = r.Volume,
                            averageSpeed = r.AverageSpeed,
                            speedLimit = r.SpeedLimit,
                            congestion = r.Congestion,
                            density = r.HasDensity ? (float?)r.Density : null,
                            jamScore = r.JamScore,
                        }
                        : null,
                    // The game's own jam marker, not a derived guess.
                    bottleneck = r.HasBottleneck
                        ? new
                        {
                            position = r.BottleneckPosition,
                            from = r.BottleneckFrom,
                            to = r.BottleneckTo,
                            timer = r.BottleneckTimer,
                        }
                        : null,
                    outsideConnection = r.IsOutsideConnection
                        ? new { delay = r.OutsideDelay }
                        : null,
                };
        }

        /// <summary>
        /// City-wide traffic summary: where the jams are, without having to page
        /// through every segment first.
        /// </summary>
        private BridgeResponse TrafficReport(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            request.Query.TryGetValue("query", out string search);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 100) : 15;
            bool hasCenter = request.TryGetFloat("x", out float x) & request.TryGetFloat("z", out float z);
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 250f;

            List<RoadRecord> roads = CollectRoads(search, hasCenter, new float2(x, z), radius);

            float totalVolume = 0f;
            float weightedCongestion = 0f;
            int bottlenecks = 0;
            var outsideConnections = new List<object>();
            foreach (RoadRecord r in roads)
            {
                totalVolume += r.Volume;
                weightedCongestion += r.Congestion * r.Volume;
                if (r.HasBottleneck)
                {
                    bottlenecks++;
                }
                if (r.IsOutsideConnection)
                {
                    outsideConnections.Add(new
                    {
                        entity = new { index = r.Entity.Index, version = r.Entity.Version },
                        prefab = r.Prefab,
                        at = new { x = r.Curve.a.x, z = r.Curve.a.z },
                        delay = r.OutsideDelay,
                        congestion = r.Congestion,
                    });
                }
            }

            roads.Sort((a, b) => b.JamScore.CompareTo(a.JamScore));
            var worst = new List<object>();
            for (int i = 0; i < roads.Count && worst.Count < limit; i++)
            {
                if (roads[i].JamScore <= 0f)
                {
                    break;
                }
                worst.Add(DescribeRoad(roads[i]));
            }

            return BridgeResponse.Json(new
            {
                segments = roads.Count,
                // Volume-weighted, so empty back streets cannot dilute the number.
                averageCongestion = totalVolume > 0.0001f ? weightedCongestion / totalVolume : 0f,
                bottlenecks,
                outsideConnections,
                worstSegments = worst,
                note = "worstSegments is ranked by congestion x volume plus the game's own bottleneck "
                    + "timer; bottleneck.from/to locate the jam along the segment as 0-1 fractions. "
                    + "Filter with x/z/radius or query to report on one area.",
            });
        }

        private sealed class RoadRecord
        {
            public Entity Entity;
            public string Prefab;
            public Bezier4x3 Curve;
            public float Length;
            public float StartElevation;
            public float EndElevation;
            public bool HasTraffic;
            public float Volume;
            public float AverageSpeed;
            public float SpeedLimit;
            public float Congestion;
            public bool HasDensity;
            public float Density;
            public bool HasBottleneck;
            public float BottleneckPosition;
            public float BottleneckFrom;
            public float BottleneckTo;
            public int BottleneckTimer;
            public bool IsOutsideConnection;
            public float OutsideDelay;

            /// <summary>Ranking score: a slow empty road is not a jam, nor is a busy fast one.</summary>
            public float JamScore => (Congestion * Volume) + (HasBottleneck ? BottleneckTimer : 0f);
        }

        /// <summary>
        /// Game.Net.Bottleneck is what TrafficBottleneckSystem writes when vehicles
        /// pile up; it lives on the lanes, so the edge's sub-lanes are checked too.
        /// Positions are bytes over the lane, reported here as 0-1 fractions.
        /// </summary>
        private void ReadBottleneck(Entity entity, RoadRecord record)
        {
            if (TryReadBottleneck(entity, record))
            {
                return;
            }
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(entity))
            {
                return;
            }
            DynamicBuffer<Game.Net.SubLane> lanes = EntityManager.GetBuffer<Game.Net.SubLane>(entity, isReadOnly: true);
            for (int i = 0; i < lanes.Length; i++)
            {
                // Keep the longest-standing one: that is the lane actually holding the jam.
                TryReadBottleneck(lanes[i].m_SubLane, record);
            }
        }

        private bool TryReadBottleneck(Entity candidate, RoadRecord record)
        {
            if (candidate == Entity.Null
                || !EntityManager.Exists(candidate)
                || !EntityManager.HasComponent<Game.Net.Bottleneck>(candidate))
            {
                return false;
            }
            Game.Net.Bottleneck bottleneck = EntityManager.GetComponentData<Game.Net.Bottleneck>(candidate);
            if (record.HasBottleneck && bottleneck.m_Timer <= record.BottleneckTimer)
            {
                return true;
            }
            record.HasBottleneck = true;
            record.BottleneckPosition = bottleneck.m_Position / 255f;
            record.BottleneckFrom = bottleneck.m_MinPos / 255f;
            record.BottleneckTo = bottleneck.m_MaxPos / 255f;
            record.BottleneckTimer = bottleneck.m_Timer;
            return true;
        }

        /// <summary>
        /// Outside connections live on the end nodes, and their delay is how long
        /// traffic queues to leave or enter the map there.
        /// </summary>
        private void ReadOutsideConnection(Entity entity, RoadRecord record)
        {
            if (!EntityManager.HasComponent<Game.Net.Edge>(entity))
            {
                return;
            }
            Game.Net.Edge edge = EntityManager.GetComponentData<Game.Net.Edge>(entity);
            foreach (Entity node in new[] { edge.m_Start, edge.m_End })
            {
                if (node == Entity.Null
                    || !EntityManager.Exists(node)
                    || !EntityManager.HasComponent<Game.Net.OutsideConnection>(node))
                {
                    continue;
                }
                record.IsOutsideConnection = true;
                record.OutsideDelay = math.max(
                    record.OutsideDelay,
                    EntityManager.GetComponentData<Game.Net.OutsideConnection>(node).m_Delay);
            }
        }

        /// <summary>
        /// Game.Net.Road carries the same accumulated flow the traffic infoview
        /// colours edges with: duration is vehicle-time, distance is vehicle-metres,
        /// split over two direction sets of four time buckets each.
        /// </summary>
        private void ReadTraffic(Entity entity, Entity prefabEntity, RoadRecord record)
        {
            if (!EntityManager.HasComponent<Game.Net.Road>(entity))
            {
                return;
            }
            Game.Net.Road road = EntityManager.GetComponentData<Game.Net.Road>(entity);
            float duration = math.csum(road.m_TrafficFlowDuration0) + math.csum(road.m_TrafficFlowDuration1);
            float distance = math.csum(road.m_TrafficFlowDistance0) + math.csum(road.m_TrafficFlowDistance1);

            record.HasTraffic = true;
            record.Volume = duration;
            record.AverageSpeed = duration > 0.0001f ? distance / duration : 0f;

            if (EntityManager.HasComponent<RoadData>(prefabEntity))
            {
                record.SpeedLimit = EntityManager.GetComponentData<RoadData>(prefabEntity).m_SpeedLimit;
            }
            // No traffic yet means no evidence of a jam, not a free-flowing road.
            record.Congestion = record.SpeedLimit > 0.0001f && duration > 0.0001f
                ? math.saturate(1f - (record.AverageSpeed / record.SpeedLimit))
                : 0f;
        }

        private BridgeResponse GetPrefabs(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            string category = request.Query.TryGetValue("category", out string rawCategory)
                ? rawCategory.ToLowerInvariant()
                : "building";
            EntityQuery query;
            switch (category)
            {
                case "building":
                    query = BuildingPrefabQuery;
                    break;
                case "road":
                    query = RoadPrefabQuery;
                    break;
                case "net":
                    query = NetPrefabQuery;
                    break;
                case "tree":
                    query = TreePrefabQuery;
                    break;
                case "route":
                    query = RoutePrefabQuery;
                    break;
                default:
                    return BridgeResponse.Error(400, "category must be 'building', 'road', 'net' (all networks incl. pipes/power/tracks/paths), 'tree' or 'route' (transport lines)");
            }

            request.Query.TryGetValue("query", out string search);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 200) : 50;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var results = new List<object>();
            int total = 0;
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(search)
                        && prefab.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    total++;
                    if (results.Count < limit)
                    {
                        results.Add(new
                        {
                            name = prefab.name,
                            type = prefab.GetType().Name,
                            locked = IsLocked(entity),
                        });
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                category,
                totalMatches = total,
                returned = results.Count,
                note = "use the exact 'name' value with /build/place; locked prefabs need milestone progress",
                stalenessWarning = LockStalenessWarning,
                prefabs = results,
            });
        }

        private BridgeResponse PlaceBuilding(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<name from /prefabs>");
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "provide ?x=<float>&z=<float> world coordinates");
            }
            request.TryGetFloat("rotation", out float rotationDegrees);

            if (!TryFindPrefabByName(BuildingPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab)
                && !TryFindPrefabByName(TreePrefabQuery, prefabName, out prefabEntity, out prefab))
            {
                return BridgeResponse.Error(404, $"unknown building/tree prefab '{prefabName}'; search via /prefabs?category=building|tree&query=...");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked (milestone not reached); pass force=true to place anyway");
            }

            float3 position = new float3(x, 0f, z);
            if (request.TryGetFloat("y", out float y))
            {
                position.y = y;
            }
            else
            {
                TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
                TerrainHeightData heightData = terrain.GetHeightData();
                position.y = TerrainUtils.SampleHeight(ref heightData, position);
            }
            quaternion rotation = quaternion.RotateY(math.radians(rotationDegrees));

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueuePlacement(prefabEntity, prefab, position, rotation, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            // Completed asynchronously by BridgeToolSystem over the next tool frames.
            return null;
        }

        private BridgeResponse BuildRoad(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<name from /prefabs?category=road>");
            }
            if (!request.TryGetFloat("x1", out float x1) || !request.TryGetFloat("z1", out float z1)
                || !request.TryGetFloat("x2", out float x2) || !request.TryGetFloat("z2", out float z2))
            {
                return BridgeResponse.Error(400, "provide ?x1=&z1=&x2=&z2= world coordinates for both endpoints");
            }

            float length = math.distance(new float2(x1, z1), new float2(x2, z2));
            if (length < 8f)
            {
                return BridgeResponse.Error(400, $"segment too short ({length:F1}m); minimum ~8m");
            }
            if (length > 1500f)
            {
                return BridgeResponse.Error(400, $"segment too long ({length:F0}m); split into segments of <=1500m");
            }

            if (!TryFindPrefabByName(NetPrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"unknown network prefab '{prefabName}'; search via /prefabs?category=road|net&query=...");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked (milestone not reached); pass force=true to build anyway");
            }

            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heightData = terrain.GetHeightData();
            float3 start = new float3(x1, 0f, z1);
            start.y = TerrainUtils.SampleHeight(ref heightData, start);
            float3 end = new float3(x2, 0f, z2);
            end.y = TerrainUtils.SampleHeight(ref heightData, end);

            bool hasMid = request.TryGetFloat("cx", out float cx) & request.TryGetFloat("cz", out float cz);
            float3 mid = default;
            if (hasMid)
            {
                mid = new float3(cx, 0f, cz);
                mid.y = TerrainUtils.SampleHeight(ref heightData, mid);
            }

            request.TryGetFloat("e1", out float e1);
            request.TryGetFloat("e2", out float e2);
            var elevations = new float2(math.clamp(e1, -30f, 60f), math.clamp(e2, -30f, 60f));

            if (!TryResolveAttachment(request, "start", start, out Entity startEdge, out float startSplit, out BridgeResponse startError))
            {
                return startError;
            }
            if (!TryResolveAttachment(request, "end", end, out Entity endEdge, out float endSplit, out BridgeResponse endError))
            {
                return endError;
            }

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueRoad(prefabEntity, prefab, start, end, mid, hasMid, elevations,
                startEdge, startSplit, endEdge, endSplit, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private BridgeResponse BuildRoute(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("prefab", out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                return BridgeResponse.Error(400, "provide ?prefab=<name from /prefabs?category=route>");
            }
            if (!request.Query.TryGetValue("stops", out string stopsRaw) || string.IsNullOrEmpty(stopsRaw))
            {
                return BridgeResponse.Error(400,
                    "provide ?stops=index:version,index:version,... listing the stops in travel order "
                    + "(transport stop entities from /city/buildings)");
            }
            if (!TryFindPrefabByName(RoutePrefabQuery, prefabName, out Entity prefabEntity, out PrefabBase prefab))
            {
                return BridgeResponse.Error(404, $"unknown transport line prefab '{prefabName}'; search via /prefabs?category=route");
            }
            if (IsLocked(prefabEntity) && !IsForced(request))
            {
                return BridgeResponse.Error(409, $"prefab '{prefab.name}' is locked (milestone not reached); pass force=true to build anyway");
            }

            string[] parts = stopsRaw.Split(',');
            var stops = new List<Entity>();
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                string[] pair = trimmed.Split(':');
                if (pair.Length != 2
                    || !int.TryParse(pair[0], out int index)
                    || !int.TryParse(pair[1], out int version))
                {
                    return BridgeResponse.Error(400, $"stop '{trimmed}' is not in index:version form");
                }
                var stop = new Entity { Index = index, Version = version };
                if (!EntityManager.Exists(stop))
                {
                    return BridgeResponse.Error(404, $"stop entity {trimmed} does not exist");
                }
                stops.Add(stop);
            }
            if (stops.Count < 2)
            {
                return BridgeResponse.Error(400, $"a line needs at least 2 stops, got {stops.Count}");
            }

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueRoute(prefabEntity, prefab, stops.ToArray(), request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private BridgeResponse ListRoutes(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var results = new List<object>();
            using (EntityQuery query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.ReadOnly<Game.Routes.TransportLine>()))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Game.Routes.TransportLine line = EntityManager.GetComponentData<Game.Routes.TransportLine>(entity);
                    string name = "<unknown>";
                    if (EntityManager.HasComponent<PrefabRef>(entity))
                    {
                        PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(
                            EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                        name = prefab != null ? prefab.name : name;
                    }

                    int stops = EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(entity)
                        ? EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(entity, isReadOnly: true).Length
                        : 0;
                    int vehicles = EntityManager.HasBuffer<Game.Routes.RouteVehicle>(entity)
                        ? EntityManager.GetBuffer<Game.Routes.RouteVehicle>(entity, isReadOnly: true).Length
                        : 0;

                    results.Add(new
                    {
                        entity = new { index = entity.Index, version = entity.Version },
                        prefab = name,
                        stops,
                        vehicles,
                        vehicleInterval = line.m_VehicleInterval,
                        ticketPrice = line.m_TicketPrice,
                        notEnoughVehicles =
                            (line.m_Flags & Game.Routes.TransportLineFlags.NotEnoughVehicles) != 0,
                        waiting = ReadWaitingPassengers(entity),
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                count = results.Count,
                note = "transport lines in the city; waiting sums the passengers queueing at the stops "
                    + "and their average wait, which is what tells an overloaded line from an idle one",
                routes = results,
            });
        }

        private object ReadWaitingPassengers(Entity route)
        {
            if (!EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(route))
            {
                return null;
            }
            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(route, isReadOnly: true);
            int total = 0;
            int longestWait = 0;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == Entity.Null
                    || !EntityManager.Exists(waypoint)
                    || !EntityManager.HasComponent<Game.Routes.WaitingPassengers>(waypoint))
                {
                    continue;
                }
                Game.Routes.WaitingPassengers waiting =
                    EntityManager.GetComponentData<Game.Routes.WaitingPassengers>(waypoint);
                total += waiting.m_Count;
                longestWait = math.max(longestWait, waiting.m_AverageWaitingTime);
            }
            return new { passengers = total, longestAverageWait = longestWait };
        }

        /// <summary>
        /// Reads startEdge/startSplit (or endEdge/endSplit). Without an explicit
        /// split the nearest point on that edge to the given coordinate is used,
        /// which is usually what "join the road here" means.
        /// </summary>
        private bool TryResolveAttachment(BridgeRequest request, string which, float3 point,
            out Entity edge, out float split, out BridgeResponse error)
        {
            edge = Entity.Null;
            split = 0f;
            error = null;

            if (!request.TryGetInt(which + "Edge", out int index))
            {
                return true;
            }
            if (!request.TryGetInt(which + "EdgeVersion", out int version))
            {
                error = BridgeResponse.Error(400, $"provide ?{which}EdgeVersion= alongside {which}Edge (both from /city/roads)");
                return false;
            }

            var candidate = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(candidate)
                || (!EntityManager.HasComponent<Game.Net.Edge>(candidate) && !EntityManager.HasComponent<Game.Net.Node>(candidate)))
            {
                error = BridgeResponse.Error(404, $"entity {index}:{version} is not an existing road segment or node");
                return false;
            }

            edge = candidate;
            if (request.TryGetFloat(which + "Split", out float rawSplit))
            {
                split = math.clamp(rawSplit, 0f, 1f);
                return true;
            }
            if (EntityManager.HasComponent<Game.Net.Curve>(candidate))
            {
                Bezier4x3 curve = EntityManager.GetComponentData<Game.Net.Curve>(candidate).m_Bezier;
                split = NearestSplit(curve, point);
            }
            return true;
        }

        /// <summary>Position along a curve closest to a world point, sampled then refined.</summary>
        private static float NearestSplit(Bezier4x3 curve, float3 point)
        {
            float best = 0f;
            float bestDistance = float.MaxValue;
            const int samples = 32;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                float distance = math.distancesq(MathUtils.Position(curve, t).xz, point.xz);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = t;
                }
            }
            float step = 1f / samples;
            for (int refine = 0; refine < 12; refine++)
            {
                step *= 0.5f;
                foreach (float t in new[] { best - step, best + step })
                {
                    float clamped = math.clamp(t, 0f, 1f);
                    float distance = math.distancesq(MathUtils.Position(curve, clamped).xz, point.xz);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = clamped;
                    }
                }
            }
            return best;
        }

        private static readonly Dictionary<string, (Game.Prefabs.CompositionFlags.General general, Game.Prefabs.CompositionFlags.Side side)> kUpgradeNames =
            new Dictionary<string, (Game.Prefabs.CompositionFlags.General, Game.Prefabs.CompositionFlags.Side)>(StringComparer.OrdinalIgnoreCase)
            {
                ["grass"] = (default, Game.Prefabs.CompositionFlags.Side.PrimaryBeautification),
                ["trees"] = (default, Game.Prefabs.CompositionFlags.Side.SecondaryBeautification),
                ["wideSidewalk"] = (default, Game.Prefabs.CompositionFlags.Side.WideSidewalk),
                ["soundBarrier"] = (default, Game.Prefabs.CompositionFlags.Side.SoundBarrier),
                ["parking"] = (default, Game.Prefabs.CompositionFlags.Side.ParkingSpaces),
                ["lighting"] = (Game.Prefabs.CompositionFlags.General.Lighting, default),
                ["medianGrass"] = (Game.Prefabs.CompositionFlags.General.PrimaryMiddleBeautification, default),
                ["medianTrees"] = (Game.Prefabs.CompositionFlags.General.SecondaryMiddleBeautification, default),
            };

        private BridgeResponse HandleUpgradeRoad(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(400, "provide ?index=&version= of a road segment from /city/roads");
            }
            request.Query.TryGetValue("upgrades", out string upgradesRaw);
            bool hasReplacement = request.Query.TryGetValue("prefab", out string replacementName)
                && !string.IsNullOrEmpty(replacementName);
            if (string.IsNullOrEmpty(upgradesRaw) && !hasReplacement)
            {
                return BridgeResponse.Error(400,
                    $"provide ?prefab=<road name> to change the road type, and/or ?upgrades=<comma list>: {string.Join(", ", kUpgradeNames.Keys)}");
            }

            var entity = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Game.Net.Edge>(entity))
            {
                return BridgeResponse.Error(404, $"entity {index}:{version} is not an existing road segment");
            }

            if (hasReplacement)
            {
                if (!string.IsNullOrEmpty(upgradesRaw))
                {
                    return BridgeResponse.Error(400,
                        "prefab and upgrades cannot be combined in one call; replace the road type first, then apply upgrades to the new segment");
                }
                if (!TryFindPrefabByName(NetPrefabQuery, replacementName, out Entity replacementEntity, out PrefabBase replacementPrefab))
                {
                    return BridgeResponse.Error(404, $"unknown network prefab '{replacementName}'; search via /prefabs?category=road|net&query=...");
                }
                if (IsLocked(replacementEntity) && !IsForced(request))
                {
                    return BridgeResponse.Error(409, $"prefab '{replacementPrefab.name}' is locked (milestone not reached); pass force=true to build anyway");
                }

                PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
                string currentName = null;
                if (EntityManager.HasComponent<PrefabRef>(entity))
                {
                    PrefabBase current = prefabs.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    currentName = current != null ? current.name : null;
                }

                BridgeToolSystem replaceTool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
                if (!replaceTool.TryQueueReplace(entity, replacementEntity, replacementPrefab, currentName, request))
                {
                    return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
                }
                return null;
            }

            string side = request.Query.TryGetValue("side", out string rawSide) ? rawSide.ToLowerInvariant() : "both";
            Game.Prefabs.CompositionFlags flags = default;
            foreach (string name in upgradesRaw.Split(','))
            {
                string trimmed = name.Trim();
                if (!kUpgradeNames.TryGetValue(trimmed, out (Game.Prefabs.CompositionFlags.General general, Game.Prefabs.CompositionFlags.Side side) mapped))
                {
                    return BridgeResponse.Error(400, $"unknown upgrade '{trimmed}'; valid: {string.Join(", ", kUpgradeNames.Keys)}");
                }
                flags.m_General |= mapped.general;
                if (side == "left" || side == "both")
                {
                    flags.m_Left |= mapped.side;
                }
                if (side == "right" || side == "both")
                {
                    flags.m_Right |= mapped.side;
                }
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string prefabName = null;
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                prefabName = prefab != null ? prefab.name : null;
            }

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueUpgrade(entity, prefabName, flags, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private BridgeResponse ListBuildings(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            request.Query.TryGetValue("query", out string search);
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 500) : 100;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var results = new List<object>();
            int total = 0;
            using (NativeArray<Entity> entities = PlacedBuildingQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(prefabRef.m_Prefab);
                    string name = prefab != null ? prefab.name : "<unknown>";
                    if (!string.IsNullOrEmpty(search)
                        && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    total++;
                    if (results.Count < limit)
                    {
                        Transform transform = EntityManager.GetComponentData<Transform>(entity);
                        results.Add(new
                        {
                            entity = new { index = entity.Index, version = entity.Version },
                            prefab = name,
                            isSubBuilding = EntityManager.HasComponent<Game.Common.Owner>(entity),
                            position = new
                            {
                                x = transform.m_Position.x,
                                y = transform.m_Position.y,
                                z = transform.m_Position.z,
                            },
                            status = ReadBuildingStatus(entity),
                            efficiency = ReadEfficiency(entity),
                            workers = ReadWorkers(entity),
                        });
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                totalMatches = total,
                returned = results.Count,
                note = "use entity index+version with /build/demolish; efficiency.factors names "
                    + "what is holding a building back (NotEnoughEmployees, ElectricitySupply, "
                    + "MaterialSupply...), workers shows staffing of the company renting it",
                buildings = results,
            });
        }

        private object ReadBuildingStatus(Entity entity)
        {
            bool abandoned = EntityManager.HasComponent<Game.Buildings.Abandoned>(entity);
            bool condemned = EntityManager.HasComponent<Game.Buildings.Condemned>(entity);
            float? condition = EntityManager.HasComponent<Game.Buildings.BuildingCondition>(entity)
                ? EntityManager.GetComponentData<Game.Buildings.BuildingCondition>(entity).m_Condition
                : (float?)null;
            if (!abandoned && !condemned && condition == null)
            {
                return null;
            }
            return new { abandoned, condemned, condition };
        }

        /// <summary>
        /// The Efficiency buffer is one entry per limiting factor, each a multiplier.
        /// Their product is the building's actual efficiency, and the entries below 1
        /// are the answer to "why is this building underperforming".
        /// </summary>
        private object ReadEfficiency(Entity entity)
        {
            if (!EntityManager.HasBuffer<Game.Buildings.Efficiency>(entity))
            {
                return null;
            }
            DynamicBuffer<Game.Buildings.Efficiency> buffer =
                EntityManager.GetBuffer<Game.Buildings.Efficiency>(entity, isReadOnly: true);
            float overall = 1f;
            var factors = new List<object>();
            for (int i = 0; i < buffer.Length; i++)
            {
                Game.Buildings.Efficiency item = buffer[i];
                overall *= item.m_Efficiency;
                if (item.m_Efficiency < 0.999f)
                {
                    factors.Add(new
                    {
                        factor = item.m_Factor.ToString(),
                        efficiency = item.m_Efficiency,
                    });
                }
            }
            return new { value = overall, factors };
        }

        /// <summary>
        /// Workplaces belong to the company renting the building, not the building,
        /// so the Renter buffer has to be walked to reach WorkProvider.
        /// </summary>
        private object ReadWorkers(Entity entity)
        {
            if (!EntityManager.HasBuffer<Game.Buildings.Renter>(entity))
            {
                return null;
            }
            DynamicBuffer<Game.Buildings.Renter> renters =
                EntityManager.GetBuffer<Game.Buildings.Renter>(entity, isReadOnly: true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity company = renters[i].m_Renter;
                if (company == Entity.Null
                    || !EntityManager.Exists(company)
                    || !EntityManager.HasComponent<Game.Companies.WorkProvider>(company))
                {
                    continue;
                }
                Game.Companies.WorkProvider provider =
                    EntityManager.GetComponentData<Game.Companies.WorkProvider>(company);
                int employees = EntityManager.HasBuffer<Game.Companies.Employee>(company)
                    ? EntityManager.GetBuffer<Game.Companies.Employee>(company, isReadOnly: true).Length
                    : 0;
                int? profitability = EntityManager.HasComponent<Game.Companies.Profitability>(company)
                    ? EntityManager.GetComponentData<Game.Companies.Profitability>(company).m_Profitability
                    : (int?)null;
                return new
                {
                    employees,
                    maxWorkers = provider.m_MaxWorkers,
                    shortage = math.max(0, provider.m_MaxWorkers - employees),
                    profitability,
                };
            }
            return null;
        }

        private BridgeResponse Demolish(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(400, "provide ?index=<int>&version=<int> from /city/buildings");
            }

            var entity = new Entity { Index = index, Version = version };
            if (!EntityManager.Exists(entity))
            {
                return BridgeResponse.Error(404, $"entity {index}:{version} does not exist (stale id?)");
            }
            bool isBuilding = EntityManager.HasComponent<Game.Buildings.Building>(entity);
            bool isNetEdge = EntityManager.HasComponent<Game.Net.Edge>(entity);
            bool isFlora = EntityManager.HasComponent<Game.Objects.Tree>(entity)
                || EntityManager.HasComponent<Game.Objects.Plant>(entity);
            bool isDistrict = EntityManager.HasComponent<Game.Areas.District>(entity);
            if (!isBuilding && !isNetEdge && !isFlora && !isDistrict)
            {
                return BridgeResponse.Error(400, "entity is not a building, road segment, tree/plant or district; refusing to delete");
            }
            if (EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                return BridgeResponse.Error(409, "entity is already being deleted");
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string prefabName = null;
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                prefabName = prefab != null ? prefab.name : null;
            }

            // Deletion MUST go through the game's bulldoze pipeline. Adding a raw
            // Deleted component skips node/block/lane cleanup and corrupts state.
            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueDemolish(entity, prefabName, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private static bool IsForced(BridgeRequest request)
        {
            return request.TryGetBool("force", out bool force) && force;
        }

        private bool TryFindPrefabByName(EntityQuery query, string name, out Entity prefabEntity, out PrefabBase prefab)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase candidate = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (candidate != null && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        prefabEntity = entity;
                        prefab = candidate;
                        return true;
                    }
                }
            }
            prefabEntity = Entity.Null;
            prefab = null;
            return false;
        }
    }
}
