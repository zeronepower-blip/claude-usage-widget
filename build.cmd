@echo off
rem ClaudeUsage.cs 재빌드 (외부 의존성 0 — Windows 내장 csc만 사용)
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize+ /out:%~dp0ClaudeUsage.exe /r:System.Web.Extensions.dll %~dp0ClaudeUsage.cs
if %errorlevel%==0 (echo 빌드 성공: %~dp0ClaudeUsage.exe) else (echo 빌드 실패 exit=%errorlevel%)
