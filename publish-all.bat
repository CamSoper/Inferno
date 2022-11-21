pushd .\Inferno.Cli
call .\publish.bat
cd ..\Inferno.Api
call .\publish.bat
popd