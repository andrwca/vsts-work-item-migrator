using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Common.Config;
using Logging;

namespace Common.Migration
{
    public abstract class BaseWitBatchRequestGenerator
    {
        static ILogger Logger { get; } = MigratorLogging.CreateLogger<BaseWitBatchRequestGenerator>();

        protected IMigrationContext migrationContext; // stop passing this around
        protected IBatchMigrationContext batchContext;

        // Chose List of tuples instead of Dictionary because this guarantees that the ordering is maintained. Also lets us use custom names for the 2 values rather than key/value.
        // WorkItem Id property is a nullable int, so we have it here also
        protected List<(int BatchId, int WorkItemId)> IdWithinBatchToWorkItemIdMapping { get; }
        protected int IdWithinBatch;
        protected IList<WitBatchRequest> WitBatchRequests;
        protected string QueryString;

        public BaseWitBatchRequestGenerator()
        {
        }

        public BaseWitBatchRequestGenerator(IMigrationContext migrationContext, IBatchMigrationContext batchContext)
        {
            this.migrationContext = migrationContext;
            this.batchContext = batchContext;
            IdWithinBatchToWorkItemIdMapping = new List<(int BatchId, int WorkItemId)>();
            IdWithinBatch = -1;
            WitBatchRequests = new List<WitBatchRequest>();
            bool bypassRules = true;
            bool suppressNotifications = true;
            QueryString = $"bypassRules={bypassRules}&suppressNotifications={suppressNotifications}&api-version=7.1";

            // we only have a batch context when it's create/update work items
            if (batchContext != null)
            {
                // remove any work items marked as failed during preprocessing
                this.batchContext.SourceWorkItems = RemoveWorkItemsThatFailedPreprocessing(batchContext.WorkItemMigrationState, batchContext.SourceWorkItems);
            }
        }

        public abstract Task Write();

        protected bool WorkItemHasFailureState(WorkItem sourceWorkItem)
        {
            WorkItemMigrationState state = batchContext.WorkItemMigrationState.FirstOrDefault(a => a.SourceId == sourceWorkItem.Id.Value);

            if (state != null && state.MigrationState == WorkItemMigrationState.State.Error)
            {
                Logger.LogWarning(LogDestination.File, $"Skipping migration of work item with id {sourceWorkItem.Id.Value} due to Error in migration state with failure reasons: {state.FailureReason.ToString()}");
                return true;
            }

            return false;
        }

        protected IList<WorkItem> RemoveWorkItemsThatFailedPreprocessing(IList<WorkItemMigrationState> workItemMigrationState, IList<WorkItem> sourceWorkItems)
        {
            if (sourceWorkItems != null)
            {
                Dictionary<int, FailureReason> notMigratedWorkItems = ClientHelpers.GetNotMigratedWorkItemsFromWorkItemsMigrationState(workItemMigrationState);
                return sourceWorkItems.Where(w => !notMigratedWorkItems.ContainsKey(w.Id.Value)).ToList();
            }
            else
            {
                return null;
            }
        }

        protected void DecrementIdWithinBatch(int? sourceWorkItemId)
        {
            IdWithinBatchToWorkItemIdMapping.Add((IdWithinBatch, sourceWorkItemId.Value));
            IdWithinBatch--;
        }

        protected JsonPatchDocument CreateJsonPatchDocumentFromWorkItemFields(WorkItem sourceWorkItem)
        {
            string sourceWorkItemType = GetWorkItemTypeFromWorkItem(sourceWorkItem);
            JsonPatchDocument jsonPatchDocument = new JsonPatchDocument();

            IList<string> fieldNamesAlreadyPopulated = new List<string>();

            foreach (var sourceField in sourceWorkItem.Fields)
            {
                if (fieldNamesAlreadyPopulated.Contains(sourceField.Key)) // we have already processed the content for this target field so skip
                {
                    continue;
                }

                if (FieldIsWithinType(sourceField.Key, sourceWorkItemType) && !IsFieldUnsupported(sourceField.Key))
                {
                    KeyValuePair<string, object> fieldProcessedForConfigFields = GetTargetField(sourceField, fieldNamesAlreadyPopulated);
                    KeyValuePair<string, object> preparedField = UpdateProjectNameIfNeededForField(sourceWorkItem, fieldProcessedForConfigFields);
                    
                    // TEMPORARY HACK for handling emoticons in identity fields:
                    if (migrationContext.Config.ClearIdentityDisplayNames)
                    {
                        preparedField = RemoveEmojis(sourceField, preparedField);
                    }

                    // Redact fields if enabled.
                    if (migrationContext.Config.RedactionEnabled)
                    {
                        if (migrationContext.Config.RedactionPhrases != null && preparedField.Value is string)
                        {
                            string preparedFieldValueString = preparedField.Value as string;
                            foreach (string phrase in migrationContext.Config.RedactionPhrases)
                            {
                                preparedFieldValueString = preparedFieldValueString.Replace(phrase, "REDACTED", StringComparison.OrdinalIgnoreCase);
                            }
                            preparedField = new KeyValuePair<string, object>(preparedField.Key, preparedFieldValueString);
                        }
                    }

                    JsonPatchOperation jsonPatchOperation;

                    // Convert identity fields to string format "Display Name <Unique Name>"
                    if (sourceField.Value.GetType() == typeof(Microsoft.VisualStudio.Services.WebApi.IdentityRef))
                    {
                        var identity = (Microsoft.VisualStudio.Services.WebApi.IdentityRef)sourceField.Value;
                        string updatedIdentityFieldValue = identity.DisplayName + " <" + identity.UniqueName + ">";
                        KeyValuePair<string, object> updatedField = new KeyValuePair<string, object>(preparedField.Key, updatedIdentityFieldValue);
                           
                        jsonPatchOperation = MigrationHelpers.GetJsonPatchOperationAddForField(updatedField);
                        jsonPatchDocument.Add(jsonPatchOperation);
                        continue;
                    }

                    // add inline image urls
                    if (migrationContext.HtmlFieldReferenceNames.Contains(preparedField.Key) 
                        && preparedField.Value is string)
                    {
                        string updatedHtmlFieldValue = GetUpdatedHtmlField((string)preparedField.Value);
                        KeyValuePair<string, object> updatedField = new KeyValuePair<string, object>(preparedField.Key, updatedHtmlFieldValue);
                        jsonPatchOperation = MigrationHelpers.GetJsonPatchOperationAddForField(updatedField);
                    }
                    else
                    {
                        jsonPatchOperation = MigrationHelpers.GetJsonPatchOperationAddForField(preparedField);
                    }
                    jsonPatchDocument.Add(jsonPatchOperation);
                }
            }

            return jsonPatchDocument;
        }

        private string GetTargetFieldName(string sourceFieldName)
        {
            if (migrationContext.Config.FieldReplacements != null)
            {
                if (migrationContext.Config.FieldReplacements.ContainsKeyIgnoringCase(sourceFieldName))
                {
                    TargetFieldMap targetFieldMap = migrationContext.Config.FieldReplacements[sourceFieldName];
                    if (!string.IsNullOrEmpty(targetFieldMap.FieldReferenceName))
                    {
                        return targetFieldMap.FieldReferenceName;
                    }
                }
            }

            return sourceFieldName;
        }

        private KeyValuePair<string, object> GetTargetField(KeyValuePair<string, object> sourceField, IList<string> fieldNamesAlreadyPopulated)
        {
            KeyValuePair<string, object> newField = sourceField;

            string sourceFieldName = sourceField.Key;
            object sourceFieldValue = sourceField.Value;

            if (migrationContext.Config.FieldReplacements != null)
            {
                if (migrationContext.Config.FieldReplacements.ContainsKeyIgnoringCase(sourceFieldName))
                {
                    TargetFieldMap targetFieldMap = migrationContext.Config.FieldReplacements[sourceFieldName];
                    if (targetFieldMap.Value != null)
                    {
                        newField = new KeyValuePair<string, object>(sourceFieldName, targetFieldMap.Value); // set targetField to value
                    }
                    else if (!string.IsNullOrEmpty(targetFieldMap.FieldReferenceName))
                    {
                        newField = new KeyValuePair<string, object>(targetFieldMap.FieldReferenceName, sourceFieldValue); // bring source value to specified target field
                        fieldNamesAlreadyPopulated.Add(targetFieldMap.FieldReferenceName);
                    }
                }
            }

            return newField;
        }

        // TEMPORARY HACK for handling emoticons in identity fields:
        protected KeyValuePair<string, object> RemoveEmojis(KeyValuePair<string, object> sourceField, KeyValuePair<string, object> targetField)
        {
            if (targetField.Value is string
                && migrationContext.IdentityFields.Contains(sourceField.Key))
            {
                string targetFieldValueString = targetField.Value as string;

                if (!string.IsNullOrEmpty(targetFieldValueString))
                {
                    string fixedTargetFieldValue;
                    if (targetFieldValueString.Contains('<'))
                    {
                        int index = targetFieldValueString.IndexOf('<');
                        fixedTargetFieldValue = targetFieldValueString.Substring(index);
                    }
                    else
                    {
                        fixedTargetFieldValue = Regex.Replace(targetFieldValueString, @"[\p{C}\p{S}]*", "");
                    }

                    KeyValuePair<string, object> targetFieldNoSanta = new KeyValuePair<string, object>(targetField.Key, fixedTargetFieldValue);
                    targetField = targetFieldNoSanta;
                }
            }

            return targetField;
        }

        /// <summary>
        /// Returns a JsonPatchOperation with any inline image urls in the html content replaced to point to the appropriate stored attachment on the target.
        /// </summary>
        /// <param name="htmlField"></param>
        /// <returns></returns>
        private string GetUpdatedHtmlField(string htmlFieldValue)
        {
            HashSet<string> inlineImageUrls = MigrationHelpers.GetInlineImageUrlsFromField(htmlFieldValue, migrationContext.SourceClient.Connection.Uri.AbsoluteUri);

            foreach (string inlineImageUrl in inlineImageUrls)
            {
                if (batchContext.SourceInlineImageUrlToTargetInlineImageGuid.ContainsKey(inlineImageUrl))
                {
                    string newValue = BuildTargetInlineImageUrl(inlineImageUrl, batchContext.SourceInlineImageUrlToTargetInlineImageGuid[inlineImageUrl]);
                    htmlFieldValue = htmlFieldValue.Replace(inlineImageUrl, newValue);
                }
            }

            return htmlFieldValue;
        }

        private string BuildTargetInlineImageUrl(string sourceInlineImageUrl, string targetInlineImageGuid)
        {
            string sourceAccount = migrationContext.Config.SourceConnection.Account;
            string targetAccount = migrationContext.Config.TargetConnection.Account;
            string result = sourceInlineImageUrl.Replace(sourceAccount, targetAccount);
            return MigrationHelpers.ReplaceAttachmentUrlGuid(result, targetInlineImageGuid);
        }

        protected KeyValuePair<string, object> UpdateProjectNameIfNeededForField(WorkItem sourceWorkItem, KeyValuePair<string, object> sourceField)
        {
            if (FieldRequiresProjectNameUpdate(sourceField.Key))
            {
                // do the check so we know which of the 3 it is here
                return CreateTargetField(sourceWorkItem, sourceField);
            }
            else
            {
                return sourceField;
            }
        }

        protected KeyValuePair<string, object> CreateTargetField(WorkItem sourceWorkItem, KeyValuePair<string, object> sourceField)
        {
            KeyValuePair<string, object> targetField = new KeyValuePair<string, object>();
            string targetProject = migrationContext.Config.TargetConnection.Project;
            string sourceProject = migrationContext.Config.SourceConnection.Project;

            string defaultAreaPath = string.IsNullOrEmpty(migrationContext.Config.DefaultAreaPath) ? targetProject : migrationContext.Config.DefaultAreaPath;
            string defaultIterationPath = string.IsNullOrEmpty(migrationContext.Config.DefaultIterationPath) ? targetProject : migrationContext.Config.DefaultIterationPath;

            // Make sure the new area path and iteration path exist on target before assigning them.
            // Otherwise assign targetProject
            if (sourceField.Key.Equals(FieldNames.AreaPath, StringComparison.OrdinalIgnoreCase))
            {
                string targetPathName = GetTargetPathName(sourceField.Value as string, sourceProject, targetProject);

                if (ExistsInTargetAreaPathList(targetPathName))
                {
                    targetField = new KeyValuePair<string, object>(sourceField.Key, targetPathName);
                }
                else
                {
                    targetField = new KeyValuePair<string, object>(sourceField.Key, defaultAreaPath);
                    Logger.LogWarning(LogDestination.File, $"Could not find corresponding AreaPath: {targetPathName} on target. Assigning the AreaPath: {defaultAreaPath} on source work item with Id: {sourceWorkItem.Id}.");
                }
            }
            else if (sourceField.Key.Equals(FieldNames.IterationPath, StringComparison.OrdinalIgnoreCase))
            {
                string targetPathName = GetTargetPathName(sourceField.Value as string, sourceProject, targetProject);

                if (ExistsInTargetIterationPathList(targetPathName))
                {
                    targetField = new KeyValuePair<string, object>(sourceField.Key, targetPathName);
                }
                else
                {
                    targetField = new KeyValuePair<string, object>(sourceField.Key, defaultIterationPath);
                    Logger.LogWarning(LogDestination.File, $"Could not find corresponding IterationPath: {targetPathName} on target. Assigning the IterationPath: {defaultIterationPath} on source work item with Id: {sourceWorkItem.Id}.");
                }
            }
            else if (sourceField.Key.Equals(FieldNames.TeamProject, StringComparison.OrdinalIgnoreCase))
            {
                targetField = new KeyValuePair<string, object>(sourceField.Key, targetProject);
            }

            return targetField;
        }

        public string GetTargetPathName(string fieldValue, string sourceProject, string targetProject)
        {
            return AreaAndIterationPathTree.ReplaceLeadingProjectName(fieldValue, sourceProject, targetProject);
        }

        public bool ExistsInTargetAreaPathList(string areaPath)
        {
            return migrationContext.TargetAreaPaths.Any(a => a.Equals(areaPath, StringComparison.OrdinalIgnoreCase));
        }

        public bool ExistsInTargetIterationPathList(string iterationPath)
        {
            return migrationContext.TargetIterationPaths.Any(a => a.Equals(iterationPath, StringComparison.OrdinalIgnoreCase));
        }

        public bool FieldRequiresProjectNameUpdate(string fieldName)
        {
            return migrationContext.FieldsThatRequireSourceProjectToBeReplacedWithTargetProject.Any(a => a.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsFieldUnsupported(string fieldRefName)
        {
            return migrationContext.UnsupportedFields.Any(a => fieldRefName.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// returns true if fieldName exists within workItemType on target ignoring case of strings.
        /// </summary>
        /// <param name="sourceFieldName"></param>
        /// <param name="sourceWorkItemType"></param>
        /// <returns></returns>
        public bool FieldIsWithinType(string sourceFieldName, string sourceWorkItemType)
        {
            var targetFieldName = GetTargetFieldName(sourceFieldName);
            ISet<string> fieldsOfKey = migrationContext.WorkItemTypes.First(a => a.Key.Equals(sourceWorkItemType, StringComparison.OrdinalIgnoreCase)).Value;
            return fieldsOfKey.Any(a => a.Equals(targetFieldName, StringComparison.OrdinalIgnoreCase));
        }

        public string GetWorkItemTypeFromWorkItem(WorkItem sourceWorkItem)
        {
            return sourceWorkItem.Fields[FieldNames.WorkItemType] as string;
        }

        public JsonPatchOperation GetInsertBatchIdAddOperation()
        {
            JsonPatchOperation jsonPatchOperation = new JsonPatchOperation();
            jsonPatchOperation.Operation = Operation.Add;
            jsonPatchOperation.Path = "/id";
            jsonPatchOperation.Value = IdWithinBatch;

            return jsonPatchOperation;
        }
    }
}
