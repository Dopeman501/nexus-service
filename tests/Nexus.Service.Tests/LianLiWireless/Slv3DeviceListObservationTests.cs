using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3DeviceListObservationTests
{
    [Theory][InlineData(0)][InlineData(4)]
    public void Invalid_request_page_budget_is_not_an_accepted_header(int pages) =>
        Assert.False(Slv3DeviceListObservation.Read(new byte[] { 0x10, 0, 0, 0 }, pages).HeaderAccepted);

    [Theory][InlineData(0, 0x10)][InlineData(3, 0x10)][InlineData(4, 0)][InlineData(4, 0x11)]
    public void Missing_or_wrong_command_header_is_not_accepted(int bytes, int echo)
    {
        var reply = new byte[bytes];
        if (bytes > 0) reply[0] = (byte)echo;
        Assert.False(Slv3DeviceListObservation.Read(reply, 1).HeaderAccepted);
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(10)]
    public void All_declared_valid_records_are_complete(int count)
    {
        var observation = Slv3DeviceListObservation.Read(Reply(count, count), 1);
        Assert.True(observation.HeaderAccepted);
        Assert.Equal(count, observation.DeclaredRecords);
        Assert.Equal(count, observation.AvailableRecords);
        Assert.Equal(count, observation.ValidRecords);
        Assert.True(observation.Complete);
    }

    [Fact]
    public void A_partial_prefix_preserves_valid_positive_observations()
    {
        var observation = Slv3DeviceListObservation.Read(Reply(12, 10), 1);
        Assert.Equal(12, observation.DeclaredRecords);
        Assert.Equal(10, observation.AvailableRecords);
        Assert.Equal(10, observation.ValidRecords);
        Assert.False(observation.Complete);
    }

    [Fact]
    public void Invalid_records_are_separate_from_missing_records()
    {
        var reply = Reply(3, 2); reply[^1] = 0;
        var observation = Slv3DeviceListObservation.Read(reply, 1);
        Assert.Equal(2, observation.AvailableRecords);
        Assert.Equal(1, observation.ValidRecords);
        Assert.False(observation.Complete);
    }

    [Fact]
    public void Oversize_native_result_is_bounded_by_the_requested_page()
    {
        var observation = Slv3DeviceListObservation.Read(Reply(12, 12), 1);
        Assert.Equal(10, observation.AvailableRecords);
        Assert.Equal(10, observation.ValidRecords);
        Assert.False(observation.Complete);
    }

    [Fact]
    public void A_declared_count_above_capacity_is_not_complete()
    {
        var observation = Slv3DeviceListObservation.Read(Reply(255, 30), 3);
        Assert.Equal(30, observation.AvailableRecords);
        Assert.False(observation.Complete);
    }

    private static byte[] Reply(int declared, int supplied)
    {
        var reply = new byte[4 + 42 * supplied]; reply[0] = 0x10; reply[1] = (byte)declared;
        for (var i = 0; i < supplied; i++) reply[4 + 42 * (i + 1) - 1] = 0x1c;
        return reply;
    }
}
