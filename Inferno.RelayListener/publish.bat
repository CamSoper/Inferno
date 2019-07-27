dotnet publish -r linux-arm
scp pi@inferno:/home/pi/inferno/listener/appsettings.json ./temp.json
scp -C .\bin\Debug\netcoreapp3.0\linux-arm\publish\* pi@inferno:~/inferno/listener
scp ./temp.json pi@inferno:/home/pi/inferno/listener/appsettings.json
del temp.json