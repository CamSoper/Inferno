dotnet publish -c Debug
scp -rC .\bin\Debug\net7.0\publish\* pi@inferno:~/inferno/api
ssh pi@inferno sudo reboot