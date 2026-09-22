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
                    matches.Add(record);
                }
            }

            if (sortByTraffic)
            {
                // Worst first: a segment is only a problem when it is both slow and busy.
                matches.Sort((a, b) => (b.Congestion * b.Volume).CompareTo(a.Congestion * a.Volume));
            }

            var results = new List<object>();
            for (int i = offset; i < matches.Count && results.Count < limit; i++)
            {
                RoadRecord r = matches[i];
                results.Add(new
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
                        }
                        : null,
                });
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
                    + "1 - averageSpeed/speedLimit; pass sort=traffic to rank jams worst-first.",
                roads = results,
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
                default:
                    return BridgeResponse.Error(400, "category must be 'building', 'road', 'net' (all networks incl. pipes/power/tracks/paths) or 'tree'");
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

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueRoad(prefabEntity, prefab, start, end, mid, hasMid, elevations, request))
            {
                return BridgeResponse.Error(409, "another build operation is in progress, retry shortly");
            }
            return null;
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
                        });
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                totalMatches = total,
                returned = results.Count,
                note = "use entity index+version with /build/demolish",
                buildings = results,
            });
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
