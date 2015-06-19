﻿using System;
using System.IO;

namespace EQueue.Protocols
{
    [Serializable]
    public class PullMessageRequest
    {
        public string ConsumerGroup { get; set; }
        public MessageQueue MessageQueue { get; set; }
        public long QueueOffset { get; set; }
        public int PullMessageBatchSize { get; set; }
        public long SuspendPullRequestMilliseconds { get; set; }
        public ConsumeFromWhere ConsumeFromWhere { get; set; }

        public static void WriteToStream(PullMessageRequest request, Stream stream)
        {
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(request.ConsumerGroup);
                writer.Write(request.MessageQueue.Topic);
                writer.Write(request.MessageQueue.QueueId);
                writer.Write(request.QueueOffset);
                writer.Write(request.PullMessageBatchSize);
                writer.Write(request.SuspendPullRequestMilliseconds);
                writer.Write((int)request.ConsumeFromWhere);
            }
        }
        public static PullMessageRequest ReadFromStream(Stream stream)
        {
            using (var reader = new BinaryReader(stream))
            {
                var request = new PullMessageRequest();
                request.ConsumerGroup = reader.ReadString();
                request.MessageQueue = new MessageQueue(reader.ReadString(), reader.ReadInt32());
                request.QueueOffset = reader.ReadInt64();
                request.PullMessageBatchSize = reader.ReadInt32();
                request.SuspendPullRequestMilliseconds = reader.ReadInt64();
                request.ConsumeFromWhere = (ConsumeFromWhere)reader.ReadInt32();
                return request;
            }
        }
    }
}
