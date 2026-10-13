@echo off
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
echo.
echo Готово: out\TaskbarAppVolume.exe
pause
