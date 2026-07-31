USE [ReelPart-New];
-- Complete L261 / Line 1 / B-side ProductBOM: add M2, M3, M4 feeders (M1 already present).
-- Source: D:\ASHISH\COM-PARTS LIST BY LINE\LINE 1\L261 B SIDE MC{2,3,4} line 1.CSV. Qty = MountStep/6 (L261 6-up).
BEGIN TRAN;
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'WK2-8381-000',1,0,0,'B Side',2,'[F]122 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'VE2-7090-475',2,0,0,'B Side',2,'[F]123 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'VC8-9370-102',4,0,0,'B Side',2,'[F]124 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'VR8-2860-391',2,0,0,'B Side',2,'[F]125 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'WA2-3943-000',2,0,0,'B Side',2,'[F]126 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'VC8-8380-106',2,0,0,'B Side',2,'[F]127 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'WA2-5142-000',2,0,0,'B Side',2,'[F]128 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'YH4-3062-000',1,0,0,'B Side',3,'[F]113 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'WA6-6717-000',1,0,0,'B Side',3,'[F]117 (F)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'YH4-3068-000',1,0,0,'B Side',3,'[F]502 (R)',1);
INSERT INTO ProductBOM (ProductID,PartNumber,Quantity,UnitPrice,TotalPrice,Side,Machine,SupplyPosition,Line) VALUES (1,'WA7-8586-00',1,0,0,'B Side',4,'[F]120 (F)',1);
-- verify (expect M1..M4 present, ~21 feeders total):
SELECT Machine, COUNT(*) AS Feeders FROM ProductBOM WHERE ProductID=1 AND Line=1 AND Side='B Side' GROUP BY Machine ORDER BY Machine;
-- COMMIT;   -- run after verifying; else ROLLBACK;
