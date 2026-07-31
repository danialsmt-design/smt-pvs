@echo off
title Set DB password  -  Parts Verification (LINE1PVS)
color 0B
echo.
echo  ============================================================
echo    Connect the Parts Verification app to the database
echo  ============================================================
echo.
echo  Enter the SAME password you set for pvs_ro on the DB PC.
echo  (It is stored locally so the app can read the database.)
echo.
set /p PW=pvs_ro password:
<nul set /p="%PW%" > "C:\PvsLineApp\db-password.txt"
echo.
if exist "C:\PvsLineApp\db-password.txt" (
  echo  Saved to  C:\PvsLineApp\db-password.txt
  echo.
  echo  Now tell Claude "done" and the app will be restarted to connect,
  echo  OR restart the PvsLineApp task yourself.
) else (
  echo  Could not write the file. Tell Claude.
)
echo.
pause
