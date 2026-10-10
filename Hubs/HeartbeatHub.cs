using Microsoft.AspNetCore.SignalR;
using Whetstone.Services;

namespace Whetstone.Hubs;

public class HeartbeatHub : Hub
{
    private readonly EventStore _store;

    public HeartbeatHub(EventStore store)
    {
        _store = store;
    }

    public override async Task OnConnectedAsync()
    {
        // Send recent events and current state to the newly connected client
        var recent = _store.GetRecent(500);
        var state = _store.GetState();

        await Clients.Caller.SendAsync("ReceiveHistory", recent);
        await Clients.Caller.SendAsync("ReceiveState", state);

        await base.OnConnectedAsync();
    }
}
