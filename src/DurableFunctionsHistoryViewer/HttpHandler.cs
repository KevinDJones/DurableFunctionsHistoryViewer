﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DurableFunctionsHistoryViewer.Models;
using DurableFunctionsHistoryViewer.Tools;
using DurableFunctionsHistoryViewer.ViewModels;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using RazorLight;

namespace DurableFunctionsHistoryViewer
{
    internal class HttpHandler
    {
        private const string IndexAction = "/index";
        private const string DetailAction = "/detail";
        private const string InstanceIdParameter = "instanceid";
        private const string StartTimeParameter = "starttime";
        private const string EndTimeParameter = "endtime";
        private const string OrchestratorNameParameter = "orchestratorname";
        private const string CodeParameter = "code";
        private readonly CloudTableClient _tableClient;
        private readonly string _taskHubName;
        private readonly IRazorLightEngine _razorLightEngine;
        private readonly Dfhv _confg;
        public HttpHandler(CloudTableClient tableClient, string taskHubName, IRazorLightEngine razorLightEngine, Dfhv confg)
        {
            _tableClient = tableClient;
            _taskHubName = taskHubName;
            _razorLightEngine = razorLightEngine;
            _confg = confg;
        }

        public async Task<HttpResponseMessage> HandleRequestAsync(HttpRequestMessage request)
        {
            string path = request.RequestUri.AbsolutePath.TrimEnd('/');
            var param = GetParams(request);

            int i = path.IndexOf(IndexAction, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                // Retrive All Status in case of the request URL ends e.g. /instances/
                return await GetList(param.Code, param.StartTime, param.EndTime, param.OrchestratorName, request);
            }

            i = path.IndexOf(DetailAction, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 || (request.Method == HttpMethod.Get && path.EndsWith(DetailAction.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                // Retrive All Status in case of the request URL ends e.g. /detail/
                if (string.IsNullOrEmpty(param.InstanceId))
                {
                    return request.CreateResponse(HttpStatusCode.NotFound);
                }

                return await GetDetail(param.Code, param.InstanceId, request);
            }

            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        private string GetDetailUrl(string instanceId, HttpRequestMessage request)
        {
            var hostUrl = request.RequestUri.GetLeftPart(UriPartial.Authority);
            var url = hostUrl + _confg.NotificationUrl.AbsolutePath.TrimEnd('/');
            url += DetailAction;
            var query = $"{InstanceIdParameter}={WebUtility.UrlEncode(instanceId)}";
            if (!string.IsNullOrEmpty(_confg.NotificationUrl.Query))
            {
                // This is expected to include the auto-generated system key for this extension.
                query += "&" + _confg.NotificationUrl.Query.TrimStart('?');
            }
            url += $"?{query}";

            return url;
        }

        public async Task<HttpResponseMessage> GetDetail(string code, string instanceId, HttpRequestMessage request)
        {
            var table = _tableClient.GetTableReference($"{_taskHubName}History");
            if (!await table.ExistsAsync())
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            var entities = (await table.GetByPartition<HistoryTableEntity>(instanceId))
                .OrderBy(x=> x.RowKey)
                .Select(x=> new HistoryItemViewModel
                {
                    Detail = x.Detail,
                    EventId = x.EventId,
                    EventType = x.EventType,
                    ExecutionId = x.ExecutionId,
                    Input = x.Input,
                    IsPlayed = x.IsPlayed,
                    Name = x.Name,
                    OrchestrationInstance = x.OrchestrationInstance,
                    OrchestrationStatus = x.OrchestrationStatus,
                    Reason = x.Reason,
                    Result = x.Result,
                    TaskScheduledId = x.TaskScheduledId,
                    Row = x.RowKey
                })
                .ToList();
            var vm = new HistoryViewModel
            {
                List = entities
            };
            var html = await _razorLightEngine.CompileRenderAsync(
                "Views.Detail.cshtml", vm);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        }

        public async Task<HttpResponseMessage> GetList(string code, DateTimeOffset? startTime, DateTimeOffset? endTime, string orchestratorName, HttpRequestMessage request)
        {
            var table = _tableClient.GetTableReference($"{_taskHubName}Instances");
            if (!await table.ExistsAsync())
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            var query = default(TableQuery<InstanceTableEntity>);

            if (startTime.HasValue && endTime.HasValue)
            {
                var start = TableQuery.GenerateFilterConditionForDate("Timestamp", QueryComparisons.GreaterThanOrEqual, startTime.Value);
                var end = TableQuery.GenerateFilterConditionForDate("Timestamp", QueryComparisons.LessThanOrEqual, endTime.Value);
                var where = TableQuery.CombineFilters(start, TableOperators.And, end);
                if (!string.IsNullOrEmpty(orchestratorName))
                {
                    var nameQuery = TableQuery.GenerateFilterCondition("Name", QueryComparisons.Equal, orchestratorName);
                    where = TableQuery.CombineFilters(where, TableOperators.And, nameQuery);
                }

                query = new TableQuery<InstanceTableEntity>()
                        .Where(where)
                    ;
            }
            else if (startTime.HasValue)
            {
                var start = TableQuery.GenerateFilterConditionForDate("Timestamp", QueryComparisons.GreaterThanOrEqual, startTime.Value);
                if (!string.IsNullOrEmpty(orchestratorName))
                {
                    var nameQuery = TableQuery.GenerateFilterCondition("Name", QueryComparisons.Equal, orchestratorName);
                    start = TableQuery.CombineFilters(start, TableOperators.And, nameQuery);
                }
                query = new TableQuery<InstanceTableEntity>().Where(start);
            }
            else if (endTime.HasValue)
            {
                var end = TableQuery.GenerateFilterConditionForDate("Timestamp", QueryComparisons.LessThanOrEqual, endTime.Value);
                if (!string.IsNullOrEmpty(orchestratorName))
                {
                    var nameQuery = TableQuery.GenerateFilterCondition("Name", QueryComparisons.Equal, orchestratorName);
                    end = TableQuery.CombineFilters(end, TableOperators.And, nameQuery);
                }
                query = new TableQuery<InstanceTableEntity>().Where(end);
            }
            else
            {
                if (!string.IsNullOrEmpty(orchestratorName))
                {
                    var nameQuery = TableQuery.GenerateFilterCondition("Name", QueryComparisons.Equal, orchestratorName);
                    query = new TableQuery<InstanceTableEntity>().Where(nameQuery);
                }
                else
                {
                    query = new TableQuery<InstanceTableEntity>();
                }                
            }            
            
            var instances = await table
                .Query<InstanceTableEntity>(query);

            var vm = new IndexViewModel()
            {
                Code = code,
                StartTime = startTime?.ToString("yyyy-MM-ddTHH:mm:ss"),
                EndTime = endTime?.ToString("yyyy-MM-ddTHH:mm:ss"),
                List = instances.Select(x => new IndexItemViewModel()
                {
                    InstanceId = x.PartitionKey,
                    CreatedTime = x.CreatedTime,
                    ExecutionId = x.ExecutionId,
                    Input = x.Input,
                    LastUpdatedTime = x.LastUpdatedTime,
                    Name = x.Name,
                    RuntimeStatus = x.RuntimeStatus,
                    Version = x.Version,
                    DetailUrl = GetDetailUrl(x.PartitionKey, request)
                }).ToList()
            };
          var html = await _razorLightEngine.CompileRenderAsync(
                "Views.Index.cshtml", vm);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };            
        }

        private (DateTimeOffset? StartTime, DateTimeOffset? EndTime, string InstanceId, string OrchestratorName, string Code) GetParams(HttpRequestMessage request)
        {
            string instanceId = null;
            DateTimeOffset? starttime = null;
            DateTimeOffset? endtime = null;
            string orchestratorName = null;
            string code = null;

            var pairs = request.GetQueryNameValuePairs();
            foreach (var key in pairs.AllKeys)
            {
                if (instanceId == null
                         && key.Equals(InstanceIdParameter, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(pairs[key]))
                {
                    instanceId = pairs[key];
                }
                else if (starttime == null
                         && key.Equals(StartTimeParameter, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(pairs[key]))
                {
                    if (DateTimeOffset.TryParse(pairs[key], out var time))
                    {
                        starttime = time;
                    }                    
                }
                else if (endtime == null
                         && key.Equals(EndTimeParameter, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(pairs[key]))
                {
                    if (DateTimeOffset.TryParse(pairs[key], out var time))
                    {
                        endtime = time;
                    }
                }
                else if (orchestratorName == null
                         && key.Equals(OrchestratorNameParameter, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(pairs[key]))
                {
                    orchestratorName = pairs[key];
                }
                else if (code == null
                         && key.Equals(CodeParameter, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(pairs[key]))
                {
                    code = pairs[key];
                }
            }

            return (starttime,endtime, instanceId, orchestratorName, code);
        }
    }
}
