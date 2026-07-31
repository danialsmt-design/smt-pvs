@echo off
REM Run on the DB host (or ACER-PC) in a session whose Windows user is dbo/sysadmin on SQLEXPRESS.
REM Grants pvs_ro INSERT on DailyProductionCount so PVS can write the Line-1 production count.
echo Granting pvs_ro INSERT on DailyProductionCount ...
sqlcmd -S localhost\SQLEXPRESS -E -d "ReelPart-New" -Q "GRANT INSERT ON OBJECT::dbo.DailyProductionCount TO [pvs_ro];"
if %errorlevel%==0 (echo Done.) else (echo FAILED - run from a dbo session.)
pause
