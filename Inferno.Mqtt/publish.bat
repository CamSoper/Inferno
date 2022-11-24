dotnet publish -c Debug
scp -r .\bin\Debug\net7.0\publish\* pi@inferno:~/inferno/mqtt