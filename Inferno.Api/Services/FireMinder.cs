using System.Diagnostics;
using Inferno.Api.Interfaces;
using Inferno.Common.Extensions;
using Inferno.Common.Interfaces;
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
        TimeSpan _reigniteWait = TimeSpan.FromMinutes(1);
        DateTime _igniterOnTime;
        bool _fireCheck;
        DateTime _fireCheckTime;
        bool _fireStarted;
        int _ignitionTemp;
        bool _initialIgnition;

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
            Debug.WriteLine("Resetting fire status.");
            _fireStarted = false;
            _fireCheck = false;
            _initialIgnition = true;
            _ignitionTemp = 200;
        }

        public int GetFireCheckTemp()
        {
            if(_smoker.Mode == SmokerMode.Smoke)
            {
                return 140;
            }
            else
            {
                return _smoker.SetPoint - (_smoker.SetPoint / 180 * 30);
            }
        }

        private async Task FireMinderLoop()
        {
            Debug.WriteLine("Starting Fire Minder thread.");
            ResetFireStatus();
            while (true)
            {
                try
                {
                    if (_smoker.Mode.IsCookingMode() &&
                        _smoker.Temps.GrillTemp < _ignitionTemp &&
                        !_fireStarted)
                    {
                        // The fire is not started, turn on the igniter
                        if (!_igniter.IsOn)
                        {
                            _igniter.On();
                            _ignitionTemp = Convert.ToInt32(_smoker.Temps.GrillTemp) + 10;
                            _igniterOnTime = DateTime.Now;
                        }
                    }

                    if (_smoker.Mode.IsCookingMode() &&
                        _smoker.Temps.GrillTemp > GetFireCheckTemp() &&
                        _fireStarted &&
                        _initialIgnition)
                    {
                        // The fire has been lit at least once.
                        _initialIgnition = false;
                    }

                    if (_igniter.IsOn && 
                            DateTime.Now - _igniterOnTime > _igniterTimeout)
                    {
                        // The igniter has been on for too long, shut it off and go to error mode
                        string errorText = $"{DateTime.Now} Igniter timeout. Setting error mode.";
                        Debug.WriteLine(errorText);
                        Console.WriteLine(errorText);
                        _igniter.Off();
                        _smoker.SetMode(SmokerMode.Error);
                    }

                    if (_smoker.Mode.IsCookingMode())
                    {
                        if(_smoker.Temps.GrillTemp >= _ignitionTemp)
                        {
                            // The fire has started, make sure the igniter is off
                            _fireStarted = true;
                            _igniter.Off();
                        }

                        if (_fireStarted &&
                                !_initialIgnition &&
                                _smoker.Temps.GrillTemp < GetFireCheckTemp() &&
                                !_fireCheck)
                        {
                            // The fire might be going out, keep an eye on it
                            _fireCheck = true;
                            _fireCheckTime = DateTime.Now;
                        }
                        else if (_fireStarted && 
                                    _smoker.Temps.GrillTemp < GetFireCheckTemp() && 
                                    _fireCheck && 
                                    DateTime.Now - _fireCheckTime > _reigniteWait &&
                                    !_igniter.IsOn)
                        {
                            // The fire has been going out for a while, try to reignite
                            _igniter.On();
                            _ignitionTemp = Convert.ToInt32(_smoker.Temps.GrillTemp) + 5;
                            _igniterOnTime = DateTime.Now;
                        }
                        else if (_fireCheck && _smoker.Temps.GrillTemp >= GetFireCheckTemp())
                        {
                            // The fire is healthy again
                            _igniter.Off(); // This is probably not needed, but just in case
                            _fireCheck = false;
                        }
                        else if (_fireCheck && DateTime.Now - _fireCheckTime > _fireTimeout)
                        {
                            // The fire is out, give up and go to error mode
                            string errorText = $"{DateTime.Now} Fire timeout. Setting error mode.";
                            Debug.WriteLine(errorText);
                            Console.WriteLine(errorText);
                            _smoker.SetMode(SmokerMode.Error);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    string errorText = $"{DateTime.Now} Fire Minder loop exception! {ex} {ex.StackTrace}";
                    Console.WriteLine(errorText);
                    Debug.WriteLine(errorText);
                }
            }
        }
    }
}