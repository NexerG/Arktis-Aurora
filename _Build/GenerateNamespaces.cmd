@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0GenerateNamespaces.ps1" "%~dp0.."
