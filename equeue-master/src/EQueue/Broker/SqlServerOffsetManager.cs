﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using ECommon.Components;
using ECommon.Extensions;
using ECommon.Logging;
using ECommon.Scheduling;
using EQueue.Protocols;

namespace EQueue.Broker
{
    public class SqlServerOffsetManager : IOffsetManager
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _groupQueueOffsetDict = new ConcurrentDictionary<string, ConcurrentDictionary<string, long>>();
        private readonly IScheduleService _scheduleService;
        private readonly ILogger _logger;
        private readonly SqlServerOffsetManagerSetting _setting;
        private readonly string _getLatestVersionSQL;
        private readonly string _getLatestVersionQueueOffsetSQL;
        private readonly string _insertNewVersionQueueOffsetSQLFormat;
        private readonly string _deleteOldVersionQueueOffsetSQLFormat;
        private readonly string _selectQueueOffsetsSQLFormat;
        private long _currentVersion;
        private long _lastUpdateVersion;
        private long _lastPersistVersion;
        private int _isPersistingOffsets;
        private int _persistQueueOffsetTaskId;

        public SqlServerOffsetManager(SqlServerOffsetManagerSetting setting)
        {
            _setting = setting;
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
            _getLatestVersionSQL = "select max(Version) from [" + _setting.QueueOffsetTable + "]";
            _getLatestVersionQueueOffsetSQL = "select * from [" + _setting.QueueOffsetTable + "] where Version = {0}";
            _insertNewVersionQueueOffsetSQLFormat = "insert into [" + _setting.QueueOffsetTable + "] (Version,ConsumerGroup,Topic,QueueId,QueueOffset,Timestamp) values ({0},'{1}','{2}',{3},{4},'{5}')";
            _deleteOldVersionQueueOffsetSQLFormat = "delete from [" + _setting.QueueOffsetTable + "] where Version = {0}";
            _selectQueueOffsetsSQLFormat = "select Topic, QueueId, min(QueueOffset) as QueueOffset from [" + _setting.QueueOffsetTable + "] where Version = {0} group by Topic, QueueId";
        }

        public void Start()
        {
            Clear();
            RecoverQueueOffset();
            _persistQueueOffsetTaskId = _scheduleService.ScheduleTask("SqlServerOffsetManager.TryPersistQueueOffset", TryPersistQueueOffset, _setting.PersistQueueOffsetInterval, _setting.PersistQueueOffsetInterval);
        }
        public void Shutdown()
        {
            _scheduleService.ShutdownTask(_persistQueueOffsetTaskId);
        }

        public int GetConsumerGroupCount()
        {
            return _groupQueueOffsetDict.Count;
        }
        public long GetQueueOffset(string topic, int queueId, string group)
        {
            ConcurrentDictionary<string, long> queueOffsetDict;
            if (_groupQueueOffsetDict.TryGetValue(group, out queueOffsetDict))
            {
                long offset;
                var key = string.Format("{0}-{1}", topic, queueId);
                if (queueOffsetDict.TryGetValue(key, out offset))
                {
                    return offset;
                }
            }
            return -1L;
        }
        public long GetMinOffset(string topic, int queueId)
        {
            var key = string.Format("{0}-{1}", topic, queueId);
            var minOffset = -1L;
            foreach (var queueOffsetDict in _groupQueueOffsetDict.Values)
            {
                long offset;
                if (queueOffsetDict.TryGetValue(key, out offset))
                {
                    if (minOffset == -1)
                    {
                        minOffset = offset;
                    }
                    else if (offset < minOffset)
                    {
                        minOffset = offset;
                    }
                }
            }

            return minOffset;
        }
        public void UpdateQueueOffset(string topic, int queueId, long offset, string group)
        {
            if (UpdateQueueOffsetInternal(topic, queueId, offset, group))
            {
                _logger.DebugFormat("ConsumeOffset updated, consumerGroup: {0}, topic: {1}, queueId: {2}, offset: {3}", group, topic, queueId, offset);
                Interlocked.Increment(ref _lastUpdateVersion);
            }
        }
        public void DeleteQueueOffset(string topic, int queueId)
        {
            var key = string.Format("{0}-{1}", topic, queueId);
            foreach (var groupEntry in _groupQueueOffsetDict)
            {
                long offset;
                if (groupEntry.Value.TryRemove(key, out offset))
                {
                    _logger.DebugFormat("Deleted queue offset, topic:{0}, queueId:{1}, consumer group:{2}, consumedOffset:{3}", topic, queueId, groupEntry.Key, offset);
                }
            }
            Interlocked.Increment(ref _lastUpdateVersion);
            TryPersistQueueOffset();
        }
        public void DeleteQueueOffset(string consumerGroup, string topic, int queueId)
        {
            ConcurrentDictionary<string, long> queueOffsetDict;
            if (_groupQueueOffsetDict.TryGetValue(consumerGroup, out queueOffsetDict))
            {
                var key = string.Format("{0}-{1}", topic, queueId);
                long offset;
                if (queueOffsetDict.TryRemove(key, out offset))
                {
                    _logger.DebugFormat("Deleted queue offset, topic:{0}, queueId:{1}, consumer group:{2}, consumedOffset:{3}", topic, queueId, consumerGroup, offset);
                }
            }
            Interlocked.Increment(ref _lastUpdateVersion);
            TryPersistQueueOffset();
        }
        public IEnumerable<QueueConsumedOffset> GetQueueConsumedOffsets()
        {
            using (var connection = new SqlConnection(_setting.ConnectionString))
            {
                connection.Open();
                var queueConsumedOffsets = new List<QueueConsumedOffset>();
                using (var command = new SqlCommand(string.Format(_selectQueueOffsetsSQLFormat, _currentVersion), connection))
                {
                    var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var topic = (string)reader["Topic"];
                        var queueId = (int)reader["QueueId"];
                        var queueOffset = (long)reader["QueueOffset"];
                        queueConsumedOffsets.Add(new QueueConsumedOffset { Topic = topic, QueueId = queueId, ConsumedOffset = queueOffset });
                    }
                }
                return queueConsumedOffsets;
            }
        }
        public IEnumerable<TopicConsumeInfo> QueryTopicConsumeInfos(string groupName, string topic)
        {
            var entryList = _groupQueueOffsetDict.Where(x => string.IsNullOrEmpty(groupName) || x.Key.Contains(groupName));
            var topicConsumeInfoList = new List<TopicConsumeInfo>();

            foreach (var entry in entryList)
            {
                foreach (var subEntry in entry.Value.Where(x => string.IsNullOrEmpty(topic) || x.Key.Split(new string[] { "-" }, StringSplitOptions.None)[0].Contains(topic)))
                {
                    var items = subEntry.Key.Split(new string[] { "-" }, StringSplitOptions.None);
                    topicConsumeInfoList.Add(new TopicConsumeInfo
                    {
                        ConsumerGroup = entry.Key,
                        Topic = items[0],
                        QueueId = int.Parse(items[1]),
                        ConsumedOffset = subEntry.Value
                    });
                }
            }

            return topicConsumeInfoList;
        }

        private void Clear()
        {
            _currentVersion = 0;
            _lastUpdateVersion = 0;
            _lastPersistVersion = 0;
            _groupQueueOffsetDict.Clear();
        }
        private bool UpdateQueueOffsetInternal(string topic, int queueId, long offset, string group)
        {
            var changed = false;
            var queueOffsetDict = _groupQueueOffsetDict.GetOrAdd(group, k =>
            {
                changed = true;
                return new ConcurrentDictionary<string, long>();
            });
            var key = string.Format("{0}-{1}", topic, queueId);
            queueOffsetDict.AddOrUpdate(key, offset, (k, oldOffset) =>
            {
                if (offset != oldOffset)
                {
                    changed = true;
                }
                return offset;
            });
            return changed;
        }
        private void RecoverQueueOffset()
        {
            _logger.Info("Start to recover queue consume offset from db.");
            using (var connection = new SqlConnection(_setting.ConnectionString))
            {
                connection.Open();

                long? maxVersion = null;
                using (var command = new SqlCommand(_getLatestVersionSQL, connection))
                {
                    var result = command.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        maxVersion = (long)result;
                    }
                }

                if (maxVersion == null)
                {
                    _logger.Info("0 queue consume offsets recovered from db.");
                    return;
                }

                _currentVersion = maxVersion.Value;

                using (var command = new SqlCommand(string.Format(_getLatestVersionQueueOffsetSQL, maxVersion.Value), connection))
                {
                    var reader = command.ExecuteReader();
                    var count = 0;
                    while (reader.Read())
                    {
                        var version = (long)reader["Version"];
                        var group = (string)reader["ConsumerGroup"];
                        var topic = (string)reader["Topic"];
                        var queueId = (int)reader["QueueId"];
                        var queueOffset = (long)reader["QueueOffset"];

                        UpdateQueueOffsetInternal(topic, queueId, queueOffset, group);
                        count++;
                    }
                    _logger.InfoFormat("{0} queue consume offsets recovered from db, version:{1}", count, _currentVersion);
                }
            }
        }
        private void TryPersistQueueOffset()
        {
            if (Interlocked.CompareExchange(ref _isPersistingOffsets, 1, 0) == 0)
            {
                try
                {
                    PersistQueueOffset();
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Failed to persist queue offsets to db, last persist version:{0}", _lastPersistVersion), ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _isPersistingOffsets, 0);
                }
            }
        }
        private void PersistQueueOffset()
        {
            var lastUpdateVersion = _lastUpdateVersion;
            if (_lastPersistVersion >= lastUpdateVersion)
            {
                return;
            }
            using (var connection = new SqlConnection(_setting.ConnectionString))
            {
                connection.Open();

                //Start the sql transaction.
                var transaction = connection.BeginTransaction();

                //Insert the new version of queueOffset.
                var timestamp = DateTime.Now;
                using (var command = new SqlCommand())
                {
                    command.Connection = connection;
                    command.Transaction = transaction;
                    foreach (var groupEntry in _groupQueueOffsetDict)
                    {
                        var group = groupEntry.Key;
                        foreach (var offsetEntry in groupEntry.Value)
                        {
                            var items = offsetEntry.Key.Split(new string[] { "-" }, StringSplitOptions.None);
                            var topic = items[0];
                            var queueId = items[1];
                            var queueOffset = offsetEntry.Value;
                            var version = _currentVersion + 1;
                            command.CommandText = string.Format(_insertNewVersionQueueOffsetSQLFormat, version, group, topic, queueId, queueOffset, timestamp);
                            command.ExecuteNonQuery();
                        }
                    }
                    //Delete the old version of queueOffset.
                    command.CommandText = string.Format(_deleteOldVersionQueueOffsetSQLFormat, _currentVersion);
                    command.ExecuteNonQuery();
                }

                //Commit the sql transaction.
                transaction.Commit();

                _logger.DebugFormat("Success to persist queue consume offset to db, version:{0}", _currentVersion + 1);

                _currentVersion++;
                _lastPersistVersion = lastUpdateVersion;
            }
        }
    }
}
