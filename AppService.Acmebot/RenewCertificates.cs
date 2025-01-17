﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class RenewCertificates
    {
        [FunctionName("RenewCertificates")]
        public async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var proxy = context.CreateActivityProxy<ISharedFunctions>();

            // 期限切れまで 30 日以内の証明書を取得する
            var certificates = await proxy.GetCertificates(context.CurrentUtcDateTime);

            foreach (var certificate in certificates)
            {
                log.LogInformation($"{certificate.SubjectName} - {certificate.ExpirationDate}");
            }

            // 更新対象となる証明書がない場合は終わる
            if (certificates.Count == 0)
            {
                log.LogInformation("Certificates are not found");

                return;
            }

            // App Service を取得
            var sites = await proxy.GetSites();

            var tasks = new List<Task>();

            // サイト単位で証明書の更新を行う
            foreach (var site in sites)
            {
                // 期限切れが近い証明書がバインドされているか確認
                var boundCertificates = certificates.Where(x => site.HostNameSslStates.Any(xs => xs.Thumbprint == x.Thumbprint))
                                                    .ToArray();

                // 対象となる証明書が存在しない場合はスキップ
                if (boundCertificates.Length == 0)
                {
                    continue;
                }

                // 証明書の更新処理を開始
                tasks.Add(context.CallSubOrchestratorAsync(nameof(RenewSiteCertificates), (site, boundCertificates)));
            }

            // サブオーケストレーターの完了を待つ
            await Task.WhenAll(tasks);
        }

        [FunctionName(nameof(RenewSiteCertificates))]
        public async Task RenewSiteCertificates([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var (site, certificates) = context.GetInput<(Site, Certificate[])>();

            var proxy = context.CreateActivityProxy<ISharedFunctions>();

            log.LogInformation($"Site name: {site.Name}");

            foreach (var certificate in certificates)
            {
                log.LogInformation($"Subject name: {certificate.SubjectName}");

                // ワイルドカード、コンテナ、Linux の場合は DNS-01 を利用する
                var useDns01Auth = certificate.HostNames.Any(x => x.StartsWith("*")) || site.Kind.Contains("container") || site.Kind.Contains("linux");

                // 前提条件をチェック
                if (useDns01Auth)
                {
                    await proxy.Dns01Precondition(certificate.HostNames);
                }
                else
                {
                    await proxy.Http01Precondition(site);
                }

                // 新しく ACME Order を作成する
                var orderDetails = await proxy.Order(certificate.HostNames);

                // 複数の Authorizations を処理する
                var challenges = new List<ChallengeResult>();

                foreach (var authorization in orderDetails.Payload.Authorizations)
                {
                    ChallengeResult result;

                    // ACME Challenge を実行
                    if (useDns01Auth)
                    {
                        result = await proxy.Dns01Authorization((authorization, context.ParentInstanceId ?? context.InstanceId));

                        // Azure DNS で正しくレコードが引けるか確認
                        await proxy.CheckDnsChallenge(result);
                    }
                    else
                    {
                        result = await proxy.Http01Authorization((site, authorization));

                        // HTTP で正しくアクセスできるか確認
                        await proxy.CheckHttpChallenge(result);
                    }

                    challenges.Add(result);
                }

                // ACME Answer を実行
                await proxy.AnswerChallenges(challenges);

                // Order のステータスが ready になるまで 60 秒待機
                await proxy.CheckIsReady(orderDetails);

                // Order の最終処理を実行し PFX を作成
                var (thumbprint, pfxBlob) = await proxy.FinalizeOrder((certificate.HostNames, orderDetails));

                await proxy.UpdateCertificate((site, $"{certificate.HostNames[0]}-{thumbprint}", pfxBlob));

                foreach (var hostNameSslState in site.HostNameSslStates.Where(x => certificate.HostNames.Contains(x.Name)))
                {
                    hostNameSslState.Thumbprint = thumbprint;
                    hostNameSslState.ToUpdate = true;
                }
            }

            await proxy.UpdateSiteBinding(site);
        }

        [FunctionName("RenewCertificates_Timer")]
        public async Task TimerStart([TimerTrigger("0 0 0 * * 1,3,5")] TimerInfo timer, [OrchestrationClient] DurableOrchestrationClient starter, ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("RenewCertificates", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}