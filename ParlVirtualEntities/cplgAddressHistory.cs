using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;
using Portals.Plugins.Helpers;

namespace AddressHistoryPlugin
{
    public class AddressHistoryRetrieveMultiple : PluginBase
    {
        private const int ADDRESS_IS_CURRENT = 802390000;
        private const int ADDRESS_IS_PRIMARY = 802390000;
        private const int ADDRESS_IS_NOT_CURRENT = 802390001;

        public AddressHistoryRetrieveMultiple(string unsecureConfig, string secureConfig)
            : base(typeof(AddressHistoryRetrieveMultiple))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localContext)
        {
            if (localContext == null)
                throw new ArgumentNullException(nameof(localContext));

            var context = localContext.PluginExecutionContext;
            var tracing = localContext.TracingService;
            var service = localContext.CurrentUserService;

            try
            {
                tracing.Trace("[Start] AddressHistoryRetrieveMultiple triggered.");

                tracing.Trace("Executing UserId: " + context.UserId);

                tracing.Trace($"[Context] Message: {context.MessageName}");
                tracing.Trace($"[Context] InputParameters Keys: {string.Join(", ", context.InputParameters.Keys)}");
                if (context.InputParameters.ContainsKey("Query"))
                {
                    var query = context.InputParameters["Query"];
                    tracing.Trace($"[Context] Query Type: {query.GetType().Name}");
                }

                QueryParams queryParams = GetQueryParamsFromRetrieveMultiple(context, service, tracing);
                tracing.Trace($"[Params] PersonType={queryParams.PersonType}, SFId={queryParams.SecurityFormId}");

                QueryExpression qe = new QueryExpression("parl_personaddress")
                {
                    ColumnSet = new ColumnSet(
                        "parl_datemovedinyear",
                        "parl_datemovedinmonth",
                        "parl_datemovedoutyear",
                        "parl_datemovedoutmonth",
                        "parl_iscurrent",
                        "parl_isprimaryaddress",
                        "parl_name",
                        "parl_securityformid",
                        "parl_persontype"
                    )
                };

                if (!string.IsNullOrEmpty(queryParams.PersonType))
                {
                    qe.Criteria.AddCondition("parl_persontype", ConditionOperator.Equal, queryParams.PersonType);
                    tracing.Trace($"[Filter] Added PersonType={queryParams.PersonType}");
                }

                if (!string.IsNullOrEmpty(queryParams.SecurityFormId))
                {
                    qe.Criteria.AddCondition("parl_securityformid", ConditionOperator.Equal, queryParams.SecurityFormId);
                    tracing.Trace($"[Filter] Added parl_securityformid={queryParams.SecurityFormId}");
                }

                qe.AddOrder("parl_datemovedinyear", OrderType.Descending);
                qe.AddOrder("parl_datemovedinmonth", OrderType.Descending);

                tracing.Trace("[Query] Executing RetrieveMultiple on parl_personaddress...");
                var records = service.RetrieveMultiple(qe);
                tracing.Trace($"[Query] Retrieved {records.Entities.Count} records.");

                var today = DateTime.UtcNow;
                int todayIndex = today.Year * 12 + today.Month;
                tracing.Trace($"[Today] TodayIndex={todayIndex}");

                var periods = new List<Tuple<int, int>>();
                bool hasCurrent = false;

                foreach (var entity in records.Entities)
                {
                    int moveInYear = entity.GetAttributeValue<int?>("parl_datemovedinyear") ?? 0;
                    int moveInMonth = entity.GetAttributeValue<int?>("parl_datemovedinmonth") ?? 1;
                    int? moveOutYear = entity.GetAttributeValue<int?>("parl_datemovedoutyear");
                    int? moveOutMonth = entity.GetAttributeValue<int?>("parl_datemovedoutmonth");
                    int? isCurrent = entity.GetAttributeValue<OptionSetValue>("parl_iscurrent")?.Value;
                    int? isPrimary = entity.GetAttributeValue<OptionSetValue>("parl_isprimaryaddress")?.Value;

                    int moveInIndex = moveInYear * 12 + moveInMonth;
                    int moveOutIndex = todayIndex;

                    if (isCurrent == ADDRESS_IS_CURRENT)
                    {
                        moveOutIndex = todayIndex;
                        hasCurrent = true;
                        tracing.Trace($"[Address] Current address → Using TodayIndex");
                    }
                    else if (isPrimary == ADDRESS_IS_PRIMARY && isCurrent != ADDRESS_IS_NOT_CURRENT)
                    {
                        moveOutIndex = todayIndex;
                        hasCurrent = true;
                        tracing.Trace($"[Address] Primary treated as current → Using TodayIndex");
                    }
                    else if (moveOutYear.HasValue && moveOutMonth.HasValue)
                    {
                        moveOutIndex = (moveOutYear.Value * 12) + moveOutMonth.Value;
                        tracing.Trace($"[Address] Using MoveOutIndex={moveOutIndex}");
                    }

                    periods.Add(Tuple.Create(moveInIndex, moveOutIndex));
                    tracing.Trace($"[Period Added] ({moveInIndex}, {moveOutIndex})");
                }

                var merged = MergePeriods(periods, tracing);

                int totalMonths = 0;
                bool hasGaps = false;
                int? prevEnd = null;

                foreach (var p in merged)
                {
                    int start = p.Item1;
                    int end = p.Item2;
                    int duration = end - start;
                    totalMonths += duration;

                    tracing.Trace($"[Calc] Interval=({start},{end}), Duration={duration}, Total={totalMonths}");

                    if (prevEnd.HasValue && start - prevEnd.Value > 1)
                    {
                        hasGaps = true;
                        tracing.Trace($"[Gap] Gap detected between {prevEnd} and {start}");
                    }

                    prevEnd = end;
                }

                EntityCollection results = new EntityCollection();
                Entity output = new Entity("parl_internaladdresscalculation");

                output["parl_internaladdresscalculationid"] = Guid.NewGuid();
                output["parl_sfid"] = queryParams.SecurityFormId;
                output["parl_persontype"] = queryParams.PersonType;
                output["parl_totalmonths"] = totalMonths;
                output["parl_hascurrent"] = new OptionSetValue(hasCurrent ? 802390000 : 802390001);
                output["parl_hasgaps"] = new OptionSetValue(hasGaps ? 802390000 : 802390001);

                results.Entities.Add(output);
                context.OutputParameters["BusinessEntityCollection"] = results;

                tracing.Trace($"[Result new] TotalMonths={totalMonths}, HasCurrent={hasCurrent}, HasGaps={hasGaps}, SFId={queryParams.SecurityFormId}, PersonType={queryParams.PersonType}");
            }
            catch (Exception ex)
            {
                tracing.Trace($"[Error] {ex}");
                throw;
            }
        }

        private static List<Tuple<int, int>> MergePeriods(List<Tuple<int, int>> periods, ITracingService tracer)
        {
            var sorted = periods.OrderBy(p => p.Item1).ToList();
            var merged = new List<Tuple<int, int>>();

            int? prevStart = null;
            int? prevEnd = null;

            foreach (var p in sorted)
            {
                int start = p.Item1;
                int end = p.Item2;
                tracer.Trace($"[Merge] Processing ({start},{end})");

                if (!prevStart.HasValue)
                {
                    prevStart = start;
                    prevEnd = end;
                }
                else
                {
                    if (start <= prevEnd.Value)
                    {
                        if (end > prevEnd.Value) prevEnd = end;
                        tracer.Trace($"[Merge] Overlap/Touch → New prevEnd={prevEnd}");
                    }
                    else
                    {
                        merged.Add(Tuple.Create(prevStart.Value, prevEnd.Value));
                        tracer.Trace($"[Merge] Finalized interval ({prevStart},{prevEnd})");
                        prevStart = start;
                        prevEnd = end;
                    }
                }
            }

            if (prevStart.HasValue && prevEnd.HasValue)
            {
                merged.Add(Tuple.Create(prevStart.Value, prevEnd.Value));
                tracer.Trace($"[Merge] Added last interval ({prevStart},{prevEnd})");
            }

            return merged;
        }

        private static QueryParams GetQueryParamsFromRetrieveMultiple(IPluginExecutionContext context, IOrganizationService service, ITracingService tracer)
        {
            var queryParams = new QueryParams();

            if (context.InputParameters["Query"] is FetchExpression fetch)
            {
                tracer.Trace("[QueryParams] FetchExpression detected.");
                var fetchRequest = new FetchXmlToQueryExpressionRequest
                {
                    FetchXml = fetch.Query
                };
                var fetchResponse = (FetchXmlToQueryExpressionResponse)service.Execute(fetchRequest);
                var qe = fetchResponse.Query;

                foreach (var condition in qe.Criteria.Conditions)
                {
                    tracer.Trace($"[QueryParams] Found condition {condition.AttributeName}={condition.Values[0]}");
                    if (condition.AttributeName == "parl_persontype")
                        queryParams.PersonType = condition.Values[0].ToString();
                    if (condition.AttributeName == "parl_sfid")
                        queryParams.SecurityFormId = condition.Values[0].ToString();
                }
            }
            else if (context.InputParameters["Query"] is QueryExpression qe)
            {
                tracer.Trace("[QueryParams] QueryExpression detected.");
                foreach (var condition in qe.Criteria.Conditions)
                {
                    tracer.Trace($"[QueryParams] Found condition {condition.AttributeName}={condition.Values[0]}");
                    if (condition.AttributeName == "parl_persontype")
                        queryParams.PersonType = condition.Values[0].ToString();
                    if (condition.AttributeName == "parl_sfid")
                        queryParams.SecurityFormId = condition.Values[0].ToString();
                }
            }

            return queryParams;
        }
    
    }

    public class AddressHistoryRetrieve : PluginBase
    {
        public AddressHistoryRetrieve(string unsecureConfig, string secureConfig)
            : base(typeof(AddressHistoryRetrieve))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localContext) { }
    }

    public class AddressHistoryCreate : PluginBase
    {
        public AddressHistoryCreate(string unsecureConfig, string secureConfig)
            : base(typeof(AddressHistoryCreate))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localContext) { }
    }

    public class AddressHistoryUpdate : PluginBase
    {
        public AddressHistoryUpdate(string unsecureConfig, string secureConfig)
            : base(typeof(AddressHistoryUpdate))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localContext) { }
    }

    public class AddressHistoryDelete : PluginBase
    {
        public AddressHistoryDelete(string unsecureConfig, string secureConfig)
            : base(typeof(AddressHistoryDelete))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localContext) { }
    }

    public class QueryParams
    {
        public string PersonType { get; set; }
        public string SecurityFormId { get; set; }
    }
}