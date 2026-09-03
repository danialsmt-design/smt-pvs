/* ConsumedReels — reels PVS retired as spent remnants (swapped off a feeder below their rank threshold), and any
   restored back. PVS zeroes the reel's StockOuts.Quantity AND writes a row here; a restore puts the qty back and
   sets Restored=1. Requires dbo/DDL to create; after that PVS (its StockOuts write login) needs INSERT/UPDATE here.

   Rank thresholds (retire when remaining below): A -> only 0 | B -> < 30 | C -> < 200.                            */

IF OBJECT_ID('dbo.ConsumedReels','U') IS NULL
BEGIN
    CREATE TABLE dbo.ConsumedReels (
        ID                INT IDENTITY(1,1) PRIMARY KEY,
        Uid               NVARCHAR(60)  NOT NULL,
        PartNumber        NVARCHAR(40)  NOT NULL,
        Rank              CHAR(1)       NULL,
        RemainingAtRetire INT           NOT NULL DEFAULT (0),   -- pcs the reel had when retired (for restore)
        Line              NVARCHAR(20)  NULL,
        LotNo             NVARCHAR(40)  NULL,
        RetiredAt         DATETIME      NOT NULL DEFAULT (GETDATE()),
        Restored          BIT           NOT NULL DEFAULT (0),
        RestoredAt        DATETIME      NULL
    );
    CREATE INDEX IX_ConsumedReels_Uid_Open ON dbo.ConsumedReels (Uid, Restored);
END;

/* PartAttrition — accumulated written-off pieces per part number (the remainder zeroed when a remnant reel is
   retired). Incremented on retire, decremented on restore. Source of the bi-weekly attrition report.            */
IF OBJECT_ID('dbo.PartAttrition','U') IS NULL
BEGIN
    CREATE TABLE dbo.PartAttrition (
        PartNumber    NVARCHAR(40) NOT NULL PRIMARY KEY,
        AttritionPcs  BIGINT       NOT NULL DEFAULT (0),   -- accumulated written-off pieces
        Reels         INT          NOT NULL DEFAULT (0),   -- accumulated reels retired
        LastAt        DATETIME     NULL
    );
END;

/* Grant the PVS write login INSERT/UPDATE/SELECT on BOTH tables (same login PVS already writes StockOuts with:
   pvs_ro). If SyncStockOuts uses a different login, change 'pvs_ro' below to that login name and re-run.          */
GRANT INSERT, UPDATE, SELECT ON dbo.ConsumedReels TO [pvs_ro];
GRANT INSERT, UPDATE, SELECT ON dbo.PartAttrition TO [pvs_ro];
