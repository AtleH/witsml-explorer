using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Witsml;
using Witsml.Data;
using Witsml.ServiceReference;
using WitsmlExplorer.Api.Jobs;
using WitsmlExplorer.Api.Jobs.Common;
using WitsmlExplorer.Api.Models;
using WitsmlExplorer.Api.Query;
using WitsmlExplorer.Api.Services;
using Index = Witsml.Data.Curves.Index;

namespace WitsmlExplorer.Api.Workers
{
    public class RenameMnemonicWorker: BaseWorker<RenameMnemonicJob>, IWorker
    {
        private readonly IWitsmlClient witsmlClient;
        public JobType JobType => JobType.RenameMnemonic;

        public RenameMnemonicWorker(IWitsmlClientProvider witsmlClientProvider)
        {
            witsmlClient = witsmlClientProvider.GetClient();
        }

        public override async Task<(WorkerResult, RefreshAction)> Execute(RenameMnemonicJob job)
        {
            Verify(job);

            var logHeader = await GetLog(job.LogReference);

            var mnemonics = GetMnemonics(logHeader, job.Mnemonic);
            var startIndex = Index.Start(logHeader);
            var endIndex = Index.End(logHeader);

            while (startIndex < endIndex)
            {
                var logData = await GetLogData(logHeader, mnemonics, startIndex, endIndex);
                if (logData == null) break;

                var updatedWitsmlLog = CreateNewLogWithRenamedMnemonic(job, logHeader, mnemonics, logData);

                var result = await witsmlClient.UpdateInStoreAsync(updatedWitsmlLog);
                if (!result.IsSuccessful)
                {
                    Log.Error($"Failed to rename mnemonic from {job.Mnemonic} to {job.NewMnemonic} for " +
                              $"UidWell: {job.LogReference.WellUid}, UidWellbore: {job.LogReference.WellboreUid}, UidLog: {job.LogReference.LogUid}.");
                    return (new WorkerResult(witsmlClient.GetServerHostname(), false, $"Failed to rename Mnemonic from {job.Mnemonic} to {job.NewMnemonic}", result.Reason), null);
                }

                startIndex = Index.End(logData).AddEpsilon();
            }

            var resultDeleteOldMnemonic = await DeleteOldMnemonic(job.LogReference.WellUid, job.LogReference.WellboreUid, job.LogReference.LogUid, job.Mnemonic);
            if (!resultDeleteOldMnemonic.IsSuccess)
            {
                return (resultDeleteOldMnemonic, null);
            }

            Log.Information($"{GetType().Name} - Job successful. Mnemonic renamed from {job.Mnemonic} to {job.NewMnemonic}");
            return (
                new WorkerResult(witsmlClient.GetServerHostname(), true, $"Mnemonic renamed from {job.Mnemonic} to {job.NewMnemonic}"),
                new RefreshLogObject(witsmlClient.GetServerHostname(), job.LogReference.WellUid, job.LogReference.WellboreUid, job.LogReference.LogUid, RefreshType.Update));
        }

        private static WitsmlLogs CreateNewLogWithRenamedMnemonic(RenameMnemonicJob job, WitsmlLog logHeader, IEnumerable<string> mnemonics, WitsmlLog logData)
        {
            var updatedData = new WitsmlLog
            {
                UidWell = logHeader.UidWell,
                UidWellbore = logHeader.UidWellbore,
                Uid = logHeader.Uid,
                LogCurveInfo = logHeader.LogCurveInfo.Where(lci => mnemonics.Contains(lci.Mnemonic, StringComparer.OrdinalIgnoreCase)).ToList(),
                LogData = logData.LogData
            };
            updatedData.LogCurveInfo.Find(c => c.Mnemonic == job.Mnemonic)!.Mnemonic = job.NewMnemonic;
            updatedData.LogData.MnemonicList = string.Join(",", GetMnemonics(logHeader, job.NewMnemonic));

            return new WitsmlLogs
            {
                Logs = new List<WitsmlLog> {updatedData}
            };
        }

        private async Task<WitsmlLog> GetLogData(WitsmlLog log, IEnumerable<string> mnemonics, Index startIndex, Index endIndex)
        {
            var query = LogQueries.GetLogContent(
                log.UidWell,
                log.UidWellbore,
                log.Uid,
                log.IndexType,
                mnemonics,
                startIndex, endIndex);

            var data = await witsmlClient.GetFromStoreAsync(query, new OptionsIn(ReturnElements.DataOnly));
            return data.Logs.Any() ? data.Logs.First() : null;
        }

        private async Task<WorkerResult> DeleteOldMnemonic(string wellUid, string wellboreUid, string logUid, string mnemonic)
        {
            var query = LogQueries.DeleteMnemonics(wellUid, wellboreUid, logUid, new[] {mnemonic});
            var result = await witsmlClient.DeleteFromStoreAsync(query);
            if (result.IsSuccessful)
            {
                return new WorkerResult(witsmlClient.GetServerHostname(), true, $"Successfully deleted old mnemonic");
            }

            Log.Error("Failed to delete old mnemonic. WellUid: {WellUid}, WellboreUid: {WellboreUid}, Uid: {LogUid}, Mnemonic: {Mnemonic}", wellUid, wellboreUid, logUid, mnemonic);
            return new WorkerResult(witsmlClient.GetServerHostname(), false, $"Failed to delete old mnemonic {mnemonic} while renaming mnemonic", result.Reason);
        }

        private static void Verify(RenameMnemonicJob job)
        {
            if (string.IsNullOrEmpty(job.Mnemonic) || string.IsNullOrEmpty(job.NewMnemonic))
            {
                throw new InvalidOperationException("Empty name given when trying to rename a mnemonic. Make sure valid names are given");
            }
            else if (job.Mnemonic.Equals(job.NewMnemonic))
            {
                throw new InvalidOperationException("Cannot rename a mnemonic to the same name it already has. Make sure new mnemonic is a unique name");
            }
        }

        private static IReadOnlyCollection<string> GetMnemonics(WitsmlLog log, string mnemonic)
        {
            return new List<string>
            {
                log.IndexCurve.Value,
                mnemonic
            };
        }

        private async Task<WitsmlLog> GetLog(LogReference logReference)
        {
            var logQuery = LogQueries.GetWitsmlLogById(logReference.WellUid, logReference.WellboreUid, logReference.LogUid);
            var logs = await witsmlClient.GetFromStoreAsync(logQuery, new OptionsIn(ReturnElements.HeaderOnly));
            return !logs.Logs.Any() ? null : logs.Logs.First();
        }
    }
}

