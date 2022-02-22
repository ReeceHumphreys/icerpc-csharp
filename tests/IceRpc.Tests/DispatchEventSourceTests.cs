// Copyright (c) ZeroC, Inc. All rights reserved.

using IceRpc.Internal;
using NUnit.Framework;
using System.Buffers;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;

namespace IceRpc.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class DispatchEventSourceTests : IDisposable
{
    private readonly DispatchEventSource _eventSource;

    public DispatchEventSourceTests() =>
        _eventSource = new DispatchEventSource(Guid.NewGuid().ToString());

    [TearDown]
    public void TearDown() => Dispose();

    public void Dispose() => _eventSource.Dispose();

    [Test]
    public void DispatchEventSource_RequestStart()
    {
        int expectedEventId = 1;
        using var eventListener = new TestEventListener(expectedEventId);
        eventListener.EnableEvents(_eventSource, EventLevel.Verbose);

        _eventSource.RequestStart(CreateIncomingRequest("/service", "ice_id"));

        EventWrittenEventArgs? eventData = eventListener.EventData;
        Assert.That(eventData, Is.Not.Null);
        Assert.AreEqual(expectedEventId, eventData!.EventId);
        Assert.AreEqual("RequestStart", eventData.EventName);
        Assert.AreEqual(EventLevel.Informational, eventData.Level);
        Assert.That(eventData.EventSource, Is.SameAs(_eventSource));
        Assert.AreEqual("/service", eventData.Payload![0]);
        Assert.AreEqual("ice_id", eventData.Payload![1]);
    }

    [Test]
    public void DispatchEventSource_RequestStop()
    {
        int expectedEventId = 2;
        using var eventListener = new TestEventListener(expectedEventId);
        eventListener.EnableEvents(_eventSource, EventLevel.Verbose);

        _eventSource.RequestStop(CreateIncomingRequest("/service", "ice_id"));

        EventWrittenEventArgs? eventData = eventListener.EventData;
        Assert.That(eventData, Is.Not.Null);
        Assert.AreEqual(expectedEventId, eventData!.EventId);
        Assert.AreEqual("RequestStop", eventData.EventName);
        Assert.AreEqual(EventLevel.Informational, eventData.Level);
        Assert.That(eventData.EventSource, Is.SameAs(_eventSource));
        Assert.AreEqual("/service", eventData.Payload![0]);
        Assert.AreEqual("ice_id", eventData.Payload![1]);
    }

    [Test]
    public void DispatchEventSource_RequestCanceled()
    {
        int expectedEventId = 3;
        using var eventListener = new TestEventListener(expectedEventId);
        eventListener.EnableEvents(_eventSource, EventLevel.Verbose);

        _eventSource.RequestCanceled(CreateIncomingRequest("/service", "ice_id"));

        EventWrittenEventArgs? eventData = eventListener.EventData;
        Assert.That(eventData, Is.Not.Null);
        Assert.AreEqual(expectedEventId, eventData!.EventId);
        Assert.AreEqual("RequestCanceled", eventData.EventName);
        Assert.AreEqual(EventLevel.Informational, eventData.Level);
        Assert.That(eventData.EventSource, Is.SameAs(_eventSource));
        Assert.AreEqual("/service", eventData.Payload![0]);
        Assert.AreEqual("ice_id", eventData.Payload![1]);
    }

    [Test]
    public void DispatchEventSource_RequestFailed()
    {
        int expectedEventId = 4;
        using var eventListener = new TestEventListener(expectedEventId);
        eventListener.EnableEvents(_eventSource, EventLevel.Verbose);

        _eventSource.RequestFailed(CreateIncomingRequest("/service", "ice_id"), "IceRpc.RemoteException");

        EventWrittenEventArgs? eventData = eventListener.EventData;
        Assert.That(eventData, Is.Not.Null);
        Assert.AreEqual(expectedEventId, eventData!.EventId);
        Assert.AreEqual("RequestFailed", eventData.EventName);
        Assert.AreEqual(EventLevel.Informational, eventData.Level);
        Assert.That(eventData.EventSource, Is.SameAs(_eventSource));
        Assert.AreEqual("/service", eventData.Payload![0]);
        Assert.AreEqual("ice_id", eventData.Payload![1]);
        Assert.AreEqual("IceRpc.RemoteException", eventData.Payload![2]);
    }

    private static IncomingRequest CreateIncomingRequest(string path, string operation) =>
        new IncomingRequest(
            Protocol.IceRpc,
            path: path,
            fragment: "",
            operation: operation,
            PipeReader.Create(ReadOnlySequence<byte>.Empty),
            Encoding.Slice20,
            responseWriter: InvalidPipeWriter.Instance);

    private class TestEventListener : EventListener
    {
        private readonly int _eventId;

        public TestEventListener(int eventId) => _eventId = eventId;

        public EventWrittenEventArgs? EventData { get; private set; }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == _eventId)
            {
                EventData = eventData;
            }
        }
    }
}