using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using SVP.Plugins.PeopleData.Helpers;

namespace SVP.Plugins.PeopleData
{
    public class OrganisationHierarchyapi : PluginBase
    {
        private const int MaxDepth = 10;

        public OrganisationHierarchyapi() : base(typeof(OrganisationHierarchyapi)) { }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localPluginContext)
        {
            var context = localPluginContext.PluginExecutionContext;

            var targetRef = new EntityReference("account", context.PrimaryEntityId);

            if (targetRef.LogicalName != "account")
                return;

            var service = localPluginContext.CurrentUserService;
            var tracing = localPluginContext.TracingService;

            // Retrieve the current account record.
            var account = service.Retrieve("account", targetRef.Id, new ColumnSet("name", "parentaccountid"));

            BuildAccountHierarchy(account, service, tracing, context);

            
        }

        private void BuildAccountHierarchy(Entity currentAccount, IOrganizationService service, ITracingService tracing, IPluginExecutionContext context)
        {
            var hierarchyBuilder = new StringBuilder();
            var labelledBuilder = new StringBuilder();

            var visited = new HashSet<Guid>();

            string currentName = currentAccount.GetAttributeValue<string>("name") ?? "No Name";

            tracing.Trace($"[Hierarchy] Adding current account: {currentAccount.Id} - {currentName}");


            hierarchyBuilder.Insert(0, currentName);
            labelledBuilder.Insert(0, $"{currentName}(({currentAccount.Id}))");

            visited.Add(currentAccount.Id);

            EntityReference parentRef = currentAccount.GetAttributeValue<EntityReference>("parentaccountid");

            tracing.Trace($"[Hierarchy] Adding current parentref: {parentRef}");
            int depth = 0;

            while (parentRef != null && depth < MaxDepth)
            {
                tracing.Trace($"Retrieving parent account at depth {depth} with ID: {parentRef.Id}");
                Entity parent = service.Retrieve("account", parentRef.Id, new ColumnSet("name", "parentaccountid"));
                string parentName = parent.GetAttributeValue<string>("name") ?? "No Name";
                tracing.Trace($"Retrieving parent account name at depth {depth} with Name: {parentName}");

                if (visited.Contains(parent.Id))
                {
                    tracing.Trace($"Circular reference detected at account ID: {parent.Id}");
                    hierarchyBuilder.Insert(0, "[CYCLE] ~> ");
                    labelledBuilder.Insert(0, "[CYCLE]~>");
                    break;
                }

                visited.Add(parent.Id); // ✅ Add after checking

                hierarchyBuilder.Insert(0, parentName + " ~> ");
                labelledBuilder.Insert(0, $"{parentName}(({parent.Id}))~>");

                parentRef = parent.GetAttributeValue<EntityReference>("parentaccountid"); 
                depth++;
            }

            if (depth >= MaxDepth)
            {
                hierarchyBuilder.Insert(0, "... ~> ");
                labelledBuilder.Insert(0, "... ~> ");
                tracing.Trace("Max depth reached. Truncating hierarchy.");
            }

            tracing.Trace($"[Hierarchy] Full Labelled Hierarchy: {labelledBuilder}");

            // Return the result to the Custom API response
            context.OutputParameters["HierarchyName"] = hierarchyBuilder.ToString();
            context.OutputParameters["LabelledHierarchy"] = labelledBuilder.ToString();

        }
    
    
    }
}

