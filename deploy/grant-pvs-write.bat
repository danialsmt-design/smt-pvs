@echo off
REM One-time: let PVS (SQL login pvs_ro) write the production count into DailyProductionCount.
REM Run this ON the Parts Control PC (DESKTOP-TECHNIC), logged in as the DB admin / DBA account.
REM dbsvc can INSERT but cannot GRANT, so this must be run by a db_owner/sysadmin account.
echo ========================================================
echo  Granting pvs_ro INSERT on DailyProductionCount ...
echo ========================================================
sqlcmd -S .\SQLEXPRESS -E -b -d ReelPart-New -Q "GRANT INSERT ON dbo.DailyProductionCount TO pvs_ro;"
if %errorlevel%==0 (
  echo.
  echo  SUCCESS - PVS can now write the production count.
) else (
  echo.
  echo  FAILED - this account is not allowed to GRANT.
  echo  Please run again as the SQL administrator / DBA account.
)
echo.
echo  Current pvs_ro permissions on DailyProductionCount:
sqlcmd -S .\SQLEXPRESS -E -d ReelPart-New -Q "SELECT p.permission_name, p.state_desc FROM sys.database_permissions p JOIN sys.database_principals dp ON p.grantee_principal_id=dp.principal_id WHERE dp.name='pvs_ro' AND p.major_id=OBJECT_ID('dbo.DailyProductionCount');"
echo.
pause
