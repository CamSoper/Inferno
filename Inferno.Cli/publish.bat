dotnet publish -r linux-arm
scp -C .\bin\Debug\netcoreapp3.0\linux-arm\publish\* pi@inferno:~/inferno/cli