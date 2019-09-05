dotnet publish -r linux-arm
scp .\bin\Debug\netcoreapp3.0\linux-arm\publish\* pi@inferno:~/inferno/temps