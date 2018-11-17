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
    public static class HttpNodePipeline
    {
        private static ILogger _log;
        private static String _githubURL = "https://github.com/patricklee2/node-ci.git";
        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        [FunctionName("HttpNodePipeline")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpNodePipeline started");
            await InitUtils(log);

            LogInfo("HttpNodePipeline request received");

            String requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            List<String> newTags = data?.newTags.ToObject<List<String>>();

            if (newTags == null)
            {
                LogInfo("Failed: missing parameters `newTags` in body");
                await _mailUtils.SendFailureMail("Failed: missing parameters `newTags` in body", GetLog());
                return new BadRequestObjectResult("Failed: missing parameters `newTags` in body");
            }
            else if (newTags.Count == 0)
            {
                LogInfo("no new node tags found");
                await _mailUtils.SendSuccessMail(newTags, GetLog());
                return (ActionResult)new OkObjectResult($"no new node tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpNodePipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new node tags found {0}", String.Join(", ", newTags)));
                
                    List<String> newVersions = MakePipeline(newTags, log);
                    await _mailUtils.SendSuccessMail(newVersions, GetLog());
                    return (ActionResult)new OkObjectResult($"built new node images: {String.Join(", ", newVersions)}");
                }
                catch (Exception e)
                {
                    LogInfo(e.ToString());
                    _telemetry.TrackException(e);
                    await _mailUtils.SendFailureMail(e.ToString(), GetLog());
                    return new InternalServerErrorResult();
                }
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
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Node");
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

        public static List<string> MakePipeline(List<String> newTags, ILogger log)
        {
            List<String> newVersions = new List<String>();
            foreach (String t in newTags)
            {
                newVersions.Add(t.Replace("-alpine", ""));
            }

            foreach (String version in newVersions)
            {
                int tries = 3;
                while (true)
                {
                    try
                    {
                        tries--;
                        _mailUtils._version = version;
                        PushGithub(version);
                        CreateNodePipeline(version);
                        LogInfo(String.Format("node {0} built", version));
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("node {0} failed", version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }

        public static void CreateNodePipeline(String version)
        {
            LogInfo("creating pipeling for node hostingstart " + version);

            CreateNodeHostingStartPipeline(version);
            CreateNodeAppPipeline(version);
        }

        public static void CreateNodeHostingStartPipeline(String version)
        {
            String nodeVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-node-hostingstart-{0}-task", nodeVersionDash);
            String appName = String.Format("appsvcbuild-node-hostingstart-{0}-site", nodeVersionDash);
            String webhookName = String.Format("appsvcbuildnodehostingstart{0}wh", version.Replace(".", ""));
            String gitPath = String.Format("{0}#master:{1}", _githubURL, version);
            String imageName = String.Format("node:{0}", version);

            LogInfo("creating acr task for node hostingstart " + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, gitPath, _secretsUtils._gitToken, imageName);
            LogInfo("creating webapp for node hostingstart " + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        public static void CreateNodeAppPipeline(String version)
        {
            String nodeVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-node-app-{0}-task", nodeVersionDash);
            String appName = String.Format("appsvcbuild-node-app-{0}-site", nodeVersionDash);
            String webhookName = String.Format("appsvcbuildnodeapp{0}wh", version.Replace(".", ""));
            String gitPath = String.Format("{0}#master:nodeApp-{1}", _githubURL, version);
            String imageName = String.Format("nodeapp:{0}", version);

            LogInfo("creating acr task for node app" + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, gitPath, _secretsUtils._gitToken, imageName);
            LogInfo("creating webapp for node app " + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        private static void PushGithub(String version)
        {
            LogInfo("creating github files for node " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);
            String localRepo = String.Format("{0}\\node-ci", parent);

            _githubUtils.Clone(_githubURL, localRepo);
            _githubUtils.FillTemplate(
                localRepo,
                String.Format("{0}\\template", localRepo),
                String.Format("{0}\\{1}", localRepo, version),
                String.Format("{0}\\{1}\\DockerFile", localRepo, version),
                String.Format("FROM node:{0}-alpine\n", version),
                false);
            _githubUtils.FillTemplate(
                localRepo,
                String.Format("{0}\\nodeAppTemplate", localRepo),
                String.Format("{0}\\nodeApp-{1}", localRepo, version),
                String.Format("{0}\\nodeApp-{1}\\DockerFile", localRepo, version),
                String.Format("FROM appsvcbuildacr.azurecr.io/node:{0}\n", version),
                false);
            _githubUtils.Stage(localRepo, String.Format("{0}\\{1}", localRepo, version));
            _githubUtils.Stage(localRepo, String.Format("{0}\\nodeApp-{1}", localRepo, version));
            _githubUtils.CommitAndPush(_githubURL, localRepo, String.Format("[appsvcbuild] Add node {0}", version));
            //_githubUtils.CleanUp(parent);
        }
    }
}