dotnet publish
scp pi@inferno:/home/pi/inferno/listener/appsettings.json ./temp.json
scp -r .\bin\Debug\net5.0\publish\* pi@inferno:~/inferno/listener
scp ./temp.json pi@inferno:/home/pi/inferno/listener/appsettings.json
del temp.json