Run the following command to build the project:

```bash
dotnet publish src/Tessera.App -c Release -r linux-x64 --self-contained -o dist
./dist/Tessera.App
