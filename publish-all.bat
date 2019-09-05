pushd .\Inferno.Cli
call .\publish.bat
cd ..\Inferno.RelayListener
call .\publish.bat
cd ..\Inferno.TemperatureLogger
call .\publish.bat
cd ..\Inferno.Api
call .\publish.bat
popd