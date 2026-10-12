@echo off
del "%~dp0remo-capture.bin" 2>nul
start "" "%~dp0Remo.exe" --capture
