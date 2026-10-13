@echo off
chcp 936 >nul
title 清理旧解包器残留（需要管理员）
echo ============================================================
echo  记账本 · 清理 v0.2.1 之前那套旧解包器留下的残留
echo  目标： newest\lb-setup\  和  newest\lb-setup-running.lock
echo.
echo  说明：这两个东西是旧安装器（存根 + 容器 v2）留下的，早就不用了，
echo        但它们的文件权限里有一条"拒绝删除子项"，普通权限删不掉；
echo        本脚本要管理员权限才能接管并清掉。
echo ============================================================
echo.

net session >nul 2>&1
if errorlevel 1 (
  echo  [!] 现在不是管理员权限。请右键本文件 →「以管理员身份运行」。
  echo.
  pause
  exit /b 1
)

set TARGET=D:\desktop\记账\newest

echo [1/4] 结束可能占用文件的进程…
taskkill /f /im lbsetup.exe >nul 2>nul
taskkill /f /im 记账本-安装包-v2.0.1-免NET.exe >nul 2>nul

echo [2/4] 接管所有权（takeown）…
if exist "%TARGET%\lb-setup" takeown /f "%TARGET%\lb-setup" /r /d y >nul 2>nul
if exist "%TARGET%\lb-setup-running.lock" takeown /f "%TARGET%\lb-setup-running.lock" >nul 2>nul

echo [3/4] 去掉"拒绝删除"的权限项（icacls /remove:d）…
if exist "%TARGET%\lb-setup" icacls "%TARGET%\lb-setup" /remove:d Everyone /t /c >nul 2>nul
if exist "%TARGET%\lb-setup" icacls "%TARGET%\lb-setup" /grant "%USERNAME%":(OI)(CI)F /t /c >nul 2>nul

echo [4/4] 删除…
if exist "%TARGET%\lb-setup" rd /s /q "%TARGET%\lb-setup" 2>nul
if exist "%TARGET%\lb-setup-running.lock" del /f /q "%TARGET%\lb-setup-running.lock" 2>nul

echo.
if exist "%TARGET%\lb-setup" (echo  [X] lb-setup 还在（可能仍被占用）——重启后再跑一次本脚本) else (echo  [OK] lb-setup 已清掉)
if exist "%TARGET%\lb-setup-running.lock" (echo  [X] 锁文件还在——重启后再跑一次本脚本) else (echo  [OK] 锁文件已清掉)

if not exist "%TARGET%\lb-setup" if not exist "%TARGET%\lb-setup-running.lock" (
  echo.
  echo  两个都清干净了。newest 现在是空的，这个文件夹已经没人用了
  echo  （安装包现在按版本存放：v0.2\v^<版本^>\安装包\），可以整个删掉。
)
echo.
pause
