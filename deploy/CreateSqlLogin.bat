@echo off
title Create read-only SQL login for the Parts Verification app
color 0B
echo.
echo  ============================================================
echo    Create SQL login  pvs_ro  (read-only) on ReelPart-New
echo  ============================================================
echo.
echo  This creates a SQL login the line PCs use to READ the database.
echo  Read-only (db_datareader) - it cannot change any data.
echo.
echo  You will type a password. Remember it - you will put the SAME
echo  password into  db-password.txt  on each line PC.
echo.
set /p PW=Enter a password for pvs_ro:
echo.

set "SQLCMD="
where sqlcmd >nul 2>&1 && set "SQLCMD=sqlcmd"
if not defined SQLCMD if exist "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE" set "SQLCMD=C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE"
if not defined SQLCMD if exist "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\150\Tools\Binn\SQLCMD.EXE" set "SQLCMD=C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\150\Tools\Binn\SQLCMD.EXE"
if not defined SQLCMD if exist "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\130\Tools\Binn\SQLCMD.EXE" set "SQLCMD=C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\130\Tools\Binn\SQLCMD.EXE"
if not defined SQLCMD ( echo  Could not find sqlcmd. Tell Claude. & pause & exit /b 1 )

echo  Creating login and granting read access...
"%SQLCMD%" -S ".\SQLEXPRESS" -E -Q "IF SUSER_ID('pvs_ro') IS NULL CREATE LOGIN [pvs_ro] WITH PASSWORD = N'%PW%', CHECK_POLICY = OFF; ELSE ALTER LOGIN [pvs_ro] WITH PASSWORD = N'%PW%';"
"%SQLCMD%" -S ".\SQLEXPRESS" -E -d "ReelPart-New" -Q "IF DATABASE_PRINCIPAL_ID('pvs_ro') IS NULL CREATE USER [pvs_ro] FOR LOGIN [pvs_ro]; ALTER ROLE db_datareader ADD MEMBER [pvs_ro];"

echo.
echo  Verifying...
"%SQLCMD%" -S ".\SQLEXPRESS" -U pvs_ro -P "%PW%" -d "ReelPart-New" -Q "SELECT CASE WHEN COUNT(*)>0 THEN 'SUCCESS - pvs_ro can read ReelPart-New' ELSE 'no rows?' END AS Result FROM Products;"

echo.
echo  If you see SUCCESS above, the login works.
echo  Next: put this same password into  C:\PvsLineApp\db-password.txt  on the line PC.
echo.
pause
