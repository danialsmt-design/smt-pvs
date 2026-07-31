-- Grant PVS's read-only login (pvs_ro) permission to APPEND production-count rows.
-- Needed for the "PVS writes DailyProductionCount every 30 min" feature (Line 1 ownership).
-- Run this as a DBO / sysadmin on the DB host (DESKTOP-TECHNIC\SQLEXPRESS) — e.g. from the
-- ACER-PC desktop session that has dbo, the same way StockOuts UPDATE was granted.
--
-- pvs_ro keeps read-only everywhere else; this adds INSERT on ONE table only.

USE [ReelPart-New];
GO

GRANT INSERT ON OBJECT::dbo.DailyProductionCount TO [pvs_ro];
GO

-- Verify:
SELECT dp.permission_name, dp.state_desc
FROM sys.database_permissions dp
JOIN sys.objects o      ON o.object_id = dp.major_id
JOIN sys.database_principals u ON u.principal_id = dp.grantee_principal_id
WHERE o.name = 'DailyProductionCount' AND u.name = 'pvs_ro';
GO
