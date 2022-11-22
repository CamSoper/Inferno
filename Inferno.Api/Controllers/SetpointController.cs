using Inferno.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Inferno.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SetpointController : ControllerBase
    {

        ISmoker _smoker;

        public SetpointController(ISmoker smoker)
        {
            _smoker = smoker;
        }

        // GET api/values
        [HttpGet]
        public ActionResult<int> Get()
        {
            return _smoker.SetPoint;
        }

        // POST api/values
        [HttpPost]
        public ActionResult Post([FromBody] int value)
        {
            _smoker.SetPoint = value;
            return Accepted();
        }
    }
}
