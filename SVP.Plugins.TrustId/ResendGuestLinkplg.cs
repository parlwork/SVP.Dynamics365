using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using SVP.Plugins.TrustId.Helpers;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SVP.Plugins.TrustId
{
    public class ResendGuestLinkplg : PluginBase
    {
        //private readonly string _secureConfig;
        public ResendGuestLinkplg(string unsecure, string secure) : base(typeof(ResendGuestLinkplg))
        {
            //_secureConfig = secure;
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

            bool isManual = target.Contains("parl_trustidguestlinkresend")
                && target.GetAttributeValue<bool>("parl_trustidguestlinkresend");

            tracing.Trace($"Manual:{ isManual}");

            bool isAuto = target.Contains("parl_trustidguestlinkautoresend")
                          && target.GetAttributeValue<bool>("parl_trustidguestlinkautoresend");

            tracing.Trace($"Auto:{isAuto}");

            if (!isManual && !isAuto)
            {
                tracing.Trace("No resend requested — exiting.");
                return;
            }

            //if (!(target.Contains("parl_trustidguestlinkresend")
            //      && target.GetAttributeValue<bool>("parl_trustidguestlinkresend")))
            //{
            //    tracing.Trace("Guest link - Manual not requested — exiting.");
            //    return;
            //}

            // Retrieve the record
            var bpssRecord = service.Retrieve(
                "parl_bpsscheck",
                context.PrimaryEntityId,
                new Microsoft.Xrm.Sdk.Query.ColumnSet(
                    "emailaddress",
                    "parl_firstname",
                    "parl_lastname",
                    "parl_trustidemailsubject",
                    "parl_trustidemailcontent",
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

                // Parse ev config
                var request = new OrganizationRequest("RetrieveEnvironmentVariableValue");
                request["DefinitionSchemaName"] = "parl_trustidconfig";
                var response = service.Execute(request);
                var json = response["Value"] as string;
                var config = JsonDocument.Parse(json).RootElement;
                tracing.Trace("using ev config for resend");

                var apiKey = config.GetProperty("apikey").GetString();
                var username = config.GetProperty("username").GetString();
                var password = config.GetProperty("password").GetString();
                var branchId = config.GetProperty("branchid").GetString();
                var callbackHeaderName = config.GetProperty("callbackheadername").GetString();
                var callbackHeaderValue = config.GetProperty("callbackheadervalue").GetString();
                var callbackBaseUrl = config.GetProperty("callbackbaseurl").GetString();
                var baseUrl = config.GetProperty("baseurl").GetString();
                var expiryDays = config.GetProperty("guestlinkexpirydays").GetInt32();

                var deviceId = Guid.NewGuid().ToString();
                tracing.Trace($"Device ID: {deviceId}");
                var clientRef = context.PrimaryEntityId.ToString();

                var email = bpssRecord.GetAttributeValue<string>("emailaddress");
                tracing.Trace($"email: {email}");
                var firstName = bpssRecord.GetAttributeValue<string>("parl_firstname");
                var lastName = bpssRecord.GetAttributeValue<string>("parl_lastname");
                var name = $"{firstName} {lastName}".Trim();
                tracing.Trace($"name: {name}");

                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
                {
                    tracing.Trace("Email or name missing — exiting.");
                    return;
                }

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
                tracing.Trace($"Deleting existing guest link with ContainerId: {existingContainerId}");

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
                tracing.Trace("Existing guest link deleted successfully.");

                // --- 3) Create guest link ---
                var emailSubject = bpssRecord.GetAttributeValue<string>("parl_trustidemailsubject");
                var emailContent = bpssRecord.GetAttributeValue<string>("parl_trustidemailcontent");
                var mdContent = $"Dear {firstName},\n\n {emailContent}\n\nIf you have any questions, please contact UK Parliament team.";

                var guestUrl = $"{baseUrl}/VPE/guestLink/createGuestLink";
                var guestBody = JsonSerializer.Serialize(new
                {
                    SessionId = sessionId,
                    DeviceId = deviceId,
                    Email = email,
                    Name = name,
                    BranchId = branchId,
                    EmailSubjectOverride = string.IsNullOrWhiteSpace(emailSubject) ? null : emailSubject,
                    EmailContentOverride = string.IsNullOrWhiteSpace(emailContent) ? null : mdContent,
                    ContainerEventCallbackHeaders = new[]
                    {
                        new { Header = callbackHeaderName, Value = callbackHeaderValue }
                    },
                    ClientApplicationReference = clientRef
                });

                tracing.Trace("Calling TrustID createGuestLink..");
                tracing.Trace($"GuestBody: {guestBody}");
                var guestResp = http.PostAsync(guestUrl, new StringContent(guestBody, Encoding.UTF8, "application/json"))
                                    .GetAwaiter().GetResult();
                var guestJson = guestResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                guestResp.EnsureSuccessStatusCode();

                // --- 4) Parse guest link response ---
                string guestLinkMsg = null;
                string containerId = null;
                bool isSuccess = false;

                using (var doc = JsonDocument.Parse(guestJson))
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Message", out var msgEl)) guestLinkMsg = msgEl.GetString();
                    if (root.TryGetProperty("Success", out var successEl)) isSuccess = successEl.GetBoolean();
                    if (root.TryGetProperty("ContainerId", out var cidEl)) containerId = cidEl.GetString();
                }

                tracing.Trace($"Guest link message from TrustID: {guestLinkMsg}");

                // --- 5) Update record ---
                var update = new Entity("parl_bpsscheck") { Id = context.PrimaryEntityId };

                update["parl_trustidemailtype"] = false;

                update["parl_trustidemailsubject"] = null;
                update["parl_trustidemailcontent"] = null;

                update["parl_trustidguestlinkrequesteddate"] = DateTime.UtcNow;
                update["parl_trustidguestlinkexpirydate"] = DateTime.UtcNow.AddDays(expiryDays);

                var user = service.Retrieve("systemuser", context.InitiatingUserId, new Microsoft.Xrm.Sdk.Query.ColumnSet("fullname"));
                var userName = user.GetAttributeValue<string>("fullname");
                update["parl_trustidguestlinkedrequestedby"] = userName;
                tracing.Trace($"Requested by user: {userName}");

                if (!string.IsNullOrWhiteSpace(containerId))
                    update["parl_trustidcontainerid"] = containerId;

                if (isSuccess)
                    update["parl_trustidstatus"] = new OptionSetValue(802390000); // Guest link sent

                if (!string.IsNullOrWhiteSpace(guestLinkMsg))
                {
                    update["parl_trustidlastmessagedescription"] = guestLinkMsg;
                    update["parl_trustidlastmessagedate"] = DateTime.UtcNow;
                }

                // --- 6) Build audit log entry ---
                var existingLog = bpssRecord.GetAttributeValue<string>("parl_trustidauditlog") ?? string.Empty;
                //var logAction = isSuccess
                //    ? $"Guest link resent successfully. Expired: {existingContainerId}"
                //    : $"Guest link resend failed: {guestLinkMsg}";

                var logAction = isSuccess
                     ? $"Guest link {(isAuto ? "auto-resent" : "resent")} successfully. Expired: {existingContainerId}"
                     : $"Guest link resend failed: {guestLinkMsg}";

                var newLogEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} | {userName} | {logAction}";

                update["parl_trustidauditlog"] = string.IsNullOrWhiteSpace(existingLog)
                    ? newLogEntry
                    : existingLog + Environment.NewLine + newLogEntry;

                service.Update(update);
            }
            catch (Exception ex)
            {
                tracing.Trace("Error in TrustIDResendGuestLinkplg: " + ex);
                throw;
            }
        }

        // ------------------------------------------------------------------ //
        //  Condition guard                                                     //
        // ------------------------------------------------------------------ //

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
                                    || documentStatus == 802390001; // Documents uploaded to PSV

            tracing.Trace($"Delete conditions — Status: {currentStatus}, ContainerPresent: {containerPresent}, DocumentStatus: {documentStatus}");

            if (!statusAllowed) { tracing.Trace($"Status {currentStatus} does not allow resend — exiting gracefully."); return false; }
            if (!containerPresent) { tracing.Trace("No existing container ID — exiting gracefully."); return false; }
            if (documentBlocked) { tracing.Trace($"Document status {documentStatus} blocks resend — exiting gracefully."); return false; }

            return true;
        }
    }
}