using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Common.Config;
using Logging;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;

namespace Common.Migration
{
    /// <summary>
    /// Template Method base class for processors that produce one or more attachments
    /// (as AttachmentLink instances) to be added to the target work item.
    /// Derived classes implement how attachments are gathered/generated.
    /// </summary>
    public abstract class NewAttachmentsProcessor : IPhase2Processor
    {
        protected abstract ILogger Logger { get; }

        public abstract string Name { get; }

        public abstract bool IsEnabled(ConfigJson config);

        public virtual Task Preprocess(IMigrationContext migrationContext,
                                       IBatchMigrationContext batchContext,
                                       IList<WorkItem> sourceWorkItems,
                                       IList<WorkItem> targetWorkItems)
        {
            return Task.CompletedTask;
        }

        public async Task<IEnumerable<JsonPatchOperation>> Process(IMigrationContext migrationContext,
                                                                   IBatchMigrationContext batchContext,
                                                                   WorkItem sourceWorkItem,
                                                                   WorkItem targetWorkItem)
        {
            var ops = new List<JsonPatchOperation>();

            IList<AttachmentLink> attachments = await GenerateAttachmentsAsync(migrationContext, sourceWorkItem, targetWorkItem);

            foreach (var attachment in attachments)
            {
                // Uses existing helper (name retained for backward compatibility).
                var addOp = MigrationHelpers.GetRevisionHistoryAttachmentAddOperation(attachment, sourceWorkItem.Id.Value);
                ops.Add(addOp);
            }

            return ops;
        }

        /// <summary>
        /// Template hook: derived classes return zero or more attachments to add.
        /// They are responsible for avoiding duplicates or choosing to re-add.
        /// </summary>
        protected abstract Task<IList<AttachmentLink>> GenerateAttachmentsAsync(IMigrationContext migrationContext,
                                                                                WorkItem sourceWorkItem,
                                                                                WorkItem targetWorkItem);

        /// <summary>
        /// Helper used by JSON-based attachment processors (history / PR) to serialize an object.
        /// Binary-copy processors can ignore this.
        /// </summary>
        protected async Task<AttachmentLink> CreateJsonAttachmentAsync(IMigrationContext migrationContext,
                                                                       object content,
                                                                       string fileName,
                                                                       string comment)
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(content);

            // Redact any phrases if configured to do so
            if (migrationContext.Config.RedactionEnabled && migrationContext.Config.RedactionPhrases.Any())
            {
                foreach (var phrase in migrationContext.Config.RedactionPhrases)
                {
                    json = json.Replace(phrase, "REDACTED");
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (var ms = new System.IO.MemoryStream(bytes, writable: false))
            {
                var aRef = await WorkItemTrackingHelpers.CreateAttachmentAsync(
                    migrationContext.TargetClient.WorkItemTrackingHttpClient, ms);
                return new AttachmentLink(fileName, aRef, bytes.Length, comment);
            }
        }
    }
}