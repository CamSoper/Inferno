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
        TimeSpan _fireTimeout = TimeSpan.FromMinutes(10);
        DateTime _igniterOnTime;
        bool _fireCheck;
        DateTime _fireCheckTime;
        bool _fireStarted;
        int _ignitionTemp = 140;
        private readonly int _ignitionThreshold = 20;
        private readonly int _fireCheckThreshold = 50;


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

        public int GetFireCheckTemp()
        {
            if(_smoker.Mode == SmokerMode.Smoke)
            {
                return 130;
            }
            else
            {
                return 160;
            }
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
                        if(_smoker.Temps.GrillTemp >= _ignitionTemp)
                        {
                            _fireStarted = true;
                        }

                        if (_fireStarted && _smoker.Temps.GrillTemp < GetFireCheckTemp() && !_fireCheck)
                        {
                            _fireCheck = true;
                            _fireCheckTime = DateTime.Now;
                        }
                        else if (_fireCheck && _smoker.Temps.GrillTemp >= GetFireCheckTemp())
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