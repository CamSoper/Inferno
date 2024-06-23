dotnet publish -c Debug
scp -r .\bin\Debug\net8.0\publish\* pi@inferno:~/inferno/mqtt