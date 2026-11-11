@echo off
rem Compila panel.exe con el compilador de .NET Framework 4 incluido en Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:"%~dp0panel.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "%~dp0panel.cs"
