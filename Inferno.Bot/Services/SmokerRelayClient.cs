using Inferno.Common.Models;
using Microsoft.Azure.Relay;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Inferno.Bot.Services
{
    public class SmokerRelayClient
    {
        private readonly IConfiguration _configuration;
        private HttpClient _client;
        private readonly string _relayNamespace;
        private readonly string _connectionName;
        private readonly string _keyName;
        private readonly string _key;
        private readonly Uri _baseUri;

        public SmokerRelayClient(IConfiguration configuration)
        {
            _configuration = configuration;
            _client = HttpClientFactory.Create();

            _relayNamespace = _configuration.GetValue<string>("RelayNamespace");
            _connectionName = _configuration.GetValue<string>("RelayConnectionName");
            _keyName = _configuration.GetValue<string>("RelaySasKeyName");
            _key = _configuration.GetValue<string>("RelaySasKey");

            _baseUri = new Uri(string.Format("https://{0}/{1}/", _relayNamespace, _connectionName));
        }

        public async Task<bool> SetMode(SmokerMode smokermode)
        {
            var response = await SendRelayRequest("mode", HttpMethod.Post, $"\"{smokermode}\"");
            return response.IsSuccessStatusCode;
        }

        public async Task<SmokerStatus> GetStatus()
        {
            var response = await SendRelayRequest("status", HttpMethod.Get);
            if (response.IsSuccessStatusCode)
            {
                string payload = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<SmokerStatus>(payload);
            }
            else
            {
                return new SmokerStatus() { Mode = response.ReasonPhrase };
            }
        }

        public async Task<bool> SetSetpoint(int setPoint)
        {
            var response = await SendRelayRequest("setpoint", HttpMethod.Post, $"{setPoint}");
            return response.IsSuccessStatusCode;
        }

        private async Task AddAuthToken(HttpRequestMessage request)
        {
            TokenProvider tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(_keyName, _key);
            string token = (await tokenProvider.GetTokenAsync(request.RequestUri.AbsoluteUri, TimeSpan.FromHours(1))).TokenString;

            request.Headers.Add("ServiceBusAuthorization", token);
        }

        private async Task<HttpResponseMessage> SendRelayRequest(string apiEndpoint, HttpMethod method, string payload = "")
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri(_baseUri, apiEndpoint),
                Method = method
            };

            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(payload);
                request.Content.Headers.ContentType.MediaType = "application/json";
                request.Content.Headers.ContentType.CharSet = null;
            }

            await AddAuthToken(request);

            var response = await _client.SendAsync(request);

            return response;
        }
    }
}
