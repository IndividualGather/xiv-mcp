using XivMcp.Ui;

namespace XivMcp.Tests;

public class LobbyListTests
{
    private const uint Light = 4, Chaos = 6;

    [Fact]
    public void Nothing_to_judge_while_the_list_loads()
    {
        Assert.Equal(LobbyListVerdict.Loading, LobbyList.Judge(found: false, targetDataCenter: Light, listedDataCenters: []));
    }

    [Fact]
    public void The_target_in_the_list_is_picked()
    {
        Assert.Equal(LobbyListVerdict.Found, LobbyList.Judge(found: true, targetDataCenter: Light, listedDataCenters: [Light]));
    }

    [Fact]
    public void A_list_of_another_data_center_means_going_back_to_the_title_screen()
    {
        // The game can open at the character list of the data center a character was visiting.
        Assert.Equal(LobbyListVerdict.OtherDataCenter, LobbyList.Judge(found: false, targetDataCenter: Light, listedDataCenters: [Chaos]));
    }

    [Fact]
    public void Missing_from_its_own_data_center_list_is_missing()
    {
        Assert.Equal(LobbyListVerdict.Missing, LobbyList.Judge(found: false, targetDataCenter: Light, listedDataCenters: [Light, Light]));
    }

    [Fact]
    public void Missing_when_the_data_center_is_unknown()
    {
        Assert.Equal(LobbyListVerdict.Missing, LobbyList.Judge(found: false, targetDataCenter: 0, listedDataCenters: [Chaos]));
    }
}
