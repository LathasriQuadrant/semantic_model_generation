using Microsoft.AspNetCore.Mvc;
using TabularModelDeployer.Api.Models;
using TabularModelDeployer.Api.Services;

namespace TabularModelDeployer.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeployController : ControllerBase
{
    private readonly TabularDeploymentService _service;

    public DeployController(TabularDeploymentService service)
    {
        _service = service;
    }

    // [HttpPost]
    // public IActionResult Deploy([FromBody] DeploymentRequest request)
    // {
    //     var result = _service.DeployModel(request);
    //     return Ok(result);
    // }

    [HttpPost]
    public async Task<IActionResult> Deploy([FromBody] DeploymentRequest request)
    {
        var result = await _service.DeployModel(request);
        return Ok(result);
    }
}


