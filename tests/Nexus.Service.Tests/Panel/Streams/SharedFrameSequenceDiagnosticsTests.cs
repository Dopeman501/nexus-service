using Nexus.Service.Panel.Streams;
using Nexus.Overlay.Media;
using System.Buffers.Binary;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed unsafe class SharedFrameSequenceDiagnosticsTests
{
    [Fact]
    public void Reader_counts_only_skips_after_its_first_valid_observation()
    {
        var reader = new SharedFrameReadDiagnostics("private-section", 1, 1000, 0);
        reader.Observe(100); reader.Observe(102); reader.Observe(108);
        Assert.Equal(new SharedFrameReadSnapshot(3, 100, 108, 2, 0, 0), reader.Snapshot());
    }

    [Fact]
    public void Writer_reports_publication_and_observed_reader_progress_separately()
    {
        var writer = new SharedFrameWriteDiagnostics("private-section", 1, 1000, 0);
        writer.Observe(20, 0); writer.Observe(22, 18);
        Assert.Equal(new SharedFrameWriteSnapshot(2, 22, 18, 2, 0), writer.Snapshot());
    }

    [Theory]
    [InlineData(0)] [InlineData(-2)] [InlineData(3)]
    public void Reader_invalid_sequences_do_not_change_its_baseline(long invalid)
    {
        var reader = new SharedFrameReadDiagnostics("s", 1, 1000, 0);
        reader.Observe(4); reader.Observe(invalid); reader.Observe(6);
        Assert.Equal(new SharedFrameReadSnapshot(2, 4, 6, 0, 0, 1), reader.Snapshot());
    }

    [Fact]
    public void Reader_duplicate_and_regression_do_not_invent_progress()
    {
        var reader = new SharedFrameReadDiagnostics("s", 1, 1000, 0);
        reader.Observe(8); reader.Observe(8); reader.Observe(6); reader.Observe(12);
        Assert.Equal(new SharedFrameReadSnapshot(2, 8, 12, 1, 1, 1), reader.Snapshot());
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(3, 0)] [InlineData(4, -2)]
    [InlineData(4, 3)] [InlineData(4, 6)]
    public void Writer_invalid_or_inconsistent_snapshots_remain_unknown(long published, long consumed)
    {
        var writer = new SharedFrameWriteDiagnostics("s", 1, 1000, 0);
        writer.Observe(published, consumed);
        Assert.Equal(1, writer.Snapshot().Invalid);
        Assert.Equal(-1, writer.Snapshot().PendingAtObservation);
    }

    [Fact]
    public void Writer_regression_and_duplicate_are_invalid_and_do_not_increment_writes()
    {
        var writer = new SharedFrameWriteDiagnostics("s", 1, 1000, 0);
        writer.Observe(8, 6); writer.Observe(8, 6); writer.Observe(6, 4);
        Assert.Equal(1, writer.Snapshot().Writes);
        Assert.Equal(2, writer.Snapshot().Invalid);
    }

    [Fact]
    public void Invalid_reader_metadata_does_not_erase_a_successful_publication()
    {
        var writer = new SharedFrameWriteDiagnostics("s", 1, 1000, 0);
        writer.Observe(4, 7);
        Assert.Equal(new SharedFrameWriteSnapshot(1, 4, 7, -1, 1), writer.Snapshot());
        writer.Observe(6, 4);
        Assert.Equal(new SharedFrameWriteSnapshot(2, 6, 4, 1, 1), writer.Snapshot());
    }

    [Fact]
    public void Actual_shared_reader_byte_copy_and_consumed_sequence_are_unchanged()
    {
        var reader = new SharedFrameReadDiagnostics("s", 1, 1000, 0);
        var section = new byte[StreamFrameSection.HeaderBytes + 16];
        long last = 0;
        fixed (byte* view = section)
        {
            StreamFrameSection.Initialize(view);
            foreach (var sequence in new long[] { 6, 12 })
            {
                BinaryPrimitives.WriteInt64LittleEndian(section.AsSpan(8), sequence);
                BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(28), 16);
                section.AsSpan(StreamFrameSection.HeaderBytes).Fill((byte)sequence);
                var destination = new byte[16];
                Assert.True(StreamFrameSection.TryRead(view, section.Length, ref last, destination, out var length));
                StreamFrameSection.PublishConsumed(view, last); reader.Observe(last);
                Assert.Equal(16, length); Assert.All(destination, value => Assert.Equal((byte)sequence, value));
                Assert.Equal(sequence, BinaryPrimitives.ReadInt64LittleEndian(section.AsSpan(32)));
            }
        }
        Assert.Equal(2, reader.Snapshot().SkippedBetweenReads);
    }

    [Fact]
    public void Reports_are_hashed_rate_limited_bounded_and_have_no_payload()
    {
        var reader = new SharedFrameReadDiagnostics("private-section", 12, 1000, 100);
        var writer = new SharedFrameWriteDiagnostics("private-section", 12, 1000, 100);
        Assert.Null(reader.Report(99)); Assert.Null(writer.Report(99));
        for (var sample = 0; sample < 72; sample++)
        {
            var now = 100 + sample * 30000L;
            var r = Assert.IsType<string>(reader.Report(now));
            var w = Assert.IsType<string>(writer.Report(now));
            Assert.DoesNotContain("private-section", r); Assert.DoesNotContain("private-section", w);
            Assert.Contains("epoch=12", r); Assert.Contains("epoch=12", w);
            Assert.Null(reader.Report(now + 1)); Assert.Null(writer.Report(now + 1));
        }
        Assert.Null(reader.Report(long.MaxValue)); Assert.Null(writer.Report(long.MaxValue));
    }

    [Theory]
    [InlineData(0, 1000)] [InlineData(1, 0)] [InlineData(-1, 1)] [InlineData(1, -1)]
    public void Epoch_and_clock_contracts_are_validated(long revision, long frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedFrameReadDiagnostics("s", revision, frequency, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedFrameWriteDiagnostics("s", revision, frequency, 0));
    }

    [Fact]
    public void Section_identity_is_required_and_a_new_epoch_has_no_old_skips()
    {
        Assert.Throws<ArgumentException>(() => new SharedFrameReadDiagnostics("", 1, 1, 0));
        Assert.Throws<ArgumentException>(() => new SharedFrameWriteDiagnostics("", 1, 1, 0));
        var reader = new SharedFrameReadDiagnostics("s", 2, 1, 0); reader.Observe(400);
        Assert.Equal(0, reader.Snapshot().SkippedBetweenReads);
    }
}
