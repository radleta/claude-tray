using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class SessionChangeQueueTests
{
    private readonly SessionChangeQueue _queue = new();

    [Fact]
    public void Drain_Empty_ReturnsNothing()
    {
        _queue.Drain().Should().BeEmpty();
        _queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Drain_ReturnsWhatWasEnqueued()
    {
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        _queue.Enqueue("s2", SessionChangeKind.Removed);

        _queue.Drain().Should().BeEquivalentTo([
            new SessionChange("s1", SessionChangeKind.Changed),
            new SessionChange("s2", SessionChangeKind.Removed),
        ]);
    }

    [Fact]
    public void Enqueue_RepeatedChangesForOneSession_CoalesceToOne()
    {
        // A single hook write produces several watcher events; they must not become
        // several wire frames.
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        _queue.Enqueue("s1", SessionChangeKind.Changed);

        _queue.Drain().Should().ContainSingle();
    }

    [Fact]
    public void Enqueue_ChangedThenRemoved_KeepsRemoved()
    {
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        _queue.Enqueue("s1", SessionChangeKind.Removed);

        _queue.Drain().Should().ContainSingle()
            .Which.Kind.Should().Be(SessionChangeKind.Removed);
    }

    [Fact]
    public void Enqueue_RemovedThenChanged_KeepsChanged()
    {
        // Delete-then-recreate: the receiver deletes a session's file and a live file-sink
        // publisher recreates it on its next event. The Changed is the truth.
        _queue.Enqueue("s1", SessionChangeKind.Removed);
        _queue.Enqueue("s1", SessionChangeKind.Changed);

        _queue.Drain().Should().ContainSingle()
            .Which.Kind.Should().Be(SessionChangeKind.Changed);
    }

    [Fact]
    public void Drain_EmptiesTheQueue()
    {
        _queue.Enqueue("s1", SessionChangeKind.Changed);

        _queue.Drain().Should().ContainSingle();
        _queue.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Enqueue_AfterADrain_LandsInTheNextOne()
    {
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        _queue.Drain();

        _queue.Enqueue("s2", SessionChangeKind.Changed);

        _queue.Drain().Should().ContainSingle().Which.SessionId.Should().Be("s2");
    }

    [Fact]
    public void PendingCount_TracksDistinctSessions()
    {
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        _queue.Enqueue("s1", SessionChangeKind.Removed);
        _queue.Enqueue("s2", SessionChangeKind.Changed);

        _queue.PendingCount.Should().Be(2);
    }

    [Fact]
    public void SessionIds_AreCaseSensitive()
    {
        // Session ids are opaque tokens from Claude Code, not names — two ids differing only
        // in case are two sessions.
        _queue.Enqueue("Abc", SessionChangeKind.Changed);
        _queue.Enqueue("abc", SessionChangeKind.Changed);

        _queue.PendingCount.Should().Be(2);
    }

    [Fact]
    public void Enqueue_FromManyThreads_LosesNothing()
    {
        Parallel.For(0, 500, i => _queue.Enqueue($"s{i}", SessionChangeKind.Changed));

        _queue.Drain().Should().HaveCount(500);
    }
}
