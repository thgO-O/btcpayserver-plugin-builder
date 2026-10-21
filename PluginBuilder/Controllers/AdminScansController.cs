using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Authentication;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[ApiController]
[Route("api/v1/admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.AdminApi)]
public class AdminScansController(PluginScanService scans) : ControllerBase
{
    [HttpPost("plugins/{pluginSlug}/scan")]
    public Task<IActionResult> Plugin(string pluginSlug, CancellationToken cancellationToken) => RequestScan(pluginSlug, null, cancellationToken);

    [HttpPost("listing-requests/{requestId:int}/scan")]
    public Task<IActionResult> Listing(int requestId, CancellationToken cancellationToken) => RequestScan(null, requestId, cancellationToken);

    private async Task<IActionResult> RequestScan(string? pluginSlug, int? requestId, CancellationToken cancellationToken)
    {
        var result = await scans.Request(pluginSlug, requestId, User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            Guid.TryParse(User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), out var tokenId) ? tokenId : null, cancellationToken);
        return result.Outcome switch
        {
            PluginScanService.Outcome.NotFound => NotFound(),
            PluginScanService.Outcome.AlreadyProcessed => Conflict(new { message = "This listing request has already been processed." }),
            PluginScanService.Outcome.NoTarget => UnprocessableEntity(new { message = PluginScanService.MissingTargetMessage }),
            _ => Accepted(new { result.ScanRequestId, message = "Scan requested." })
        };
    }
}
