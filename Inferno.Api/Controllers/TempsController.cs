using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using Inferno.Common.Models;
using Microsoft.AspNetCore.Mvc;

namespace Inferno.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TempsController : ControllerBase
    {

        ISmoker _smoker;

        public TempsController(ISmoker smoker)
        {
            _smoker = smoker;
        }

        // GET api/values
        [HttpGet]
        public ActionResult<Temps> Get()
        {
            return _smoker.Temps;
        }
    }
}
