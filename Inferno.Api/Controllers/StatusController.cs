using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Inferno.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class StatusController : ControllerBase
    {

        ISmoker _smoker;

        public StatusController(ISmoker smoker)
        {
            _smoker = smoker;
        }

        // GET api/values
        [HttpGet]
        public ActionResult<SmokerStatus> Get()
        {
            return _smoker.Status;
        }
    }
}
