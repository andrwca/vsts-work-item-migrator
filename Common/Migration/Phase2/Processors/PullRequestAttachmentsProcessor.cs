using Common.Config;
using Logging;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Common.Migration
{
    public class PullRequestAttachmentsProcessor : IPhase2Processor
    {
        static ILogger Logger { get; } = MigratorLogging.CreateLogger<PullRequestAttachmentsProcessor>();

        public string Name => Constants.RelationPhasePullRequestAttachments;

        public bool IsEnabled(ConfigJson config)
        {
            return config.MovePullRequests;
        }

        public async Task Preprocess(IMigrationContext migrationContext, IBatchMigrationContext batchContext, IList<WorkItem> sourceWorkItems, IList<WorkItem> targetWorkItems)
        {

        }

        public async Task<IEnumerable<JsonPatchOperation>> Process(IMigrationContext migrationContext, IBatchMigrationContext batchContext, WorkItem sourceWorkItem, WorkItem targetWorkItem)
        {
            var jsonPatchOperations = new List<JsonPatchOperation>();

            // Find PRs associated with the work item
            // (ArtifactLink relations whose name attribute == "Pull Request")
            var pullRequestArtifactLinks = GetPullRequestArtifactLinks(sourceWorkItem);

            var attachments = await UploadAttachmentsToTarget(migrationContext, sourceWorkItem, pullRequestArtifactLinks);
            foreach (var attachment in attachments)
            {
                JsonPatchOperation prAttachmentAddOperation = MigrationHelpers.GetRevisionHistoryAttachmentAddOperation(attachment, sourceWorkItem.Id.Value);
                jsonPatchOperations.Add(prAttachmentAddOperation);
            }

            return jsonPatchOperations;
        }

        private static IEnumerable<WorkItemRelation> GetPullRequestArtifactLinks(WorkItem workItem)
        {
            if (workItem?.Relations == null)
            {
                return Enumerable.Empty<WorkItemRelation>();
            }

            return workItem.Relations.Where(r =>
                r.Rel == Constants.RelationArtifactLink
                && r.Attributes != null
                && r.Attributes.ContainsKey(Constants.RelationAttributeName)
                && string.Equals(r.Attributes[Constants.RelationAttributeName]?.ToString(),
                                 Constants.RelationAttributePullRequestNameValue,
                                 System.StringComparison.OrdinalIgnoreCase));
        }

        private async Task<IList<AttachmentLink>> UploadAttachmentsToTarget(IMigrationContext migrationContext, WorkItem sourceWorkItem, IEnumerable<WorkItemRelation> pullRequestArtifactLinks)
        {
            var attachmentLinks = new List<AttachmentLink>();

            foreach (var prLink in pullRequestArtifactLinks)
            {
                var pullRequest = await GetPullRequest(migrationContext, sourceWorkItem, prLink);
                var pullRequestComments = await GetPullRequestComments(migrationContext, pullRequest);

                attachmentLinks.AddRange(
                    await AddAttachment(
                        migrationContext,
                        pullRequest,
                        $"{Constants.PullRequest}-{sourceWorkItem.Id}-{pullRequest.PullRequestId}.json",
                        $"Pull request Id: {pullRequest.PullRequestId}"));

                attachmentLinks.AddRange(
                    await AddAttachment(
                        migrationContext,
                        pullRequestComments,
                        $"{Constants.PullRequestComments}-{sourceWorkItem.Id}-{pullRequest.PullRequestId}.json",
                        $"Pull request Id: {pullRequest.PullRequestId}"));
            }

            return attachmentLinks;
        }

        private static async Task<IEnumerable<AttachmentLink>> AddAttachment(IMigrationContext migrationContext, object content, string filename, string comment)
        {
            var links = new List<AttachmentLink>();
            string attachmentContent = JsonConvert.SerializeObject(content);
            using (MemoryStream stream = new MemoryStream())
            {
                var stringBytes = System.Text.Encoding.UTF8.GetBytes(attachmentContent);
                await stream.WriteAsync(stringBytes, 0, stringBytes.Length);
                stream.Position = 0;

                //upload the attachment to the target for each batch of workitem updates
                var attachmentReference = await WorkItemTrackingHelpers.CreateAttachmentAsync(migrationContext.TargetClient.WorkItemTrackingHttpClient, stream);
                links.Add(new AttachmentLink(filename, attachmentReference, stringBytes.Length, comment: comment));
            }
            return links;
        }

        private async Task<GitPullRequestCommentThread> GetPullRequestComments(IMigrationContext migrationContext, GitPullRequest pullRequest)
        {
            try
            {
                var threads = await migrationContext.SourceClient.GitHttpClient.GetThreadsAsync(pullRequest.Repository.Id, pullRequest.PullRequestId);
                return threads.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.LogError(LogDestination.File, ex, $"Error retrieving PR comments for PR {pullRequest.PullRequestId}");
                throw;
            }
        }

        private async Task<GitPullRequest> GetPullRequest(IMigrationContext migrationContext, WorkItem sourceWorkItem, WorkItemRelation prLink)
        {
            try
            {
                var url = WebUtility.UrlDecode(prLink.Url).Trim('/');
                var prId = url.Split('/').Last();
                return await migrationContext.SourceClient.GitHttpClient.GetPullRequestByIdAsync(int.Parse(prId));
            }
            catch (Exception ex)
            {
                Logger.LogError(LogDestination.File, ex, $"Error retrieving PR details for work item {sourceWorkItem.Id} PR link {prLink.Url}");
                throw;
            }
        }
    }
}
