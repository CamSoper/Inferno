using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Inferno.Api.Interfaces;
using Inferno.Common.Extensions;
using Inferno.Common.Models;

namespace Inferno.Api.Services
{
    public class FireMinder
    {
        ISmoker _smoker;
        IRelayDevice _igniter;
        Task _fireMinderLoop;

        TimeSpan _igniterTimeout = TimeSpan.FromMinutes(10);
        TimeSpan _fireTimeout = TimeSpan.FromMinutes(5);
        DateTime _igniterOnTime;
        int _ignitionTemp = 140;
        bool _fireCheck;
        DateTime _fireCheckTime;
        bool _fireStarted;

        public bool IsFireHealthy => !_fireCheck;
        public bool IsFireStarted => _fireStarted;

        public FireMinder(ISmoker smoker, IRelayDevice igniter)
        {
            _smoker = smoker;
            _igniter = igniter;
            _fireMinderLoop = FireMinderLoop();
        }

        public void ResetFireStatus()
        {
            _fireStarted = false;
            _fireCheck = false;
        }
        private async Task FireMinderLoop()
        {
            Debug.WriteLine("Starting Fire Minder thread.");
            while (true)
            {
                try
                {
                    if (_smoker.Mode.IsCookingMode() &&
                        _smoker.Temps.GrillTemp < _ignitionTemp &&
                        !_fireStarted)
                    {
                        if (!_igniter.IsOn)
                        {
                            _igniter.On();
                            _igniterOnTime = DateTime.Now;
                        }
                    }
                    else if (_smoker.Mode.IsCookingMode() &&
                        _smoker.Temps.GrillTemp >= _ignitionTemp)
                    {
                        _fireStarted = true;
                        _igniter.Off();
                    }
                    else
                    {
                        _igniter.Off();
                    }

                    if (_igniter.IsOn && DateTime.Now - _igniterOnTime > _igniterTimeout)
                    {
                        string errorText = "Igniter timeout. Setting error mode.";
                        Debug.WriteLine(errorText);
                        Console.WriteLine(errorText);
                        _igniter.Off();
                        _smoker.SetMode(SmokerMode.Error);
                    }

                    if (_smoker.Mode.IsCookingMode())
                    {
                        if (_fireStarted && _smoker.Temps.GrillTemp < _ignitionTemp && !_fireCheck)
                        {
                            _fireCheck = true;
                            _fireCheckTime = DateTime.Now;
                        }
                        else if (_fireCheck && _smoker.Temps.GrillTemp >= _ignitionTemp)
                        {
                            _fireCheck = false;
                        }
                        else if (_fireCheck && DateTime.Now - _fireCheckTime > _fireTimeout)
                        {
                            string errorText = "Fire timeout. Setting error mode.";
                            Debug.WriteLine(errorText);
                            Console.WriteLine(errorText);
                            _smoker.SetMode(SmokerMode.Error);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    string errorText = $"Fire Minder loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }
            }
        }
    }
}