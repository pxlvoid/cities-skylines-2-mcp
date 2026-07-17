using System;
using System.Collections.Generic;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Economy endpoints: budget breakdown (read), tax rates (read/write),
    /// per-service budget sliders (read/write). Write paths call the same
    /// game APIs the vanilla UI triggers use.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_ServicePrefabQuery;
        private bool m_ServicePrefabQueryCreated;

        private static readonly TaxAreaType[] kTaxAreas =
        {
            TaxAreaType.Residential,
            TaxAreaType.Commercial,
            TaxAreaType.Industrial,
            TaxAreaType.Office,
        };

        private EntityQuery ServicePrefabQuery
        {
            get
            {
                if (!m_ServicePrefabQueryCreated)
                {
                    m_ServicePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ServiceData>(),
                        ComponentType.ReadOnly<UIObjectData>());
                    m_ServicePrefabQueryCreated = true;
                }
                return m_ServicePrefabQuery;
            }
        }

        private BridgeResponse GetBudget()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            CityServiceBudgetSystem budget = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();

            var income = new Dictionary<string, int>();
            for (int i = 0; i < (int)IncomeSource.Count; i++)
            {
                income[((IncomeSource)i).ToString()] = budget.GetIncome((IncomeSource)i);
            }
            var expenses = new Dictionary<string, int>();
            for (int i = 0; i < (int)ExpenseSource.Count; i++)
            {
                expenses[((ExpenseSource)i).ToString()] = budget.GetExpense((ExpenseSource)i);
            }

            return BridgeResponse.Json(new
            {
                note = "hourly-updated monthly rates; expense values are positive costs",
                totalIncome = budget.GetTotalIncome(),
                totalExpenses = budget.GetTotalExpenses(),
                totalTaxIncome = budget.GetTotalTaxIncome(),
                balance = budget.GetBalance(),
                moneyDelta = budget.GetMoneyDelta(),
                income,
                expenses,
            });
        }

        private BridgeResponse GetTaxes()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            TaxSystem tax = World.GetOrCreateSystemManaged<TaxSystem>();
            int2 limits = tax.GetTaxParameterData().m_TotalTaxLimits;
            var areas = new Dictionary<string, object>();
            foreach (TaxAreaType area in kTaxAreas)
            {
                // Span of effective rates across job levels / resources — NOT the settable range.
                int2 span = tax.GetTaxRateRange(area);
                areas[area.ToString()] = new
                {
                    rate = tax.GetTaxRate(area),
                    effectiveRateSpan = new { min = span.x, max = span.y },
                };
            }
            return BridgeResponse.Json(new
            {
                allowedRange = new { min = limits.x, max = limits.y },
                stalenessWarning = LockStalenessWarning,
                taxRates = areas,
            });
        }

        private BridgeResponse HandleSetTax(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("area", out string areaName)
                || !Enum.TryParse(areaName, ignoreCase: true, out TaxAreaType area)
                || area == TaxAreaType.None)
            {
                return BridgeResponse.Error(400, "provide ?area=Residential|Commercial|Industrial|Office");
            }
            if (!request.TryGetInt("rate", out int rate))
            {
                return BridgeResponse.Error(400, "provide ?rate=<integer percent>");
            }

            TaxSystem tax = World.GetOrCreateSystemManaged<TaxSystem>();
            int2 limits = tax.GetTaxParameterData().m_TotalTaxLimits;
            int applied = math.clamp(rate, limits.x, limits.y);
            tax.SetTaxRate(area, applied);

            return BridgeResponse.Json(new
            {
                area = area.ToString(),
                requestedRate = rate,
                appliedRate = applied,
                newRate = tax.GetTaxRate(area),
                allowedRange = new { min = limits.x, max = limits.y },
            });
        }

        private BridgeResponse GetServiceBudgets()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            CityServiceBudgetSystem budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            var services = new List<object>();
            using (NativeArray<Entity> entities = ServicePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    ServicePrefab prefab = prefabSystem.GetPrefab<ServicePrefab>(entity);
                    if (prefab == null)
                    {
                        continue;
                    }
                    int budget = budgetSystem.GetServiceBudget(entity);
                    budgetSystem.GetEstimatedServiceBudget(entity, out int upkeep);
                    services.Add(new
                    {
                        name = prefab.name,
                        budgetPercent = budget,
                        efficiencyPercent = budgetSystem.GetServiceEfficiency(entity, budget),
                        estimatedUpkeep = upkeep,
                        buildings = budgetSystem.GetNumberOfServiceBuildings(entity),
                    });
                }
            }

            return BridgeResponse.Json(new
            {
                note = "budgetPercent range 50-150, 100 = default; lower budget saves money but reduces efficiency",
                services,
            });
        }

        private BridgeResponse HandleSetServiceBudget(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.Query.TryGetValue("service", out string serviceName) || string.IsNullOrEmpty(serviceName))
            {
                return BridgeResponse.Error(400, "provide ?service=<name from /city/service-budgets>");
            }
            if (!request.TryGetInt("percentage", out int percentage))
            {
                return BridgeResponse.Error(400, "provide ?percentage=<50-150>");
            }

            CityServiceBudgetSystem budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            using (NativeArray<Entity> entities = ServicePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    ServicePrefab prefab = prefabSystem.GetPrefab<ServicePrefab>(entity);
                    if (prefab == null || !string.Equals(prefab.name, serviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    int applied = math.clamp(percentage, 50, 150);
                    budgetSystem.SetServiceBudget(entity, applied);
                    return BridgeResponse.Json(new
                    {
                        service = prefab.name,
                        requestedPercent = percentage,
                        appliedPercent = applied,
                        efficiencyPercent = budgetSystem.GetServiceEfficiency(entity, applied),
                    });
                }
            }

            return BridgeResponse.Error(404, $"unknown service '{serviceName}'; list names via /city/service-budgets");
        }
    }
}
