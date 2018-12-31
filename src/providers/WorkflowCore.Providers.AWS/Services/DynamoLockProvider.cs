﻿using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkflowCore.Interface;

namespace WorkflowCore.Providers.AWS.Services
{
    public class DynamoLockProvider : IDistributedLockProvider
    {
        private readonly ILogger _logger;
        private readonly AmazonDynamoDBClient _client;
        private readonly string _tableName;
        private readonly string _nodeId;    
        private readonly long _ttl = 30000;
        private readonly int _heartbeat = 10000;
        private readonly long _jitter = 1000;
        private readonly List<string> _localLocks;
        private Task _heartbeatTask;
        private CancellationTokenSource _cancellationTokenSource;

        public DynamoLockProvider(AWSCredentials credentials, AmazonDynamoDBConfig config, string tableName, ILoggerFactory logFactory)
        {
            _logger = logFactory.CreateLogger<DynamoLockProvider>();
            _client = new AmazonDynamoDBClient(credentials, config);
            _localLocks = new List<string>();
            _tableName = tableName;
            _nodeId = Guid.NewGuid().ToString();
        }

        public async Task<bool> AcquireLock(string Id, CancellationToken cancellationToken)
        {
            try
            {
                var req = new PutItemRequest()
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "id", new AttributeValue(Id) },
                        { "lockOwner", new AttributeValue(_nodeId) },
                        { "expires", new AttributeValue()
                            {
                                N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() + _ttl)
                            }
                        }
                    },
                    ConditionExpression = "attribute_not_exists(id) OR (expires < :expired)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":expired", new AttributeValue()
                            {
                                N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() + _jitter)
                            }
                        }
                    }
                };

                var response = await _client.PutItemAsync(req, _cancellationTokenSource.Token);                                

                if (response.HttpStatusCode == System.Net.HttpStatusCode.OK)
                {
                    _localLocks.Add(Id);
                    return true;
                }
            }
            catch (ConditionalCheckFailedException)
            {
            }
            return false;
        }

        public async Task ReleaseLock(string Id)
        {
            _localLocks.Remove(Id);
            try
            {
                var req = new DeleteItemRequest()
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "id", new AttributeValue(Id) }
                    },
                    ConditionExpression = "lockOwner = :nodeId",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":nodeId", new AttributeValue(_nodeId) }
                    }

                };
                await _client.DeleteItemAsync(req);
            }
            catch (ConditionalCheckFailedException)
            {     
            }
        }

        public async Task Start()
        {
            await EnsureTable();
            if (_heartbeatTask != null)
            {
                throw new InvalidOperationException();
            }

            _cancellationTokenSource = new CancellationTokenSource();

            _heartbeatTask = new Task(SendHeartbeat);
            _heartbeatTask.Start();
        }

        public Task Stop()
        {
            _cancellationTokenSource.Cancel();
            _heartbeatTask.Wait();
            _heartbeatTask = null;
            return Task.CompletedTask;
        }

        private async void SendHeartbeat()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_heartbeat, _cancellationTokenSource.Token);
                    foreach (var item in _localLocks)
                    {
                        var req = new PutItemRequest
                        {
                            TableName = _tableName,
                            Item = new Dictionary<string, AttributeValue>
                        {
                            { "id", new AttributeValue(item) },
                            { "lockOwner", new AttributeValue(_nodeId) },
                            { "expires", new AttributeValue()
                                {
                                    N = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() + _ttl)
                                }
                            }
                        },
                            ConditionExpression = "lockOwner = :nodeId",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            { ":nodeId", new AttributeValue(_nodeId) }
                        }
                        };

                        await _client.PutItemAsync(req, _cancellationTokenSource.Token);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(default(EventId), ex, ex.Message);
                }
            }
        }

        private async Task EnsureTable()
        {
            try
            {
                var poll = await _client.DescribeTableAsync(_tableName);
            }
            catch (ResourceNotFoundException)
            {
                await CreateTable();
            }
        }

        private async Task CreateTable()
        {
            var createRequest = new CreateTableRequest(_tableName, new List<KeySchemaElement>()
            {
                new KeySchemaElement("id", KeyType.HASH)
            })
            {
                AttributeDefinitions = new List<AttributeDefinition>()
                {
                    new AttributeDefinition("id", ScalarAttributeType.S)
                },
                BillingMode = BillingMode.PAY_PER_REQUEST
            };

            var createResponse = await _client.CreateTableAsync(createRequest);

            int i = 0;
            bool created = false;
            while ((i < 10) && (!created))
            {
                try
                {
                    await Task.Delay(1000);
                    var poll = await _client.DescribeTableAsync(_tableName, _cancellationTokenSource.Token);
                    created = (poll.Table.TableStatus == TableStatus.ACTIVE);
                    i++;
                }
                catch (ResourceNotFoundException)
                {
                }
            }
        }
    }
}