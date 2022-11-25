
using System.Text;
using Inferno.Common.Models;
using Newtonsoft.Json;

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
            return JsonConvert.DeserializeObject<SmokerStatus>(await result.Content.ReadAsStringAsync()) ?? new SmokerStatus();
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
            Uri requestUri = new Uri($"http://localhost:5000/api/{endpoint}");
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

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~SmokerProxy()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}