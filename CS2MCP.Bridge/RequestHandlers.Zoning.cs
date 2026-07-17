using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Zoning endpoints. Zone cells live in a Cell buffer on Block entities
    /// (blocks are auto-created along roads). Painting = rewriting Cell.m_Zone
    /// for matching cells and marking the block Updated so the game's zone and
    /// spawning systems react - same net effect as the zone tool's apply job.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private const float kCellSize = 8f;

        private EntityQuery m_ZonePrefabQuery;
        private bool m_ZonePrefabQueryCreated;
        private EntityQuery m_ZoneBlockQuery;
        private bool m_ZoneBlockQueryCreated;

        private EntityQuery ZonePrefabQuery
        {
            get
            {
                if (!m_ZonePrefabQueryCreated)
                {
                    m_ZonePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ZoneData>());
                    m_ZonePrefabQueryCreated = true;
                }
                return m_ZonePrefabQuery;
            }
        }

        private EntityQuery ZoneBlockQuery
        {
            get
            {
                if (!m_ZoneBlockQueryCreated)
                {
                    m_ZoneBlockQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Block>(),
                            ComponentType.ReadOnly<Cell>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_ZoneBlockQueryCreated = true;
                }
                return m_ZoneBlockQuery;
            }
        }

        private BridgeResponse GetZoneTypes()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var zones = new List<object>();
            using (NativeArray<Entity> entities = ZonePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    ZoneData zoneData = EntityManager.GetComponentData<ZoneData>(entity);
                    zones.Add(new
                    {
                        name = prefab.name,
                        areaType = zoneData.m_AreaType.ToString(),
                        office = zoneData.IsOffice(),
                        locked = IsLocked(entity),
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "use 'name' with /build/zone; zone 'None' clears zoning (dezone)",
                stalenessWarning = LockStalenessWarning,
                zones,
            });
        }

        private BridgeResponse ZoneArea(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("zone", out string zoneName) || string.IsNullOrEmpty(zoneName))
            {
                return BridgeResponse.Error(400, "provide ?zone=<name from /zones, or 'None' to dezone>");
            }
            if (!request.TryGetFloat("x", out float x) || !request.TryGetFloat("z", out float z))
            {
                return BridgeResponse.Error(400, "provide ?x=&z= center coordinates");
            }
            float radius = request.TryGetFloat("radius", out float rawRadius)
                ? math.clamp(rawRadius, kCellSize, 200f)
                : 32f;

            ZoneType targetZone;
            string resolvedName;
            if (string.Equals(zoneName, "None", StringComparison.OrdinalIgnoreCase))
            {
                targetZone = ZoneType.None;
                resolvedName = "None";
            }
            else
            {
                if (!TryFindPrefabByName(ZonePrefabQuery, zoneName, out Entity zonePrefabEntity, out PrefabBase zonePrefab))
                {
                    return BridgeResponse.Error(404, $"unknown zone '{zoneName}'; list via /zones");
                }
                if (IsLocked(zonePrefabEntity) && !IsForced(request))
                {
                    return BridgeResponse.Error(409, $"zone '{zonePrefab.name}' is locked (milestone not reached); pass force=true to zone anyway");
                }
                targetZone = EntityManager.GetComponentData<ZoneData>(zonePrefabEntity).m_ZoneType;
                resolvedName = zonePrefab.name;
            }

            float2 center = new float2(x, z);
            int cellsChanged = 0;
            int blocksTouched = 0;

            using (NativeArray<Entity> blocks = ZoneBlockQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity blockEntity in blocks)
                {
                    Block block = EntityManager.GetComponentData<Block>(blockEntity);
                    float blockExtent = kCellSize * (math.cmax(block.m_Size) + 1) * 0.71f;
                    if (math.distance(block.m_Position.xz, center) > radius + blockExtent)
                    {
                        continue;
                    }

                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                    bool blockChanged = false;
                    for (int cellZ = 0; cellZ < block.m_Size.y; cellZ++)
                    {
                        for (int cellX = 0; cellX < block.m_Size.x; cellX++)
                        {
                            int index = cellZ * block.m_Size.x + cellX;
                            if (index >= cells.Length)
                            {
                                continue;
                            }
                            Cell cell = cells[index];
                            if ((cell.m_State & CellFlags.Visible) == 0
                                || (cell.m_State & (CellFlags.Blocked | CellFlags.Overridden)) != 0)
                            {
                                continue;
                            }
                            if (cell.m_Zone.Equals(targetZone))
                            {
                                continue;
                            }
                            float3 cellPosition = ZoneUtils.GetCellPosition(block, new int2(cellX, cellZ));
                            if (math.distance(cellPosition.xz, center) > radius)
                            {
                                continue;
                            }
                            cell.m_Zone = targetZone;
                            cells[index] = cell;
                            cellsChanged++;
                            blockChanged = true;
                        }
                    }

                    if (blockChanged)
                    {
                        blocksTouched++;
                        if (!EntityManager.HasComponent<Game.Common.Updated>(blockEntity))
                        {
                            EntityManager.AddComponent<Game.Common.Updated>(blockEntity);
                        }
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                zone = resolvedName,
                center = new { x, z },
                radius,
                cellsChanged,
                blocksTouched,
                note = cellsChanged == 0
                    ? "no zonable cells found in radius - zone cells only exist along roads (build a road first) and must be unoccupied"
                    : "zoned; buildings will grow while the simulation runs if demand exists",
            });
        }
    }
}
