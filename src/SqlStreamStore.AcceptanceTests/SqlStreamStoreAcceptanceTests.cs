﻿namespace SqlStreamStore
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Shouldly;
    using SqlStreamStore.Streams;
    using Xunit;
    using Xunit.Abstractions;

    public partial class StreamStoreAcceptanceTests : IDisposable
    {
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly IDisposable _logCapture;

        public StreamStoreAcceptanceTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
            _logCapture = CaptureLogs(testOutputHelper);
        }

        public void Dispose()
        {
            _logCapture.Dispose();
        }

        [Fact]
        public async Task When_dispose_and_read_then_should_throw()
        {
            using(var fixture = GetFixture())
            {
                var store = await fixture.GetStreamStore();
                store.Dispose();

                Func<Task> act = () => store.ReadAllForwards(Checkpoint.Start, 10);

                act.ShouldThrow<ObjectDisposedException>();
            }
        }

        [Fact]
        public async Task Can_dispose_more_than_once()
        {
            using (var fixture = GetFixture())
            {
                var store = await fixture.GetStreamStore();
                store.Dispose();

                Action act = store.Dispose;

                act.ShouldNotThrow();
            }
        }

        public static NewStreamMessage[] CreateNewStreamMessages(params int[] eventNumbers)
        {
            return eventNumbers
                .Select(eventNumber =>
                {
                    var eventId = Guid.Parse("00000000-0000-0000-0000-" + eventNumber.ToString().PadLeft(12, '0'));
                    return new NewStreamMessage(eventId, "type", "\"data\"", "\"metadata\"");
                })
                .ToArray();
        }

        private static NewStreamMessage[] CreateNewStreamEventSequence(int startId, int count)
        {
            var streamEvents = new List<NewStreamMessage>();
            for(int i = 0; i < count; i++)
            {
                var eventNumber = startId + i;
                var eventId = Guid.Parse("00000000-0000-0000-0000-" + eventNumber.ToString().PadLeft(12, '0'));
                var newStreamEvent = new NewStreamMessage(eventId, "type", "\"data\"", "\"metadata\"");
                streamEvents.Add(newStreamEvent);
            }
            return streamEvents.ToArray();
        }

        private static StreamMessage ExpectedStreamEvent(
            string streamId,
            int eventNumber,
            int sequenceNumber,
            DateTime created)
        {
            var eventId = Guid.Parse("00000000-0000-0000-0000-" + eventNumber.ToString().PadLeft(12, '0'));
            return new StreamMessage(streamId, eventId, sequenceNumber, 0, created, "type", "\"data\"", "\"metadata\"");
        }
    }

    public static class TaskExtensions
    {
        private static Func<int, int> TaskTimeout => timeout => Debugger.IsAttached ? 30000 : timeout;

        public static async Task<T> WithTimeout<T>(this Task<T> task, int timeout = 3000)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
            {
                return await task;
            }
            throw new TimeoutException("Timed out waiting for task");
        }
    }
}