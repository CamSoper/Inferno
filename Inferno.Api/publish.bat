dotnet publish
scp -r .\bin\Debug\net5.0\publish\* pi@inferno:~/inferno/api
ssh pi@inferno sudo reboot