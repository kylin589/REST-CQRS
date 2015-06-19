﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Autofac;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.JsonNet;
using ECommon.Log4Net;
using ECommon.Logging;
using ECommon.Utilities;
using EQueue.Clients.Producers;
using EQueue.Configurations;
using EQueue.Protocols;
using ECommonConfiguration = ECommon.Configurations.Configuration;

namespace QuickStart.ProducerClient
{
    class Program
    {
        static int finished;
        static int messageIndex;
        static Stopwatch watch;

        static void Main(string[] args)
        {
            InitializeEQueue();

            var brokerIP = ConfigurationManager.AppSettings["BrokerIP"];
            var brokerEndPoint = string.IsNullOrEmpty(brokerIP) ? new IPEndPoint(SocketUtils.GetLocalIPV4(), 5000) : new IPEndPoint(IPAddress.Parse(brokerIP), 5000);
            var producer = new Producer("Producer1", new ProducerSetting { BrokerProducerIPEndPoint = brokerEndPoint }).Start();
            var messageSize = int.Parse(ConfigurationManager.AppSettings["MessageSize"]);
            var messageCount = int.Parse(ConfigurationManager.AppSettings["MessageCount"]);
            var message = new byte[messageSize];
            var sendCallback = new Action<Task<SendResult>>(sendTask =>
            {
                if (sendTask.Exception != null)
                {
                    _logger.ErrorFormat("Sent message failed, errorMessage: {0}", sendTask.Exception.GetBaseException().Message);
                    return;
                }
                if (sendTask.Result.SendStatus == SendStatus.Success)
                {
                    var finishedCount = Interlocked.Increment(ref finished);
                    if (finishedCount == 1)
                    {
                        watch = Stopwatch.StartNew();
                    }
                    if (finishedCount % 10000 == 0)
                    {
                        _logger.InfoFormat("Sent {0} messages, time spent:{1}", finishedCount, watch.ElapsedMilliseconds);
                    }
                }
                else
                {
                    _logger.ErrorFormat("Sent message failed, errorMessage: {0}", sendTask.Result.ErrorMessage);
                }
            });

            for (var index = 1; index <= messageCount; index++)
            {
                producer.SendAsync(new Message("SampleTopic", 100, message), Interlocked.Increment(ref messageIndex)).ContinueWith(sendCallback);
            }

            Console.ReadLine();
        }

        static ILogger _logger;
        static void InitializeEQueue()
        {
            ECommonConfiguration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterEQueueComponents()
                .SetDefault<IQueueSelector, QueueAverageSelector>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create("Program");
        }
    }
}
