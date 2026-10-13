@echo off
chcp 936 >nul
echo 正在结束可能占用文件的旧进程…
taskkill /f /im lbsetup.exe >nul 2>nul
taskkill /f /im 记账本-安装包-v2.0.1-免NET.exe >nul 2>nul
taskkill /f /im 记账本-安装包-v2.0.1-免NET-快装版.exe >nul 2>nul
timeout /t 2 /nobreak >nul
echo 正在清理 newest 里的旧解包器残留…
rd /s /q "D:\desktop\记账\newest\lb-setup" 2>nul
del /f /q "D:\desktop\记账\newest\lb-setup-running.lock" 2>nul
echo.
if exist "D:\desktop\记账\newest\lb-setup" (echo 还剩 lb-setup 没删掉（被占用），重启后再跑一次本脚本) else (echo lb-setup 已清掉)
if exist "D:\desktop\记账\newest\lb-setup-running.lock" (echo 还剩 lb-setup-running.lock 没删掉，重启后再跑一次本脚本) else (echo 锁文件已清掉)
echo.
pause