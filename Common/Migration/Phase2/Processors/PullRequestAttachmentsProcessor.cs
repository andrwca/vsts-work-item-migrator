using Common.Config;
using Logging;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Common.Migration
{
    public class PullRequestAttachmentsProcessor : NewAttachmentsProcessor
    {
        static ILogger _logger = MigratorLogging.CreateLogger<PullRequestAttachmentsProcessor>();
        protected override ILogger Logger => _logger;

        public override string Name => Constants.RelationPhasePullRequestAttachments;

        public override bool IsEnabled(ConfigJson config) => config.MovePullRequests;

        protected override async Task<IList<AttachmentLink>> GenerateAttachmentsAsync(IMigrationContext migrationContext,
                                                                                       WorkItem sourceWorkItem,
                                                                                       WorkItem targetWorkItem)
        {
            var results = new List<AttachmentLink>();
            var prLinks = GetPullRequestArtifactLinks(sourceWorkItem);

            foreach (var prRel in prLinks)
            {
                var pr = await GetPullRequest(migrationContext, sourceWorkItem, prRel);
                var threads = await GetThreads(migrationContext, pr);

                results.Add(
                    await CreateJsonAttachmentAsync(
                        migrationContext,
                        pr,
                        $"{Constants.PullRequest}-{sourceWorkItem.Id}-{pr.PullRequestId}.json",
                        $"Pull request Id: {pr.PullRequestId}"));

                foreach (var thread in threads)
                {
                    results.Add(
                    await CreateJsonAttachmentAsync(
                        migrationContext,
                        thread,
                        $"{Constants.PullRequestComments}-{sourceWorkItem.Id}-{pr.PullRequestId}-{thread.Id}.json",
                        $"Pull request Id: {pr.PullRequestId}"));
                }
            }

            return results;
        }

        private static IEnumerable<WorkItemRelation> GetPullRequestArtifactLinks(WorkItem wi)
        {
            if (wi?.Relations == null) return Enumerable.Empty<WorkItemRelation>();

            return wi.Relations.Where(r =>
                r.Rel == Constants.RelationArtifactLink
                && r.Attributes != null
                && r.Attributes.ContainsKey(Constants.RelationAttributeName)
                && string.Equals(r.Attributes[Constants.RelationAttributeName]?.ToString(),
                                 Constants.RelationAttributePullRequestNameValue,
                                 StringComparison.OrdinalIgnoreCase));
        }

        private async Task<GitPullRequest> GetPullRequest(IMigrationContext ctx, WorkItem wi, WorkItemRelation prRel)
        {
            try
            {
                var url = WebUtility.UrlDecode(prRel.Url).Trim('/');
                var prId = url.Split('/').Last();

                return await ctx.SourceClient.GitHttpClient.GetPullRequestByIdAsync(int.Parse(prId));
            }
            catch (Exception ex)
            {
                Logger.LogError(LogDestination.File, ex, $"Error retrieving PR for work item {wi.Id} relation {prRel.Url}");
                throw;
            }
        }

        private async Task<List<GitPullRequestCommentThread>> GetThreads(IMigrationContext ctx, GitPullRequest pr)
        {
            try
            {
                return await ctx.SourceClient.GitHttpClient.GetThreadsAsync(pr.Repository.Id, pr.PullRequestId);
            }
            catch (Exception ex)
            {
                Logger.LogError(LogDestination.File, ex, $"Error retrieving PR threads for PR {pr.PullRequestId}");
                throw;
            }
        }
    }
}