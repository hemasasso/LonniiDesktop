using Lonnii.Api.Features.Print;

namespace Lonnii.Tests;

public class PrintQueueTests
{
    [Fact]
    public void Open_lists_only_this_espaces_postes_host_first()
    {
        var q = new PrintQueue();
        q.Seen("g1", "till", "Caisse 2", isHost: false);
        q.Seen("g1", "host", "Comptoir", isHost: true);
        q.Seen("g2", "other", "Ailleurs", isHost: true);

        var open = q.Open("g1");

        Assert.Equal(["host", "till"], open.Select(p => p.DeviceId));
    }

    [Fact]
    public void A_job_goes_once_to_its_poste_only()
    {
        var q = new PrintQueue();
        q.Seen("g1", "till", "Caisse", false);
        var job = q.Add("g1", "till", "v1", "ticket", "u1");

        Assert.Null(q.Next("g1", "host"));
        Assert.Null(q.Next("g2", "till"));
        Assert.Equal(job.Id, q.Next("g1", "till")!.Id);
        Assert.Null(q.Next("g1", "till"));
        Assert.Equal("sent", q.Get("g1", job.Id)!.Status);
    }

    [Fact]
    public void Only_the_target_poste_reports_and_other_espaces_cannot_read_it()
    {
        var q = new PrintQueue();
        var job = q.Add("g1", "till", "v1", "a4", "u1");

        Assert.False(q.Finish("g1", "host", job.Id, true, null));
        Assert.True(q.Finish("g1", "till", job.Id, false, "Pas de papier"));

        Assert.Equal("failed", q.Get("g1", job.Id)!.Status);
        Assert.Equal("Pas de papier", q.Get("g1", job.Id)!.Message);
        Assert.Null(q.Get("g2", job.Id));
    }

    [Fact]
    public void Remote_sessions_cannot_queue_a_print()
    {
        Assert.False(Lonnii.Api.Features.Remote.RemoteRoutingMiddleware.IsReplayable("POST", "/api/print/jobs"));
    }
}
