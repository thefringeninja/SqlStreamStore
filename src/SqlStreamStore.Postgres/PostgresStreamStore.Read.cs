﻿namespace SqlStreamStore
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using NpgsqlTypes;
    using SqlStreamStore.Infrastructure;
    using SqlStreamStore.Streams;

    partial class PostgresStreamStore
    {
        private async Task<ReadStreamPage> ReadStreamInternal(
            PostgresqlStreamId streamId,
            int start,
            int count,
            ReadDirection direction,
            bool prefetch,
            ReadNextStreamPage readNext,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            // If the count is int.MaxValue, TSql will see it as a negative number. 
            // Users shouldn't be using int.MaxValue in the first place anyway.
            count = count == int.MaxValue ? count - 1 : count;

            // To read backwards from end, need to use int MaxValue
            var streamVersion = start == StreamVersion.End ? int.MaxValue : start;

            var messages = new List<StreamMessage>();

            Func<List<StreamMessage>, int, int> getNextVersion;

            if(direction == ReadDirection.Forward)
            {
                getNextVersion = (events, lastVersion) =>
                {
                    if(events.Any())
                    {
                        return events.Last().StreamVersion + 1;
                    }

                    return lastVersion + 1;
                };
            }
            else
            {
                getNextVersion = (events, lastVersion) =>
                {
                    if(events.Any())
                    {
                        return events.Last().StreamVersion - 1;
                    }

                    return -1;
                };
            }

            var refcursorSql = new StringBuilder();

            using(var command = new NpgsqlCommand($"{_settings.Schema}.read", transaction.Connection, transaction)
            {
                CommandType = CommandType.StoredProcedure,
                Parameters =
                {
                    new NpgsqlParameter
                    {
                        NpgsqlValue = streamId.Id,
                        Size = 42,
                        NpgsqlDbType = NpgsqlDbType.Char
                    },
                    new NpgsqlParameter
                    {
                        NpgsqlValue = count + 1,
                        NpgsqlDbType = NpgsqlDbType.Integer
                    },
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Bigint,
                        NpgsqlValue = DBNull.Value
                    },
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Integer,
                        NpgsqlValue = streamVersion
                    },
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Boolean,
                        Value = direction == ReadDirection.Forward
                    },
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Boolean,
                        Value = prefetch
                    }
                }
            })
            using(var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .NotOnCapturedContext())
            {
                while(await reader.ReadAsync(cancellationToken).NotOnCapturedContext())
                {
                    refcursorSql.AppendLine($@"FETCH ALL IN ""{reader.GetString(0)}"";");
                }
            }

            using(var command = new NpgsqlCommand(refcursorSql.ToString(), transaction.Connection, transaction))
            using(var reader = await command.ExecuteReaderAsync(cancellationToken).NotOnCapturedContext())
            {
                if(!reader.HasRows)
                {
                    return new ReadStreamPage(
                        streamId.IdOriginal,
                        PageReadStatus.StreamNotFound,
                        start,
                        -1,
                        -1,
                        -1,
                        direction,
                        true,
                        readNext);
                }

                if(messages.Count == count)
                {
                    messages.Add(default(StreamMessage));
                }

                await reader.ReadAsync(cancellationToken).NotOnCapturedContext();

                var lastVersion = reader.GetInt32(0);
                var lastPosition = reader.GetInt64(1);

                await reader.NextResultAsync(cancellationToken).NotOnCapturedContext();

                while(await reader.ReadAsync(cancellationToken).NotOnCapturedContext())
                {
                    messages.Add(ReadStreamMessage(reader, prefetch));
                }

                var isEnd = true;

                if(messages.Count == count + 1)
                {
                    isEnd = false;
                    messages.RemoveAt(count);
                }

                return new ReadStreamPage(
                    streamId.IdOriginal,
                    PageReadStatus.Success,
                    start,
                    getNextVersion(messages, lastVersion),
                    lastVersion,
                    lastPosition,
                    direction,
                    isEnd,
                    readNext,
                    messages.ToArray());
            }
        }

        private (NpgsqlParameter lastPosition, NpgsqlParameter lastVersion) GetOutParameters() => (
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Bigint,
                Direction = ParameterDirection.Output
            },
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Integer,
                Direction = ParameterDirection.Output
            });

        protected override async Task<ReadAllPage> ReadAllForwardsInternal(
            long fromPositionExlusive,
            int maxCount,
            bool prefetch,
            ReadNextAllPage readNext,
            CancellationToken cancellationToken)
        {
            long nextPosition = fromPositionExlusive;

            var messages = new List<StreamMessage>(maxCount);

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).NotOnCapturedContext();

                using(var command = new NpgsqlCommand($"{_settings.Schema}.read", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    Parameters =
                    {
                        new NpgsqlParameter("count", maxCount),
                        new NpgsqlParameter("ordinal", fromPositionExlusive),
                        new NpgsqlParameter("forwards", true),
                        new NpgsqlParameter("prefetch", prefetch)
                    }
                })
                {
                    command.Prepare();
                    using(var reader = await command.ExecuteReaderAsync(cancellationToken).NotOnCapturedContext())
                    {
                        while(await reader.ReadAsync(cancellationToken).NotOnCapturedContext())
                        {
                            var message = ReadStreamMessage(reader, prefetch);
                            nextPosition = message.Position;
                            messages.Add(message);
                        }
                    }
                }
            }

            return new ReadAllPage(fromPositionExlusive,
                nextPosition,
                false,
                ReadDirection.Forward,
                readNext,
                messages.ToArray());
        }

        protected override Task<ReadAllPage> ReadAllBackwardsInternal(
            long fromPositionExclusive,
            int maxCount,
            bool prefetch,
            ReadNextAllPage readNext,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected override async Task<ReadStreamPage> ReadStreamForwardsInternal(
            string streamId,
            int start,
            int count,
            bool prefetch,
            ReadNextStreamPage readNext,
            CancellationToken cancellationToken)
        {
            var streamIdInfo = new StreamIdInfo(streamId);

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).NotOnCapturedContext();

                using(var transaction = connection.BeginTransaction())
                {
                    return await ReadStreamInternal(streamIdInfo.PostgresqlStreamId,
                        start,
                        count,
                        ReadDirection.Forward,
                        prefetch,
                        readNext,
                        transaction,
                        cancellationToken);
                }
            }
        }

        protected override Task<ReadStreamPage> ReadStreamBackwardsInternal(
            string streamId,
            int fromVersionInclusive,
            int count,
            bool prefetch,
            ReadNextStreamPage readNext,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private StreamMessage ReadStreamMessage(IDataRecord reader, bool prefetch)
            => prefetch
                ? new StreamMessage(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    reader.GetInt64(3),
                    reader.GetDateTime(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7))
                : default(StreamMessage);

        protected override Task<long> ReadHeadPositionInternal(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

    }
}