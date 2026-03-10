@echo off
echo ================================================
echo  Storage Audit - Build for USB/External Drive
echo ================================================
echo.

dotnet publish src\StorageAudit\StorageAudit.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o deploy\

echo.
echo ================================================
echo  Build complete!
echo  deploy\ folder contents:
echo.
dir /b deploy\
echo.
echo  Copy the deploy\ folder contents to your
echo  USB drive or external storage to use.
echo ================================================
pause
