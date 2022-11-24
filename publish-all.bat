pushd .\Inferno.Cli
call .\publish.bat
cd ..\Inferno.Mqtt
call .\publish.bat
cd ..\Inferno.Api
call .\publish.bat
popd