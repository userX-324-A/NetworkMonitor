using Microsoft.AspNetCore.Mvc;
using NetworkMonitorService.Services;
using System;

namespace NetworkMonitorService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MonitorController : ControllerBase
    {
        private readonly ILogger<MonitorController> _logger;
        private readonly IMonitorControlService _monitorControlService;

        public MonitorController(ILogger<MonitorController> logger, IMonitorControlService monitorControlService)
        {
            _logger = logger;
            _monitorControlService = monitorControlService;
        }

        /// <summary>
        /// Manually triggers the reset of all accumulated total network and disk IO counters.
        /// </summary>
        /// <returns>Ok if the reset was triggered, NotFound otherwise.</returns>
        [HttpPost("reset-totals")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult ResetTotalCounters()
        {
            _logger.LogInformation("API request received to reset total counters.");
            bool triggered = _monitorControlService.ResetTotalCounters();

            if (triggered)
            {
                _logger.LogInformation("ResetTotalCounters action successfully triggered via MonitorControlService.");
                return Ok("Total counters reset triggered successfully.");
            }
            else
            {
                _logger.LogWarning("Failed to trigger reset via MonitorControlService. Trigger action might not be registered.");
                return NotFound("Reset action could not be triggered. The worker service might not be ready or the action is not registered.");
            }
        }
    }
} 