using System.Collections.Generic;
using System.Threading.Tasks;
using Common.Config;
using Logging;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;

namespace Common.Migration
{
    public class RevisionHistoryAttachmentsProcessor : NewAttachmentsProcessor
    {
        static ILogger _logger = MigratorLogging.CreateLogger<RevisionHistoryAttachmentsProcessor>();
        protected override ILogger Logger => _logger;

        public override string Name => Constants.RelationPhaseRevisionHistoryAttachments;

        public override bool IsEnabled(ConfigJson config) => config.MoveHistory;

        protected override async Task<IList<AttachmentLink>> GenerateAttachmentsAsync(IMigrationContext migrationContext,
                                                                                       WorkItem sourceWorkItem,
                                                                                       WorkItem targetWorkItem)
        {
            var links = new List<AttachmentLink>();
            int updateLimit = migrationContext.Config.MoveHistoryLimit;
            int skip = 0;

            while (skip < updateLimit)
            {
                var updates = await WorkItemTrackingHelpers.GetWorkItemUpdatesAsync(
                    migrationContext.SourceClient.WorkItemTrackingHttpClient,
                    sourceWorkItem.Id.Value,
                    skip);

                links.Add(
                    await CreateJsonAttachmentAsync(
                        migrationContext,
                        updates,
                        $"{Constants.WorkItemHistory}-{sourceWorkItem.Id}-{skip}.json",
                        $"Work item history from {skip} to {skip + updates.Count}"));

                skip += updates.Count;
                if (updates.Count < Constants.PageSize)
                {
                    break;
                }
            }

            return links;
        }
    }
}