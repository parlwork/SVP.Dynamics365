using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using SVP.Plugins.TrustId.Helpers;

namespace SVP.Plugins.TrustId
{
    public class RetrieveUpdatesplg : PluginBase
    {
        //private readonly string _secureConfig;

        public RetrieveUpdatesplg(string unsecureConfig, string secureConfig)
            : base(typeof(RetrieveUpdatesplg))
        {
            //_secureConfig = secureConfig;
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localPluginContext)
        {
            var context = localPluginContext.PluginExecutionContext;
            var service = localPluginContext.CurrentUserService;
            var tracing = localPluginContext.TracingService;

            if (
                context.PrimaryEntityName != "parl_bpsscheck"
                || context.PrimaryEntityId == Guid.Empty
            )
                return;

            // Ensure Target entity exists and contains the trigger field
            if (
                !context.InputParameters.Contains("Target")
                || !(context.InputParameters["Target"] is Entity target)
            )
            {
                tracing.Trace("No Target entity found — exiting.");
                return;
            }

            if (!target.Contains("parl_trustiddocumentstatus"))
            {
                tracing.Trace("parl_trustiddocumentstatus not in update — exiting.");
                return;
            }

            // Check trigger value: Documents Ready to Download (802390000)
            var triggerStatus = target.GetAttributeValue<OptionSetValue>(
                "parl_trustiddocumentstatus"
            );
            if (triggerStatus == null || triggerStatus.Value != 802390000)
            {
                tracing.Trace(
                    $"parl_trustiddocumentstatus is not 'Documents Ready to Download' (current value: {triggerStatus?.Value.ToString() ?? "null"}) — skipping."
                );
                return;
            }

            // Parse ev config
            var request = new OrganizationRequest("RetrieveEnvironmentVariableValue");
            request["DefinitionSchemaName"] = "parl_trustidconfig";
            var response = service.Execute(request);
            var json = response["Value"] as string;

            var config = JsonDocument.Parse(json).RootElement;

            tracing.Trace("using ev config");

            var apiKey = config.GetProperty("apikey").GetString();
            var username = config.GetProperty("username").GetString();
            var password = config.GetProperty("password").GetString();
            var baseUrl = config.GetProperty("baseurl").GetString();

            var deviceId = Guid.NewGuid().ToString();

            tracing.Trace($"Device ID: {deviceId}");

            // Retrieve the BPSS check record
            var bpssRecord = service.Retrieve(
                "parl_bpsscheck",
                context.PrimaryEntityId,
                new Microsoft.Xrm.Sdk.Query.ColumnSet(
                    "parl_trustidcontainerid",
                    "parl_trustiddocumentstatus",
                    "parl_applicationresult",
                    "parl_trustidauditlog"
                )
            );

            var containerid = bpssRecord.GetAttributeValue<string>("parl_trustidcontainerid");
            tracing.Trace($"Container ID: {containerid}");

            if (string.IsNullOrWhiteSpace(containerid))
            {
                tracing.Trace("ContainerID missing — exiting.");
                return;
            }

            try
            {
                // --- 1) Login to TrustID ---
                var loginUrl = $"{baseUrl}/VPE/session/login";
                var loginBody = JsonSerializer.Serialize(
                    new
                    {
                        DeviceId = deviceId,
                        Username = username,
                        Password = password,
                    }
                );

                var http = new HttpClient();
                http.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
                http.DefaultRequestHeaders.Add("Tid-Api-Key", apiKey);

                tracing.Trace("Calling TrustID login…");
                var loginResp = http.PostAsync(
                        loginUrl,
                        new StringContent(loginBody, Encoding.UTF8, "application/json")
                    )
                    .GetAwaiter()
                    .GetResult();
                var loginJson = loginResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                loginResp.EnsureSuccessStatusCode();

                var loginDoc = JsonDocument.Parse(loginJson);
                if (!loginDoc.RootElement.TryGetProperty("SessionId", out var sidEl))
                    throw new InvalidPluginExecutionException(
                        "TrustID login: SessionId not found."
                    );
                var sessionId = sidEl.GetString();

                // --- 2) Retrieve Document Container JSON ---
                var retrieveDocUrl = $"{baseUrl}/VPE/dataAccess/retrieveDocumentContainer/";
                var retrieveDocBody = JsonSerializer.Serialize(
                    new
                    {
                        DeviceId = deviceId,
                        SessionId = sessionId,
                        ContainerId = containerid,
                    }
                );

                var pivotOption = bpssRecord.GetAttributeValue<OptionSetValue>(
                    "parl_applicationresult"
                );
                var pivot = pivotOption?.Value ?? -1;

                tracing.Trace($"Application Result pivot value: {pivot}");

                string docUrl;
                string docBody;

                switch (pivot)
                {
                    case 802390001: // Test Dataverse
                        docUrl = "https://svp-dev.powerappsportals.com/mock-json/";
                        docBody = "{}";
                        tracing.Trace("Using mock URL (Test Dataverse).");
                        break;

                    case 802390002: // Test Postman
                        docUrl =
                            "https://287112a9-60f8-4fe9-bebb-a49a5655bf72.mock.pstmn.io/VPE/dataAccess/retrieveDocumentContainer/";
                        docBody = "{}";
                        tracing.Trace("Using mock URL (Test Postman).");
                        break;

                    default: // Trust ID (802390000) or any other value
                        docUrl = retrieveDocUrl;
                        docBody = retrieveDocBody;
                        tracing.Trace($"Using live TrustID URL (pivot: {pivot}).");
                        break;
                }

                var docResp = http.PostAsync(
                        docUrl,
                        new StringContent(docBody, Encoding.UTF8, "application/json")
                    )
                    .GetAwaiter()
                    .GetResult();

                docResp.EnsureSuccessStatusCode();

                var jsonContent = docResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                tracing.Trace(
                    $"Raw JSON (first 500 chars): {jsonContent.Substring(0, Math.Min(500, jsonContent.Length))}"
                );

                // 2a Convert Microsoft date format to readable ISO format
                var readableJson = ConvertMicrosoftDatesToReadable(jsonContent);

                // 2b Pretty-print the JSON
                var jsonObject = JsonSerializer.Deserialize<JsonElement>(readableJson);
                var prettyJson = JsonSerializer.Serialize(
                    jsonObject,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                var jsonBytes = Encoding.UTF8.GetBytes(prettyJson);

                tracing.Trace($"Document container JSON retrieved, size: {jsonBytes.Length} bytes");

                // 2c Upload JSON to parl_trustidcontainerinfo
                var jsonFileName = $"Report_{containerid}_{DateTime.UtcNow:yyyyMMddHHmmss}.json";

                var initJsonRequest = new InitializeFileBlocksUploadRequest
                {
                    Target = new EntityReference("parl_bpsscheck", context.PrimaryEntityId),
                    FileAttributeName = "parl_trustidresultsinfo",
                    FileName = jsonFileName,
                };

                var initJsonResponse = (InitializeFileBlocksUploadResponse)
                    service.Execute(initJsonRequest);
                var jsonContinuationToken = initJsonResponse.FileContinuationToken;

                tracing.Trace("JSON file upload initialized");

                var jsonBlockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(0.ToString("d6")));

                var uploadJsonRequest = new UploadBlockRequest
                {
                    BlockData = jsonBytes,
                    BlockId = jsonBlockId,
                    FileContinuationToken = jsonContinuationToken,
                };

                service.Execute(uploadJsonRequest);
                tracing.Trace("JSON file block uploaded");

                var commitJsonRequest = new CommitFileBlocksUploadRequest
                {
                    BlockList = new[] { jsonBlockId },
                    FileContinuationToken = jsonContinuationToken,
                    FileName = jsonFileName,
                    MimeType = "application/json",
                };

                service.Execute(commitJsonRequest);
                tracing.Trace(
                    $"Document container JSON uploaded to parl_trustidresultsinfo: {jsonFileName}"
                );

                // --- 3) Export PDF Report and attach as Note ---
                var exportPdfUrl = $"{baseUrl}/VPE/dataAccess/exportPDF/";
                var exportPdfBody = JsonSerializer.Serialize(
                    new
                    {
                        DeviceId = deviceId,
                        SessionId = sessionId,
                        ContainerId = containerid,
                    }
                );

                tracing.Trace("Calling TrustID exportPDF…");

                var pdfRequest = new HttpRequestMessage(HttpMethod.Post, exportPdfUrl)
                {
                    Content = new StringContent(exportPdfBody, Encoding.UTF8, "application/json"),
                };
                pdfRequest.Headers.Accept.Clear();
                pdfRequest.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/pdf")
                );

                var pdfResp = http.SendAsync(pdfRequest).GetAwaiter().GetResult();
                pdfResp.EnsureSuccessStatusCode();

                var pdfBytes = pdfResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var pdfFileName = $"Rep_{containerid}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
                tracing.Trace($"PDF downloaded successfully, size: {pdfBytes.Length} bytes");

                // Create annotation (note) with PDF attachment
                var note = new Entity("annotation");
                note["objectid"] = new EntityReference("parl_bpsscheck", context.PrimaryEntityId);
                note["objecttypecode"] = "parl_bpsscheck";
                note["subject"] = $"TrustID Report - {containerid}";
                note["notetext"] =
                    $"#report downloaded on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
                note["filename"] = pdfFileName;
                note["mimetype"] = "application/pdf";
                note["documentbody"] = Convert.ToBase64String(pdfBytes);
                note["isdocument"] = true;

                service.Create(note);
                tracing.Trace($"PDF attached as note: {pdfFileName}");

                // --- 4) Download Applicant Selfie and attach as Note ---
                try
                {
                    var containerData = JsonSerializer.Deserialize<JsonElement>(jsonContent);

                    if (
                        containerData.TryGetProperty("Container", out var containerElement)
                        && containerElement.TryGetProperty(
                            "ApplicantPhotoImage",
                            out var applicantPhoto
                        )
                        && applicantPhoto.TryGetProperty("Id", out var photoIdElement)
                    )
                    {
                        var photoId = photoIdElement.GetString();
                        tracing.Trace($"Found ApplicantPhotoImage ID: {photoId}");

                        var imageUrl =
                            $"{baseUrl}/VPE/dataAccess/image/?id={photoId}&DeviceId={deviceId}&SessionId={sessionId}";

                        tracing.Trace("Calling TrustID getImage for applicant photo…");
                        var imageResp = http.GetAsync(imageUrl).GetAwaiter().GetResult();
                        imageResp.EnsureSuccessStatusCode();

                        var imageBytes = imageResp
                            .Content.ReadAsByteArrayAsync()
                            .GetAwaiter()
                            .GetResult();
                        var contentType =
                            imageResp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

                        string fileExtension;
                        if (contentType == "image/png")
                            fileExtension = "png";
                        else if (contentType == "image/gif")
                            fileExtension = "gif";
                        else if (contentType == "image/webp")
                            fileExtension = "webp";
                        else
                            fileExtension = "jpg";

                        var selfieFileName =
                            $"Selfie_{containerid}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.{fileExtension}";
                        tracing.Trace(
                            $"Selfie downloaded successfully, size: {imageBytes.Length} bytes"
                        );

                        var selfieNote = new Entity("annotation");
                        selfieNote["objectid"] = new EntityReference(
                            "parl_bpsscheck",
                            context.PrimaryEntityId
                        );
                        selfieNote["objecttypecode"] = "parl_bpsscheck";
                        selfieNote["subject"] = $"TrustID Applicant Selfie - {containerid}";
                        selfieNote["notetext"] =
                            $"#selfie downloaded on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
                        selfieNote["filename"] = selfieFileName;
                        selfieNote["mimetype"] = contentType;
                        selfieNote["documentbody"] = Convert.ToBase64String(imageBytes);
                        selfieNote["isdocument"] = true;

                        service.Create(selfieNote);
                        tracing.Trace($"Selfie attached as note: {selfieFileName}");
                    }
                    else
                    {
                        tracing.Trace(
                            "ApplicantPhotoImage.Id not found in container data - skipping selfie upload"
                        );
                    }
                }
                catch (Exception ex)
                {
                    tracing.Trace($"Error retrieving or uploading applicant photo: {ex.Message}");
                    // Don't throw - allow the rest of the plugin to continue
                }

                // --- 5) Update status and timestamp ---
                var update = new Entity("parl_bpsscheck") { Id = context.PrimaryEntityId };

                //Status and Document Status
                update["parl_trustiddocumentstatus"] = new OptionSetValue(802390001); // Documents Uploaded to PSV
                update["parl_trustidstatus"] = new OptionSetValue(802390002);

                // Extract summary fields from container JSON
                try
                {
                    var doc = JsonDocument.Parse(readableJson);
                    var root = doc.RootElement;
                    var containerEl = root.GetProperty("Container");

                    // Document field variables
                    string firstName = null,
                        middleName = null,
                        surname = null;
                    string dob = null,
                        expiryDate = null,
                        nationality = null,
                        docType = null;
                    bool documentRead = false;


                    if (
                                            containerEl.TryGetProperty("Documents", out var documents)
                                            && documents.GetArrayLength() > 0
                                        )
                    {
                        var firstDoc = documents[0];

                        documentRead = firstDoc.TryGetProperty("SuccessfullyRead", out var srEl)
                            && srEl.ValueKind == JsonValueKind.True;

                        if (documentRead)
                        {
                            if (
                                firstDoc.TryGetProperty("Nationality", out var natEl)
                                && natEl.ValueKind == JsonValueKind.Object
                            )
                                nationality = natEl.GetProperty("Name").GetString();

                            if (
                                firstDoc.TryGetProperty("DocumentVersion", out var docVerEl)
                                && docVerEl.ValueKind == JsonValueKind.Object
                            )
                                docType = docVerEl.GetProperty("Name").GetString();

                            if (firstDoc.TryGetProperty("DocumentFields", out var fields))
                            {
                                foreach (var field in fields.EnumerateArray())
                                {
                                    var fieldName = field.GetProperty("Name").GetString();
                                    switch (fieldName)
                                    {
                                        case "VI Firstname":
                                            firstName = field
                                                .GetProperty("FieldValueString")
                                                .GetString();
                                            break;
                                        case "VI Middlename":
                                            middleName = field
                                                .GetProperty("FieldValueString")
                                                .GetString();
                                            break;
                                        case "VI Surname":
                                            surname = field.GetProperty("FieldValueString").GetString();
                                            break;
                                        case "VI Birth Date":
                                            var dobRaw = field
                                                .GetProperty("FieldValueDate")
                                                .GetString();
                                            if (DateTime.TryParse(dobRaw, out var dobParsed))
                                                dob = dobParsed.ToString("dd/MM/yyyy");
                                            break;
                                        case "VI Expiration Date":
                                            var expRaw = field
                                                .GetProperty("FieldValueDate")
                                                .GetString();
                                            if (DateTime.TryParse(expRaw, out var expParsed))
                                                expiryDate = expParsed.ToString("dd/MM/yyyy");
                                            break;
                                    }
                                }
                            }
                        }
                    }

                    // Address variables
                    string address1 = null,
                        address2 = null,
                        address3 = null;
                    string address4 = null,
                        address5 = null,
                        address6 = null;
                    string postcode = null,
                        countryCode = null;

                    if (
                        containerEl.TryGetProperty(
                            "DocumentContainerFieldList",
                            out var containerFields
                        )
                    )
                    {
                        foreach (var field in containerFields.EnumerateArray())
                        {
                            var fieldName = field.GetProperty("Name").GetString();
                            var fieldValue = field.TryGetProperty("FieldValueString", out var fv)
                                ? fv.GetString()
                                : null;

                            switch (fieldName)
                            {
                                case "Address1":
                                    address1 = fieldValue;
                                    break;
                                case "Address2":
                                    address2 = fieldValue;
                                    break;
                                case "Address3":
                                    address3 = fieldValue;
                                    break;
                                case "Address4":
                                    address4 = fieldValue;
                                    break;
                                case "Address5":
                                    address5 = fieldValue;
                                    break;
                                case "Address6":
                                    address6 = fieldValue;
                                    break;
                                case "Address_Postcode":
                                    postcode = fieldValue;
                                    break;
                                case "Address_CountryCode":
                                    countryCode = fieldValue;
                                    break;
                            }
                        }
                    }

                    var addressParts = new[]
                    {
                        address1,
                        address2,
                        address3,
                        address4,
                        address5,
                        address6,
                        postcode,
                        countryCode,
                    }.Where(p => !string.IsNullOrWhiteSpace(p));
                    var fullAddress = string.Join(", ", addressParts);

                    // Get validation list once for all checks
                    JsonElement validationList = default;
                    var hasValidationList = containerEl.TryGetProperty(
                        "DocumentContainerValidationList",
                        out validationList
                    );

                    // --- Step 1: Detect RTW method --- Share code update
                    bool isShareCode = false;
                    if (containerEl.TryGetProperty("Documents", out var docsForRtwCheck))
                    {
                        foreach (var d in docsForRtwCheck.EnumerateArray())
                        {
                            if (
                                d.TryGetProperty("DocumentType", out var dtEl)
                                && dtEl.TryGetInt32(out var type)
                                && type == 6
                            )
                            {
                                isShareCode = true;
                                break;
                            }
                        }
                    }
                    var rtwMethod = isShareCode ? "Share Code" : "Digital Identity";
                    tracing.Trace($"RTW Method: {rtwMethod}");

                    // --- Step 2: Extract RTW flexible fields (both paths) ---
                    string rtwFlexStatus = null,
                        rtwWorkRestrictions = null;
                    string rtwFollowUpDate = null,
                        rtwNotes = null;

                    if (
                        containerEl.TryGetProperty(
                            "ApplicationFlexibleFieldList",
                            out var flexFields
                        )
                    )
                    {
                        foreach (var item in flexFields.EnumerateArray())
                        {
                            if (!item.TryGetProperty("m_Item2", out var m2))
                                continue;

                            var flexName = m2.TryGetProperty("FlexibleFieldNameDup", out var fnEl)
                                ? fnEl.GetString()
                                : null;

                            if (flexName == null)
                                continue;

                            switch (flexName)
                            {
                                case "__RTW_RightToWorkStatus":
                                    rtwFlexStatus = m2.TryGetProperty(
                                        "FieldValueString",
                                        out var vs
                                    )
                                        ? vs.GetString()
                                        : null;
                                    break;
                                case "__RTW_WorkRestrictions":
                                    rtwWorkRestrictions = m2.TryGetProperty(
                                        "FieldValueString",
                                        out var wr
                                    )
                                        ? wr.GetString()
                                        : null;
                                    break;
                                case "__RTW_FollowUpDate":
                                    var rawDate = m2.TryGetProperty("FieldValueDate", out var fd)
                                        ? fd.GetString()
                                        : null;
                                    if (
                                        !string.IsNullOrWhiteSpace(rawDate)
                                        && DateTime.TryParse(rawDate, out var fuParsed)
                                    )
                                        rtwFollowUpDate = fuParsed.ToString("dd/MM/yyyy");
                                    break;
                                case "__RTW_Notes":
                                    rtwNotes = m2.TryGetProperty("FieldValueString", out var nt)
                                        ? nt.GetString()
                                        : null;
                                    break;
                            }
                        }
                    }

                    tracing.Trace(
                        $"RTW Flex — Status: {rtwFlexStatus ?? "N/A"}, Restrictions: {rtwWorkRestrictions ?? "none"}, FollowUp: {rtwFollowUpDate ?? "none"}, Notes: {rtwNotes ?? "none"}"
                    );

                    // --- Step 3: Digital Identity verification check (only when not Share Code) ---
                    string rtwDigitalIdResult = null;
                    var rtwDigitalIdFailureReasons = new System.Collections.Generic.List<string>();

                    if (!isShareCode && hasValidationList)
                    {
                        foreach (var v in validationList.EnumerateArray())
                        {
                            var vName = v.GetProperty("Name").GetString();
                            if (vName == "RightToWorkDigitalIdentityVerificationCheck")
                            {
                                var outcome = v.GetProperty("ValidationOutcome").GetInt32();
                                rtwDigitalIdResult = outcome == 4 ? "Pass" : "Fail";
                                break;
                            }
                        }

                        if (rtwDigitalIdResult == "Fail")
                        {
                            foreach (var v in validationList.EnumerateArray())
                            {
                                var vName = v.GetProperty("Name").GetString();
                                if (
                                    vName.StartsWith("RightToWorkDigitalIdentity")
                                    && vName != "RightToWorkDigitalIdentityVerificationCheck"
                                    && v.GetProperty("ValidationOutcome").GetInt32() != 4
                                )
                                {
                                    rtwDigitalIdFailureReasons.Add(
                                        vName
                                            .Replace("RightToWorkDigitalIdentity", "")
                                            .Replace("Verification", "")
                                    );
                                }
                            }
                        }
                    }

                    // --- Face Match ---
                    string faceMatchResult = "Not Performed";
                    if (
                        containerEl.TryGetProperty("Documents", out var docsForFaceMatch)
                        && docsForFaceMatch.GetArrayLength() > 0
                    )
                    {
                        var firstDocProps = docsForFaceMatch[0];
                        if (firstDocProps.TryGetProperty("GeneralDocumentProperties", out var gdProps))
                        {
                            foreach (var prop in gdProps.EnumerateArray())
                            {
                                var propName = prop.TryGetProperty("Name", out var pn)
                                    ? pn.GetString()
                                    : null;
                                if (propName == "Photo Matches Applicant (TrustId)")
                                {
                                    var value = prop.TryGetProperty("Value", out var valEl)
                                        && valEl.GetBoolean();
                                    var valueUndefined = prop.TryGetProperty("ValueUndefined", out var vuEl)
                                        && vuEl.GetBoolean();

                                    if (!value && !valueUndefined)
                                    {
                                        faceMatchResult = "Fail";
                                    }
                                    else if (value && !valueUndefined)
                                    {
                                        faceMatchResult = "Pass";
                                    }
                                    else if (value && valueUndefined)
                                    {
                                        var errMsg = prop.TryGetProperty("ErrorMessage", out var emEl)
                                            ? emEl.GetString()
                                            : null;
                                        faceMatchResult = string.IsNullOrWhiteSpace(errMsg)
                                            ? "Unsure"
                                            : errMsg;
                                    }
                                    break;
                                }
                            }
                        }
                    }


                    // --- Step 4: Build compact RTW line for results section ---
                    string rtwCompactLine;

                    if (isShareCode)
                    {
                        // Share Code: RTW: Share Code - Continuous
                        rtwCompactLine = $"RTW: Share Code - {rtwFlexStatus ?? "N/A"}";
                    }
                    else
                    {
                        // Digital Identity: RTW: Digital Identity - Pass
                        // Only append status if it's NOT Continuous (the default/expected outcome)
                        var checkPart = rtwDigitalIdResult ?? "N/A";
                        if (rtwDigitalIdResult == "Fail" && rtwDigitalIdFailureReasons.Count > 0)
                            checkPart += $" ({string.Join(", ", rtwDigitalIdFailureReasons)})";

                        var showFlexStatus =
                            !string.IsNullOrWhiteSpace(rtwFlexStatus)
                            && !string.Equals(
                                rtwFlexStatus,
                                "Continuous",
                                StringComparison.OrdinalIgnoreCase
                            );

                        rtwCompactLine = showFlexStatus
                            ? $"RTW: Digital Identity - {checkPart} - {rtwFlexStatus}"
                            : $"RTW: Digital Identity - {checkPart}";
                    }

                    // --- Step 5: Build RTW Details section (only populated lines) ---
                    var rtwDetailLines = new System.Collections.Generic.List<string>();
                    rtwDetailLines.Add("RTW Details");
                    rtwDetailLines.Add($"Method: {rtwMethod}");

                    if (!isShareCode && rtwDigitalIdResult != null)
                    {
                        var checkDetail = rtwDigitalIdResult;
                        if (rtwDigitalIdResult == "Fail" && rtwDigitalIdFailureReasons.Count > 0)
                            checkDetail += $" ({string.Join(", ", rtwDigitalIdFailureReasons)})";
                        rtwDetailLines.Add($"Check: {checkDetail}");
                    }

                    rtwDetailLines.Add($"Status: {rtwFlexStatus ?? "N/A"}");

                    if (!string.IsNullOrWhiteSpace(rtwWorkRestrictions))
                        rtwDetailLines.Add($"Work Restrictions: {rtwWorkRestrictions}");

                    if (!string.IsNullOrWhiteSpace(rtwFollowUpDate))
                        rtwDetailLines.Add($"Follow-Up Date: {rtwFollowUpDate}");

                    if (!string.IsNullOrWhiteSpace(rtwNotes))
                        rtwDetailLines.Add($"Notes: {rtwNotes}");

                    rtwDetailLines.Add($"Face Match: {faceMatchResult}");

                    var rtwDetails = string.Join("\n", rtwDetailLines);

                    // Extract DBS Basic check results
                    string dbsStatus = "N/A";
                    var dbsFailureReasons = new System.Collections.Generic.List<string>();

                    if (hasValidationList)
                    {
                        foreach (var v in validationList.EnumerateArray())
                        {
                            var vName = v.GetProperty("Name").GetString();
                            if (vName == "DBSBasicIdentityVerificationCheck")
                            {
                                var outcome = v.GetProperty("ValidationOutcome").GetInt32();
                                dbsStatus = outcome == 4 ? "Pass" : "Fail";
                            }
                        }

                        // If failed, find which sub-checks caused it
                        if (dbsStatus == "Fail")
                        {
                            foreach (var v in validationList.EnumerateArray())
                            {
                                var vName = v.GetProperty("Name").GetString();
                                if (
                                    vName.StartsWith("DBSBasicDigitalIdentity")
                                    && v.GetProperty("ValidationOutcome").GetInt32() != 4
                                )
                                {
                                    dbsFailureReasons.Add(
                                        vName
                                            .Replace("DBSBasicDigitalIdentity", "")
                                            .Replace("Verification", "")
                                    );
                                }
                            }
                        }
                    }

                    var dbsSummary =
                        dbsStatus == "Fail" && dbsFailureReasons.Count > 0
                            ? $"{dbsStatus} ({string.Join(", ", dbsFailureReasons)})"
                            : dbsStatus;

                    // --- Address Verification ---
                    string addressVerificationResult = "Not Performed";
                    if (hasValidationList)
                    {
                        foreach (var v in validationList.EnumerateArray())
                        {
                            var vName = v.GetProperty("Name").GetString();
                            if (vName == "AddressVerification")
                            {
                                var raw = v.TryGetProperty("DetailedResult", out var ar)
                                    ? ar.GetString()
                                    : null;
                                addressVerificationResult = string.IsNullOrWhiteSpace(raw)
                                    ? "Not Performed"
                                    : raw;
                                break;
                            }
                        }
                    }


                    // --- KYC/AML Check ---
                    //string kycAmlResult = "N/A";

                    //foreach (var v in validationList.EnumerateArray())
                    //{
                    //    var vName = v.GetProperty("Name").GetString();
                    //    if (vName == "KycAmlCheck")
                    //    {
                    //        kycAmlResult = v.TryGetProperty("DetailedResult", out var kr)
                    //            ? kr.GetString()
                    //            : "N/A";
                    //        break;
                    //    }
                    //}

                    // --- Overall Status: 0=NO_ALERT, 1=ALERT, 2=RESOLVED ---
                    string overallStatus = "N/A";
                    if (containerEl.TryGetProperty("OverallStatus", out var overallEl))
                    {
                        var overallValue = overallEl.GetInt32();
                        switch (overallValue)
                        {
                            case 0:
                                overallStatus = "No Alert";
                                break;
                            case 1:
                                overallStatus = "Alert - Needs Investigation";
                                break;
                            case 2:
                                overallStatus = "Resolved";
                                break;
                            default:
                                overallStatus = $"Unknown ({overallValue})";
                                break;
                        }
                    }

                    // Report Details

                    // --- Step 6: Build results status with new compact RTW line ---
                    var resultsstatus =
                        rtwCompactLine
                        + "\n"
                        + $"DBS Basic Check: {dbsSummary}\n"
                        + $"Address Verification: {addressVerificationResult}";

                    string documentSummary;
                    if (documentRead)
                    {
                        documentSummary =
                            $"Document: {docType ?? "Unknown"}\n"
                            + $"First Name: {firstName ?? "N/A"}\n"
                            + $"Middle Name: {middleName ?? "N/A"}\n"
                            + $"Surname: {surname ?? "N/A"}\n"
                            + $"Date of Birth: {dob ?? "N/A"}\n"
                            + $"Expiry Date: {expiryDate ?? "N/A"}\n"
                            + $"Nationality: {nationality ?? "N/A"}\n"
                            + $"\nAddress Input: {(string.IsNullOrWhiteSpace(fullAddress) ? "N/A" : fullAddress)}";
                    }
                    else
                    {
                        documentSummary =
                            "Document: Not Read\n"
                            + $"First Name: {firstName ?? "Not Read"}\n"
                            + $"Middle Name: {middleName ?? "Not Read"}\n"
                            + $"Surname: {surname ?? "Not Read"}\n"
                            + $"Date of Birth: {dob ?? "Not Read"}\n"
                            + $"Expiry Date: {expiryDate ?? "Not Read"}\n"
                            + $"Nationality: {nationality ?? "Not Read"}\n"
                            + $"\nAddress Input: {(string.IsNullOrWhiteSpace(fullAddress) ? "N/A" : fullAddress)}";
                    }

                    var otherNotes =
                        //$"KYC/AML Check: {kycAmlResult}\n"
                        $"Overall Status: {overallStatus}";

                    // --- Step 7: Assemble report with RTW Details section ---
                    update["parl_trustidreportdetails"] =
                        resultsstatus
                        + "\n\n"
                        + documentSummary
                        + "\n\n"
                        + rtwDetails
                        + "\n\n"
                        + otherNotes;

                    // Summary Description
                    var summarydescription =
                        "Reports Status.\n"
                        + $"Fullname: {containerEl.GetProperty("Fullname").GetString() ?? "Not Found"}\n"
                        + $"Message: {root.GetProperty("Message").GetString() ?? "Not Found"}\n"
                        + $"ContainerId: {containerEl.GetProperty("Id").GetString() ?? "Not Found"}";

                    // Last Message
                    update["parl_trustidlastmessagedate"] = DateTime.UtcNow;
                    update["parl_trustidlastmessagedescription"] = summarydescription;

                    // Results Description
                    update["parl_trustidresultsupdatedon"] = DateTime.UtcNow;
                    update["parl_trustidresultsdescription"] = summarydescription;
                }
                catch (Exception ex)
                {
                    tracing.Trace($"Could not extract progress description: {ex.Message}");
                }

                // --- Audit log entry ---
                var existingLog =
                    bpssRecord.GetAttributeValue<string>("parl_trustidauditlog") ?? string.Empty;
                var logEntry =
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} | SYSTEM | Documents retrieved and uploaded (Container: {containerid})";

                update["parl_trustidauditlog"] = string.IsNullOrWhiteSpace(existingLog)
                    ? logEntry
                    : existingLog + Environment.NewLine + logEntry;

                service.Update(update);
                tracing.Trace(
                    "Record updated: parl_trustiddocumentstatus set to 'Documents Uploaded to PSV', parl_trustidlastmessagedate stamped."
                );
            }
            catch (Exception ex)
            {
                tracing.Trace("Error in TrustIDRetrieveUpdatesplg: " + ex);

                var existingLog =
                    bpssRecord.GetAttributeValue<string>("parl_trustidauditlog") ?? string.Empty;
                var logEntry =
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} | SYSTEM | Document retrieval failed: {ex.Message}";

                var failUpdate = new Entity("parl_bpsscheck") { Id = context.PrimaryEntityId };
                failUpdate["parl_trustidauditlog"] = string.IsNullOrWhiteSpace(existingLog)
                    ? logEntry
                    : existingLog + Environment.NewLine + logEntry;

                service.Update(failUpdate);

                throw;
            }
        }

        private static string ConvertMicrosoftDatesToReadable(string json)
        {
            var regex = new System.Text.RegularExpressions.Regex(
                @"\\?/Date\((-?\d+)([+-]\d{4})?\)\\?/"
            );

            return regex.Replace(
                json,
                match =>
                {
                    var milliseconds = long.Parse(match.Groups[1].Value);
                    var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                    //return $"\"{dateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\"";
                    return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                }
            );
        }
    }
}
