using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using SVP.Plugins.PeopleData.Helpers;

namespace SVP.Plugins.PeopleData
{
    public class OrganisationHierarchyplg : PluginBase
    {
        public OrganisationHierarchyplg() : base(typeof(OrganisationHierarchyplg)) { }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localPluginContext)
        {
            var context = localPluginContext.PluginExecutionContext;
            var service = localPluginContext.CurrentUserService;
            var tracing = localPluginContext.TracingService;

            // Ensure this plugin runs on the Account entity
            if (context.PrimaryEntityName != "account" || context.PrimaryEntityId == Guid.Empty)
                return;

            try
            {
                tracing.Trace("Calling parl_AccountHierarchy Custom API...");

                // Prepare the Custom API request
                var accountRef = new EntityReference("account", context.PrimaryEntityId);

                var request = new OrganizationRequest("parl_AccountHierarchy")
                {
                    Parameters =
                    {
                        { "Target", accountRef } 
                    }
                };

                // Execute the Custom API
                var response = service.Execute(request);

                string hierarchy = null;
                string labelledHierarchy = null;

                // Extract the output
                if (response.Results.TryGetValue("HierarchyName", out var hierarchyObj) && hierarchyObj is string h)
                {
                    hierarchy = h;
                    tracing.Trace($"Received HierarchyName: {hierarchy}");

                }


                if (response.Results.TryGetValue("LabelledHierarchy", out var labelledObj) && labelledObj is string lh)
                {
                    labelledHierarchy = lh;
                    tracing.Trace($"Received LabelledHierarchy: {labelledHierarchy}");
                }

                // Update the account with the new hierarchy string
                var updateEntity = new Entity("account") { Id = context.PrimaryEntityId };

                if (!string.IsNullOrEmpty(hierarchy))
                {
                    updateEntity["parl_organisationhierarchy"] = hierarchy;
                }

                if (!string.IsNullOrEmpty(labelledHierarchy))
                {
                    updateEntity["parl_labelledhierarchy"] = labelledHierarchy;
                    updateEntity["parl_orghierarchyupdatedon"] = DateTime.UtcNow;
                }

                service.Update(updateEntity);

                tracing.Trace("Account updated successfully with hierarchy name.");
                
            }
            catch (Exception ex)
            {
                tracing.Trace("Error in CallHierarchyApiPlugin: " + ex.ToString());
                throw;
            }
        }
    }
}