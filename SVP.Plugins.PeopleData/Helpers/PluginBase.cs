using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;


namespace SVP.Plugins.PeopleData.Helpers
{
    /* 
     * Plugin development guide: https://docs.microsoft.com/powerapps/developer/common-data-service/plug-ins
     * Best practices and guidance: https://docs.microsoft.com/powerapps/developer/common-data-service/best-practices/business-logic/
     */
    public abstract class PluginBase : IPlugin
    {
        protected string PluginClassName { get; }

        protected PluginBase(Type pluginClassName)
        {
            PluginClassName = pluginClassName.ToString();
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
            {
                throw new InvalidPluginExecutionException("serviceProvider");
            }

            var localPluginContext = new LocalPluginContext(serviceProvider);


            using (localPluginContext.LoggingService.BeginScope(
                new Dictionary<string, object>
                {
                    { "DataversePrimaryEntityId", localPluginContext.PluginExecutionContext.PrimaryEntityId },
                    { "DataversePrimaryEntityName", localPluginContext.PluginExecutionContext.PrimaryEntityName },
                    { "DataversePluginClassName", PluginClassName },
                    { "DataversePluginCorrelationId", localPluginContext.PluginExecutionContext.CorrelationId },
                    { "DataverseInitiatingUserId", localPluginContext.PluginExecutionContext.InitiatingUserId },
                    { "DataverseMessageName", localPluginContext.PluginExecutionContext.MessageName },
                    { "DataverseOrganizationName", localPluginContext.PluginExecutionContext.OrganizationName },
                    { "DataverseMode", localPluginContext.PluginExecutionContext.Mode },
                    { "DataversePluginStage:", localPluginContext.PluginExecutionContext.Stage }
                }
                ))
            {
                /*
                 * Using placeholders rather than string interpolation is preferred for ILogger as this means
                 * the properties as transferred as custom properties and become available for filtering in Application Insights.
                 */
                localPluginContext.LoggingService.LogInformation("Entered {pluginName}.Execute()", PluginClassName);

                localPluginContext.Trace($"Entered {PluginClassName}.Execute() " +
                    $"Correlation Id: {localPluginContext.PluginExecutionContext.CorrelationId}, " +
                    $"Initiating User: {localPluginContext.PluginExecutionContext.InitiatingUserId}");

                try
                {
                    ExecuteCdsPlugin(localPluginContext);

                    // Now exit - if the derived plugin has incorrectly registered overlapping event registrations, guard against multiple executions.
                    return;
                }
                catch (Exception ex)
                {
                    localPluginContext.Trace($"Exception in {PluginClassName}: {ex.Message}");
                    localPluginContext.LoggingService.LogError(ex, "Error in {pluginName}: {message}", PluginClassName, ex.Message);
                    throw new InvalidPluginExecutionException($"{ex.Message}", ex);
                }
                finally
                {
                    localPluginContext.LoggingService.LogInformation("Exiting {pluginName}.Execute()", PluginClassName);
                    localPluginContext.Trace($"Exiting {PluginClassName}.Execute()");
                }
            }
        }

        protected virtual void ExecuteCdsPlugin(ILocalPluginContext localPluginContext)
        {
            // Do nothing. 
        }
    }

    /*
    * This interface provides an abstraction on top of IServiceProvider for commonly used PowerApps CDS Plugin development constructs
    */
    public interface ILocalPluginContext
    {
        // The PowerApps CDS organization service for current user account
        IOrganizationService CurrentUserService { get; }

        // The PowerApps CDS organization service for system user account
        IOrganizationService SystemUserService { get; }

        // IPluginExecutionContext contains information that describes the run-time environment in which the plugin executes, information related to the execution pipeline, and entity business information
        IPluginExecutionContext PluginExecutionContext { get; }

        // Synchronous registered plugins can post the execution context to the Microsoft Azure Service Bus.
        // It is through this notification service that synchronous plug-ins can send brokered messages to the Microsoft Azure Service Bus
        IServiceEndpointNotificationService NotificationService { get; }

        // Provides logging run time trace information for plug-ins. 
        ITracingService TracingService { get; }

        // Writes telemetery to Application Insights if enabled for the organisation
        ILogger LoggingService { get; }

        // Data Source Retrieve Service used for virtual entities
        IEntityDataSourceRetrieverService DataSourceRetrieverService { get; }

        // Extract a data service for virtual entities
        //IDataService GetDataService();

        // Writes a trace message to the CDS trace log
        void Trace(string message);

        // Gets the first pre image from the context
        Entity GetFirstPreImage();

        // Gets the first post image from the context
        Entity GetFirstPostImage();


        // Get the Azure Active Directory Object Id of the systemuser that triggered the plugin execution.
        Guid GetInitiatingUserAADId();

    }

    public class LocalPluginContext : ILocalPluginContext
    {
        public IOrganizationService CurrentUserService { get; }

        public IOrganizationService SystemUserService { get; }

        public IPluginExecutionContext PluginExecutionContext { get; }

        public IServiceEndpointNotificationService NotificationService { get; }

        public ITracingService TracingService { get; }

        public ILogger LoggingService { get; }

        public IEntityDataSourceRetrieverService DataSourceRetrieverService { get; }

        public LocalPluginContext(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
            {
                throw new InvalidPluginExecutionException("serviceProvider");
            }

            PluginExecutionContext = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            TracingService = new LocalTracingService(serviceProvider);

            NotificationService = (IServiceEndpointNotificationService)serviceProvider.GetService(typeof(IServiceEndpointNotificationService));

            IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            CurrentUserService = factory.CreateOrganizationService(PluginExecutionContext.UserId);

            SystemUserService = factory.CreateOrganizationService(null);

            LoggingService = (ILogger)serviceProvider.GetService(typeof(ILogger));

            DataSourceRetrieverService = (IEntityDataSourceRetrieverService)serviceProvider.GetService(typeof(IEntityDataSourceRetrieverService));
        }

        //public IDataService GetDataService()
        //{
        //    var retriever = this.DataSourceRetrieverService;
        //    var dataSource = retriever.RetrieveEntityDataSource();

        //    return new DataService(this, SystemUserService, TracingService, dataSource);
        //}

        public void Trace(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || TracingService == null)
            {
                return;
            }

            TracingService.Trace(message);
        }

        public Entity GetFirstPostImage()
        {
            return PluginExecutionContext?.PostEntityImages?.Count > 0 ? PluginExecutionContext?.PostEntityImages.First().Value : null;
        }

        public Entity GetFirstPreImage()
        {
            return PluginExecutionContext?.PreEntityImages?.Count > 0 ? PluginExecutionContext?.PreEntityImages.First().Value : null;
        }



        /// <summary>
        /// Get the Azure Active Directory Object Id of the systemuser that triggered the plugin execution.
        /// </summary>
        /// <returns></returns>
        public Guid GetInitiatingUserAADId()
        {
            var initiatingUser = SystemUserService.Retrieve("systemuser", PluginExecutionContext.InitiatingUserId, new Microsoft.Xrm.Sdk.Query.ColumnSet("azureactivedirectoryobjectid"));
            return initiatingUser.GetAttributeValue<Guid>("azureactivedirectoryobjectid");
        }

    }

    /*
     * Specialized ITracingService implementation that prefixes all traced messages with a time delta for Plugin performance diagnostics
     * Additionally writes messages sent to tracing to the logging service.
     */
    public class LocalTracingService : ITracingService
    {
        private readonly ITracingService _tracingService;
        private readonly ILogger _logger;

        private DateTime _previousTraceTime;

        public LocalTracingService(IServiceProvider serviceProvider)
        {
            DateTime utcNow = DateTime.UtcNow;

            var context = (IExecutionContext)serviceProvider.GetService(typeof(IExecutionContext));

            DateTime initialTimestamp = context.OperationCreatedOn;

            if (initialTimestamp > utcNow)
            {
                initialTimestamp = utcNow;
            }

            _tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            _logger = (ILogger)serviceProvider.GetService(typeof(ILogger));

            _previousTraceTime = initialTimestamp;
        }

        public void Trace(string message, params object[] args)
        {
            var utcNow = DateTime.UtcNow;

            // The duration since the last trace.
            var deltaMilliseconds = utcNow.Subtract(_previousTraceTime).TotalMilliseconds;

            _tracingService.Trace($"[+{deltaMilliseconds:N0}ms)] - {message}");
            _logger.LogTrace(message);

            _previousTraceTime = utcNow;
        }
    }
}
