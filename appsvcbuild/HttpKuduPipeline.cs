using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Azure.WebJobs.Host;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Rest.Azure;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.Storage;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.ContainerRegistry.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.KeyVault;
using System.Xml.Linq;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Text.RegularExpressions;
using System.Web.Http;
using Microsoft.ApplicationInsights;

namespace appsvcbuild
{
    public static class HttpKuduPipeline
    {
        private static ILogger _log;
        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        public static async Task<String> Run(BuildRequest br, ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpKuduPipeline started");
            await InitUtils(log);

            LogInfo("HttpKuduPipeline request received");

            try
            {
                _mailUtils._buildRequest = br;
                LogInfo($"HttpKuduPipeline executed at: { DateTime.Now }");
                LogInfo(String.Format("new Kudu BuildRequest found {0}", br.ToString()));

                Boolean success = await MakePipeline(br, log);
                await _mailUtils.SendSuccessMail(new List<String> { br.Version }, GetLog());
                String successMsg =
                    $@"{{
                        ""status"": ""success"",
                        ""image"": ""appsvcbuildacr.azurecr.io/{br.OutputImageName}"",
                        ""webApp"": ""https://{br.WebAppName}.azurewebsites.net""
                    }}";
                return successMsg;
            }
            catch (Exception e)
            {
                LogInfo(e.ToString());
                _telemetry.TrackException(e);
                await _mailUtils.SendFailureMail(e.ToString(), GetLog());
                String failureMsg =
                    $@"{{
                        ""status"": ""failure"",
                        ""error"": ""{e.ToString()}""
                    }}";
                return failureMsg;
            }
        }

        public static void LogInfo(String message)
        {
            _emailLog.Append(message);
            _log.LogInformation(message);
            _telemetry.TrackEvent(message);
        }
        public static String GetLog()
        {
            return _emailLog.ToString();
        }

        public static async System.Threading.Tasks.Task InitUtils(ILogger log)
        {
            _emailLog = new StringBuilder();
            _secretsUtils = new SecretsUtils();
            await _secretsUtils.GetSecrets();
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Kudu");
            _dockerhubUtils = new DockerhubUtils();
            _githubUtils = new GitHubUtils(_secretsUtils._gitToken);
            _pipelineUtils = new PipelineUtils(
                new ContainerRegistryManagementClient(_secretsUtils._credentials),
                new WebSiteManagementClient(_secretsUtils._credentials),
                _secretsUtils._subId
                );

            _log = log;
            _mailUtils._log = log;
            _dockerhubUtils._log = log;
            _githubUtils._log = log;
            _pipelineUtils._log = log;
        }

        public static async Task<Boolean> MakePipeline(BuildRequest br, ILogger log)
        {
            int tries = 3;
            while (true)
            {
                try
                {
                    tries--;
                    _mailUtils._version = br.Version;
                    LogInfo("Creating pipeline for Kudu " + br.Version);
                    await PushGithubAsync(br);
                    await CreateKuduHostingStartPipeline(br);
                    LogInfo(String.Format("Kudu {0} built", br.Version));
                    return true;
                }
                catch (Exception e)
                {
                    LogInfo(e.ToString());
                    if (tries <= 0)
                    {
                        LogInfo(String.Format("Kudu {0} failed", br.Version));
                        throw e;
                    }
                    LogInfo("trying again");
                    System.Threading.Thread.Sleep(1 * 60 * 1000);  //1 min
                }
            }
        }

        public static async Task<Boolean> CreateKuduHostingStartPipeline(BuildRequest br)
        {
            String kuduVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-kudu-hostingstart-{0}-task", kuduVersionDash);

            LogInfo("creating acr task for kudu hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, _secretsUtils._gitToken, br.OutputImageName, _secretsUtils._pipelineToken);
            LogInfo("done creating acr task for kudu hostingstart " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAsync(BuildRequest br)
        {
            LogInfo("creating github files for kudu " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("F:\\home\\site\\wwwroot\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.TemplateRepoBranchName);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.OutputRepoOrgName, br.OutputRepoName))
            {
                _githubUtils.Clone(
                    br.OutputRepoURL,
                    localOutputRepoPath,
                    br.OutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.OutputRepoOrgName, br.OutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.OutputRepoOrgName, br.OutputRepoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath,
                false);

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.OutputRepoBranchName, String.Format("[appsvcbuild] Add kudu {0}", br.Version));
            //_githubUtils.CleanUp(parent);

            LogInfo("done creating github files for kudu " + br.Version);
            return true;
        }
    }
}
