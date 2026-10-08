using Lonnii.Api.Features.Auth;

namespace Lonnii.Api.Features.Live;

/// <summary>The cheap poll that lets tills, phones and remote admins react to changes within seconds.</summary>
public static class LiveEndpoints
{
    public static void MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/live/version",
            (GroupScope scope, ShopChangeNotifier changes) =>
                Results.Ok(new { Version = changes.Get(scope.GroupId) }))
            .WithTags("Live").RequireGroupScope();
    }
}
