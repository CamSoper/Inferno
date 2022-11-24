using Microsoft.AspNetCore.Mvc;
using Inferno.Common.Interfaces;

namespace Inferno.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PValueController : ControllerBase
    {

        ISmoker _smoker;

        public PValueController(ISmoker smoker)
        {
            _smoker = smoker;
        }

        // GET api/values
        [HttpGet]
        public ActionResult<int> Get()
        {
            return _smoker.PValue;
        }

        // POST api/values
        [HttpPost]
        public ActionResult Post([FromBody] int value)
        {
            _smoker.PValue = value;
            return Accepted();
        }
    }
}
