
using System.Text;
using Inferno.Common.Models;
using System.Text.Json;

namespace Inferno.Common.Proxies
{
    /// <summary>
    /// Proxy to invoke the Smoker API
    /// </summary>
    public class SmokerProxy : IDisposable
    {

        private HttpClient _client;

        public SmokerProxy()
        {
            _client = new HttpClient();
        }

        private bool disposedValue;

        public async Task<SmokerStatus> GetStatusAsync() 
        {
            HttpResponseMessage result = await InfernoApiRequestAsync(SmokerEndpoint.status);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<SmokerStatus>(await result.Content.ReadAsStringAsync(), options) ?? new SmokerStatus();
        }

        public async Task SetSetPointAsync(int setPoint)
        {
            await InfernoApiRequestAsync(SmokerEndpoint.setpoint, setPoint.ToString());
        }
        
        public async Task SetPValueAsync(int pValue)
        {
            await InfernoApiRequestAsync(SmokerEndpoint.pvalue, pValue.ToString());
        }

        public async Task<int> GetPValueAsync() 
        {
            HttpResponseMessage result = await InfernoApiRequestAsync(SmokerEndpoint.pvalue);
            return int.Parse(await result.Content.ReadAsStringAsync());
        }

        public async Task SetModeAsync(SmokerMode smokerMode)
        {
            await InfernoApiRequestAsync(SmokerEndpoint.mode, $"\"{smokerMode}\"");
        }

        private async Task<HttpResponseMessage> InfernoApiRequestAsync(SmokerEndpoint endpoint, string content = "")
        {
            Uri requestUri = new Uri($"http://127.0.0.1:5000/api/{endpoint}");
            HttpResponseMessage result;

            if (string.IsNullOrEmpty(content))
            {
                result = await _client.GetAsync(requestUri);
            }
            else
            {
                HttpContent requestBody = new StringContent($"{content}", Encoding.UTF8, "application/json");
                result = await _client.PostAsync(requestUri, requestBody);
            }
            result.EnsureSuccessStatusCode();
            return result;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _client.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}