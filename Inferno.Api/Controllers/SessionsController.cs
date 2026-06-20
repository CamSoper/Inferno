using System.Globalization;
using System.Text;
using Inferno.Api.Interfaces;
using Inferno.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Inferno.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SessionsController : ControllerBase
    {
        readonly ICookLogStore _store;

        public SessionsController(ICookLogStore store)
        {
            _store = store;
        }

        // GET api/sessions
        [HttpGet]
        public ActionResult<IReadOnlyList<CookSessionDto>> List()
        {
            return Ok(_store.ListSessions());
        }

        // GET api/sessions/active
        [HttpGet("active")]
        public ActionResult<CookSessionDto> Active()
        {
            var id = _store.GetActiveSessionId();
            if (id == null)
            {
                return NoContent();
            }

            var session = _store.GetSession(id.Value);
            return session == null ? NoContent() : Ok(session);
        }

        // GET api/sessions/{id}?from=&to=
        [HttpGet("{id:long}")]
        public ActionResult Get(long id, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
        {
            var session = _store.GetSession(id);
            if (session == null)
            {
                return NotFound();
            }

            return Ok(new { session, samples = _store.GetSamples(id, from, to) });
        }

        // GET api/sessions/{id}/export.csv
        [HttpGet("{id:long}/export.csv")]
        public ActionResult ExportCsv(long id, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
        {
            var session = _store.GetSession(id);
            if (session == null)
            {
                return NotFound();
            }

            var samples = _store.GetSamples(id, from, to);
            var csv = new StringBuilder();
            csv.AppendLine("timestamp,grill_temp,probe_temp,mode,setpoint,pvalue,auger_on,blower_on,igniter_on,fire_healthy,preheated");
            foreach (var s in samples)
            {
                csv.Append(s.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append(',')
                   .Append(s.GrillTemp.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(s.ProbeTemp.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(s.Mode).Append(',')
                   .Append(s.SetPoint).Append(',')
                   .Append(s.PValue).Append(',')
                   .Append(s.AugerOn ? 1 : 0).Append(',')
                   .Append(s.BlowerOn ? 1 : 0).Append(',')
                   .Append(s.IgniterOn ? 1 : 0).Append(',')
                   .Append(s.FireHealthy ? 1 : 0).Append(',')
                   .Append(s.Preheated ? 1 : 0).Append('\n');
            }

            return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"inferno-session-{id}.csv");
        }

        // POST api/sessions/{id}/label
        [HttpPost("{id:long}/label")]
        public ActionResult SetLabel(long id, [FromBody] string label)
        {
            if (_store.GetSession(id) == null)
            {
                return NotFound();
            }

            _store.SetLabel(id, label);
            return Accepted();
        }
    }
}
