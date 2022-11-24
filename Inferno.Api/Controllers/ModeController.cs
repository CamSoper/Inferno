using Inferno.Common.Interfaces;
using Inferno.Common.Models;
using Microsoft.AspNetCore.Mvc;

namespace Inferno.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ModeController : ControllerBase
    {
        ISmoker _smoker;

        public ModeController(ISmoker smoker)
        {
            _smoker = smoker;
        }

        [HttpGet]
        public ActionResult<string> Get()
        {
            return _smoker.Mode.ToString();
        }

        [HttpPost]
        public ActionResult Post([FromBody] string value)
        {
            try
            {
                SmokerMode newMode = (SmokerMode)Enum.Parse(typeof(SmokerMode), value, true);
                bool success = _smoker.SetMode(newMode);
                if (!success)
                {
                    return StatusCode(403);
                }
                return Accepted();
            }
            catch
            {
                return BadRequest();
            }
        }
    }
}
