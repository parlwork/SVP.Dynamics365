using Microsoft.Xrm.Sdk;
using SVP.Plugins.TrustId.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SVP.Plugins.TrustId
{
    public class ExpireGuestLinkplg : PluginBase
    {
        public ExpireGuestLinkplg(string unsecure, string secure) : base(typeof(ExpireGuestLinkplg))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localPluginContext)
        {
            var context = localPluginContext.PluginExecutionContext;
            var service = localPluginContext.CurrentUserService;
            var tracing = localPluginContext.TracingService;

            if (context.PrimaryEntityName != "parl_bpsscheck" || context.PrimaryEntityId == Guid.Empty)
                return;

            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
            {
                tracing.Trace("No Target entity found — exiting.");
                return;
            }

            if (!(target.Contains("parl_trustidguestlinkexpiry")
                  && target.GetAttributeValue<bool>("parl_trustidguestlinkexpiry")))
            {
                tracing.Trace("Guest link expiry not requested — exiting.");
                return;
            }

            var bpssRecord = service.Retrieve(
                "parl_bpsscheck",
                context.PrimaryEntityId,
                new Microsoft.Xrm.Sdk.Query.ColumnSet(
                    "parl_trustidauditlog",
                    "parl_trustidcontainerid",
                    "parl_trustidstatus",
                    "parl_trustiddocumentstatus"
                )
            );

            try
            {
                if (!IsDeleteAllowed(bpssRecord, tracing))
                    return;

                var existingContainerId = bpssRecord.GetAttributeValue<string>("parl_trustidcontainerid");

                // Parse env config
                var request = new OrganizationRequest("RetrieveEnvironmentVariableValue");
                request["DefinitionSchemaName"] = "parl_trustidconfig";
                var response = service.Execute(request);
                var json = response["Value"] as string;
                var config = JsonDocument.Parse(json).RootElement;
                tracing.Trace("Using ev config for expiry delete");

                var apiKey = config.GetProperty("apikey").GetString();
                var username = config.GetProperty("username").GetString();
                var password = config.GetProperty("password").GetString();
                var baseUrl = config.GetProperty("baseurl").GetString();

                var deviceId = Guid.NewGuid().ToString();
                tracing.Trace($"Device ID: {deviceId}");

                // --- 1) Login to TrustID ---
                var loginUrl = $"{baseUrl}/VPE/session/login";
                var loginBody = JsonSerializer.Serialize(new
                {
                    DeviceId = deviceId,
                    Username = username,
                    Password = password
                });

                var http = new HttpClient();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                http.DefaultRequestHeaders.Add("Tid-Api-Key", apiKey);

                tracing.Trace("Calling TrustID login…");
                var loginResp = http.PostAsync(loginUrl, new StringContent(loginBody, Encoding.UTF8, "application/json"))
                                    .GetAwaiter().GetResult();
                var loginJson = loginResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                loginResp.EnsureSuccessStatusCode();

                var loginDoc = JsonDocument.Parse(loginJson);
                if (!loginDoc.RootElement.TryGetProperty("SessionId", out var sidEl))
                    throw new InvalidPluginExecutionException("TrustID login: SessionId not found.");

                var sessionId = sidEl.GetString();
                tracing.Trace($"SessionId: {sessionId}");

                // --- 2) Delete existing guest link ---
                tracing.Trace($"Deleting guest link with ContainerId: {existingContainerId}");
                var deleteUrl = $"{baseUrl}/VPE/guestLink/deleteGuestLink";
                var deleteBody = JsonSerializer.Serialize(new
                {
                    SessionId = sessionId,
                    DeviceId = deviceId,
                    GuestId = existingContainerId
                });

                var deleteResp = http.PostAsync(deleteUrl, new StringContent(deleteBody, Encoding.UTF8, "application/json"))
                                     .GetAwaiter().GetResult();
                var deleteJson = deleteResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                tracing.Trace($"Delete response: {deleteJson}");
                deleteResp.EnsureSuccessStatusCode();
                tracing.Trace("Guest link deleted successfully.");

                // --- 2a) Parse  delete guest link response ---

                string delguestLinkMsg = null;
                bool delisSuccess = false;

                using (var doc = JsonDocument.Parse(deleteJson))
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Message", out var msgEl)) delguestLinkMsg = msgEl.GetString();
                    if (root.TryGetProperty("Success", out var successEl)) delisSuccess = successEl.GetBoolean();
                }

                tracing.Trace($"Deleting Guest link message from TrustID: {delguestLinkMsg}");

                // --- 3) Update record ---
                var update = new Entity("parl_bpsscheck") { Id = context.PrimaryEntityId };

                update["parl_trustidguestlinkexpirydate"] = DateTime.UtcNow;

                var user = service.Retrieve("systemuser", context.InitiatingUserId,
                    new Microsoft.Xrm.Sdk.Query.ColumnSet("fullname"));
                var userName = user.GetAttributeValue<string>("fullname");
                update["parl_trustidguestlinkedrequestedby"] = userName;
                tracing.Trace($"Expired by user: {userName}");

                update["parl_trustidcontainerid"] = null;

                if (delisSuccess)
                    update["parl_trustidstatus"] = new OptionSetValue(802390006); // Expired

                if (!string.IsNullOrWhiteSpace(delguestLinkMsg))
                {
                    update["parl_trustidlastmessagedescription"] = delguestLinkMsg;
                    update["parl_trustidlastmessagedate"] = DateTime.UtcNow;
                }

                // --- 4) Audit log ---
                var existingLog = bpssRecord.GetAttributeValue<string>("parl_trustidauditlog") ?? string.Empty;

                var logAction = delisSuccess
                    ? $"Guest link link expired. Deleted container: {existingContainerId}"
                    : $"Guest link expiry failed: {delguestLinkMsg}";


                var newLogEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} | {userName} | {logAction}";



                update["parl_trustidauditlog"] = string.IsNullOrWhiteSpace(existingLog)
                    ? newLogEntry
                    : existingLog + Environment.NewLine + newLogEntry;

                service.Update(update);
                tracing.Trace("Record updated — expiry.");

            }
            catch (Exception ex)
            {
                tracing.Trace("Error in ExpireGuestLinkplg: " + ex);
                throw;
            }
        }

        private static bool IsDeleteAllowed(Entity bpssRecord, ITracingService tracing)
        {
            var currentStatus = bpssRecord.GetAttributeValue<OptionSetValue>("parl_trustidstatus")?.Value;
            var documentStatus = bpssRecord.GetAttributeValue<OptionSetValue>("parl_trustiddocumentstatus")?.Value;
            var existingContainerId = bpssRecord.GetAttributeValue<string>("parl_trustidcontainerid");

            bool statusAllowed = currentStatus == 802390000    // Guest link sent
                                || currentStatus == 802390004  // Guest link auto resent
                                || currentStatus == 802390005; // Guest link resent

            bool containerPresent = !string.IsNullOrWhiteSpace(existingContainerId);

            bool documentBlocked = documentStatus == 802390000    // Documents ready to download
                                || documentStatus == 802390001;   // Documents uploaded to PSV

            tracing.Trace($"Delete conditions — Status: {currentStatus}, ContainerPresent: {containerPresent}, DocumentStatus: {documentStatus}");

            if (!statusAllowed) { tracing.Trace($"Status {currentStatus} does not allow expiry — exiting."); return false; }
            if (!containerPresent) { tracing.Trace("No existing container ID — exiting."); return false; }
            if (documentBlocked) { tracing.Trace($"Document status {documentStatus} blocks expiry — exiting."); return false; }

            return true;
        }
    }
}