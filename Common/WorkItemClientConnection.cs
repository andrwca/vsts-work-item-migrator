using System;
using Logging;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace Common
{
    public class WorkItemClientConnection
    {
        static ILogger Logger { get; } = MigratorLogging.CreateLogger<WorkItemClientConnection>();
        public GitHttpClient GitHttpClient { get; private set; }
        public WorkItemTrackingHttpClient WorkItemTrackingHttpClient { get; private set; }
        protected VssCredentials Credentials { get; private set; }
        protected Uri Url { get; private set; }
        public VssConnection Connection { get; private set; }

        public WorkItemClientConnection(Uri uri, VssCredentials credentials)
        {
            Connect(uri, credentials);
        }

        private void Connect(Uri uri, VssCredentials credentials)
        {
            this.Url = uri;
            this.Credentials = credentials;
            this.Connection = new VssConnection(uri, credentials);

            try
            {
                this.GitHttpClient = new GitHttpClient(uri, credentials, new VssHttpRequestSettings { SendTimeout = TimeSpan.FromMinutes(5) });
                this.WorkItemTrackingHttpClient = new WorkItemTrackingHttpClient(uri, credentials, new VssHttpRequestSettings { SendTimeout = TimeSpan.FromMinutes(5) });
            }
            catch (Exception e)
            {
                Logger.LogError(LogDestination.File, e, $"Unable to create the WorkItemTrackingHttpClient for {Url}");
                throw e;
            }
        }
    }
}
