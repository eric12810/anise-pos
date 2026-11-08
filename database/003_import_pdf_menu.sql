-- Source: Menu Large Aug 2025shopV2.pdf, visually reviewed 20 September 2026.
-- Adds missing products/variants only. Existing products, prices and orders are preserved.
-- Repeated listings in Anise Creations are represented once under their regular category.
-- Choice details are in the companion notes; no unverified modifier rules are invented.
USE [anisedb];
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock = sys.sp_getapplock @Resource=N'AniseMenuPdf2025Import', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
IF @lock < 0 THROW 51000, 'Another menu import is running. Try again later.', 1;
DECLARE @now datetimeoffset(0) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
DECLARE @data TABLE (CategoryOrder int, ProductOrder int, CategoryName nvarchar(100), CategorySlug nvarchar(120), ProductName nvarchar(150), ProductSlug nvarchar(170), VariantName nvarchar(100), Price decimal(10,2), VariantOrder int);
INSERT INTO @data VALUES
(0,0,N'Starters',N'starters',N'Spring Rolls',N'spring-rolls',N'Regular',6.50,0),
(0,1,N'Starters',N'starters',N'Prawn Toast',N'prawn-toast',N'Regular',7.50,0),
(0,2,N'Starters',N'starters',N'Crispy Seaweed',N'crispy-seaweed',N'Regular',6.50,0),
(0,3,N'Starters',N'starters',N'Coconut Prawns',N'coconut-prawns',N'Regular',8.00,0),
(0,4,N'Starters',N'starters',N'Yammy Fish',N'yammy-fish',N'Regular',7.50,0),
(0,5,N'Starters',N'starters',N'Fried Dumpling',N'fried-dumpling',N'Regular',7.50,0),
(0,6,N'Starters',N'starters',N'Crispy Aromatic Duck',N'crispy-aromatic-duck',N'Regular',12.50,0),
(0,7,N'Starters',N'starters',N'Treasured Capital Spare Ribs',N'treasured-capital-spare-ribs',N'Regular',8.50,0),
(0,8,N'Starters',N'starters',N'Osmanthus Nectar Rib Ends',N'osmanthus-nectar-rib-ends',N'Regular',7.50,0),
(0,9,N'Starters',N'starters',N'Korean Chicken',N'korean-chicken',N'Regular',7.50,0),
(0,10,N'Starters',N'starters',N'Grilled Satay Chicken',N'grilled-satay-chicken',N'Regular',7.50,0),
(0,11,N'Starters',N'starters',N'Smoked Chicken',N'smoked-chicken',N'Regular',7.50,0),
(0,12,N'Starters',N'starters',N'Salt and Pepper Prawns',N'salt-and-pepper-prawns',N'Regular',7.50,0),
(0,13,N'Starters',N'starters',N'Salt and Pepper Tofu',N'salt-and-pepper-tofu',N'Regular',7.50,0),
(0,14,N'Starters',N'starters',N'Salt and Pepper Wings',N'salt-and-pepper-wings',N'Regular',7.50,0),
(1,0,N'Soup',N'soup',N'Won Ton Soup',N'won-ton-soup',N'Regular',5.80,0),
(1,1,N'Soup',N'soup',N'Prawns Sweetcorn Soup',N'prawns-sweetcorn-soup',N'Regular',5.80,0),
(1,2,N'Soup',N'soup',N'Chicken Sweetcorn Soup',N'chicken-sweetcorn-soup',N'Regular',5.50,0),
(1,3,N'Soup',N'soup',N'Hot and Sour Soup',N'hot-and-sour-soup',N'Regular',5.50,0),
(1,4,N'Soup',N'soup',N'Mushroom Sweetcorn Soup',N'mushroom-sweetcorn-soup',N'Regular',5.50,0),
(2,0,N'Anise Creations',N'anise-creations',N'Sweet & Sour Meatballs',N'sweet-sour-meatballs',N'Regular',8.00,0),
(2,1,N'Anise Creations',N'anise-creations',N'Anise Rice Croquettes',N'anise-rice-croquettes',N'Hoi Sin Duck',7.00,0),
(2,1,N'Anise Creations',N'anise-creations',N'Anise Rice Croquettes',N'anise-rice-croquettes',N'Mushroom',7.00,1),
(2,2,N'Anise Creations',N'anise-creations',N'Sweet and Sour Tofu Nuggets',N'sweet-and-sour-tofu-nuggets',N'Regular',8.50,0),
(3,0,N'Chicken',N'chicken',N'Jue Jue Chicken',N'jue-jue-chicken',N'Regular',8.80,0),
(3,1,N'Chicken',N'chicken',N'Chilli Chicken',N'chilli-chicken',N'Regular',8.80,0),
(3,2,N'Chicken',N'chicken',N'Chicken with Cashew Nuts',N'chicken-with-cashew-nuts',N'Regular',8.80,0),
(3,3,N'Chicken',N'chicken',N'Sweet and Sour Chicken',N'sweet-and-sour-chicken',N'Regular',8.80,0),
(3,4,N'Chicken',N'chicken',N'''Gordon''s Gin'' Infused Lemon Chicken',N'gordon-s-gin-infused-lemon-chicken',N'Regular',9.80,0),
(4,0,N'Beef',N'beef',N'Beef Sirloin with Black Pepper',N'beef-sirloin-with-black-pepper',N'Regular',11.80,0),
(4,1,N'Beef',N'beef',N'Beef Sirloin in Black Bean Sauce',N'beef-sirloin-in-black-bean-sauce',N'Regular',11.80,0),
(4,2,N'Beef',N'beef',N'Beef Sirloin in Cantonese Style',N'beef-sirloin-in-cantonese-style',N'Regular',11.80,0),
(4,3,N'Beef',N'beef',N'Crispy Chilli Beef',N'crispy-chilli-beef',N'Regular',9.50,0),
(5,0,N'Lamb',N'lamb',N'Lamb Curry with Sweet Potatoes',N'lamb-curry-with-sweet-potatoes',N'Regular',9.00,0),
(5,1,N'Lamb',N'lamb',N'Stir Fried Lamb with Leek',N'stir-fried-lamb-with-leek',N'Regular',9.00,0),
(6,0,N'Seafood',N'seafood',N'Steamed Sea Bass Fillet',N'steamed-sea-bass-fillet',N'Regular',13.00,0),
(6,1,N'Seafood',N'seafood',N'Prawns with Exotic Mushrooms',N'prawns-with-exotic-mushrooms',N'Regular',11.50,0),
(6,2,N'Seafood',N'seafood',N'Drunken Prawn',N'drunken-prawn',N'Regular',9.50,0),
(6,3,N'Seafood',N'seafood',N'Szechuan Prawns',N'szechuan-prawns',N'Regular',9.50,0),
(7,0,N'Vegetables',N'vegetables',N'Braised Tofu',N'braised-tofu',N'Regular',8.50,0),
(7,1,N'Vegetables',N'vegetables',N'Sea Spice Aubergine',N'sea-spice-aubergine',N'Regular',8.50,0),
(7,2,N'Vegetables',N'vegetables',N'Mange Tout and French Beans',N'mange-tout-and-french-beans',N'Regular',8.50,0),
(7,3,N'Vegetables',N'vegetables',N'Pak Choi in Oyster Sauce',N'pak-choi-in-oyster-sauce',N'Regular',8.50,0),
(8,0,N'Rice',N'rice',N'Smoky Chicken Fried Rice',N'smoky-chicken-fried-rice',N'500ml',7.00,0),
(8,0,N'Rice',N'rice',N'Smoky Chicken Fried Rice',N'smoky-chicken-fried-rice',N'750ml',8.50,1),
(8,1,N'Rice',N'rice',N'Crispy Duck Fried Rice',N'crispy-duck-fried-rice',N'500ml',7.00,0),
(8,1,N'Rice',N'rice',N'Crispy Duck Fried Rice',N'crispy-duck-fried-rice',N'750ml',8.50,1),
(8,2,N'Rice',N'rice',N'Special Fried Rice',N'special-fried-rice',N'500ml',6.50,0),
(8,2,N'Rice',N'rice',N'Special Fried Rice',N'special-fried-rice',N'750ml',8.00,1),
(8,3,N'Rice',N'rice',N'King Prawn Fried Rice',N'king-prawn-fried-rice',N'500ml',6.50,0),
(8,3,N'Rice',N'rice',N'King Prawn Fried Rice',N'king-prawn-fried-rice',N'750ml',8.00,1),
(8,4,N'Rice',N'rice',N'Vegetable Fried Rice',N'vegetable-fried-rice',N'500ml',6.00,0),
(8,4,N'Rice',N'rice',N'Vegetable Fried Rice',N'vegetable-fried-rice',N'750ml',7.50,1),
(8,5,N'Rice',N'rice',N'Egg Fried Rice',N'egg-fried-rice',N'500ml',5.50,0),
(8,5,N'Rice',N'rice',N'Egg Fried Rice',N'egg-fried-rice',N'750ml',7.00,1),
(8,6,N'Rice',N'rice',N'Steamed Rice',N'steamed-rice',N'500ml',4.00,0),
(8,6,N'Rice',N'rice',N'Steamed Rice',N'steamed-rice',N'750ml',5.00,1),
(9,0,N'Noodles',N'noodles',N'Singapore Rice Noodles',N'singapore-rice-noodles',N'500ml',6.50,0),
(9,0,N'Noodles',N'noodles',N'Singapore Rice Noodles',N'singapore-rice-noodles',N'750ml',8.00,1),
(9,1,N'Noodles',N'noodles',N'Vegetable Rice Noodles',N'vegetable-rice-noodles',N'500ml',6.00,0),
(9,1,N'Noodles',N'noodles',N'Vegetable Rice Noodles',N'vegetable-rice-noodles',N'750ml',7.50,1),
(9,2,N'Noodles',N'noodles',N'King Prawn Chow Mein',N'king-prawn-chow-mein',N'500ml',6.50,0),
(9,2,N'Noodles',N'noodles',N'King Prawn Chow Mein',N'king-prawn-chow-mein',N'750ml',8.00,1),
(9,3,N'Noodles',N'noodles',N'Chicken Chow Mein',N'chicken-chow-mein',N'500ml',6.00,0),
(9,3,N'Noodles',N'noodles',N'Chicken Chow Mein',N'chicken-chow-mein',N'750ml',7.50,1),
(9,4,N'Noodles',N'noodles',N'Vegetable Chow Mein',N'vegetable-chow-mein',N'500ml',6.00,0),
(9,4,N'Noodles',N'noodles',N'Vegetable Chow Mein',N'vegetable-chow-mein',N'750ml',7.50,1),
(9,5,N'Noodles',N'noodles',N'Noodles with Beansprout',N'noodles-with-beansprout',N'500ml',5.00,0),
(9,5,N'Noodles',N'noodles',N'Noodles with Beansprout',N'noodles-with-beansprout',N'750ml',6.50,1),
(10,0,N'Drinks',N'drinks',N'Coke 330ml',N'coke-330ml',N'Regular',2.30,0),
(10,1,N'Drinks',N'drinks',N'Diet Coke 330ml',N'diet-coke-330ml',N'Regular',2.00,0),
(10,2,N'Drinks',N'drinks',N'Orange 330ml',N'orange-330ml',N'Regular',2.00,0),
(10,3,N'Drinks',N'drinks',N'7-Up 330ml',N'7-up-330ml',N'Regular',2.00,0),
(10,4,N'Drinks',N'drinks',N'Still Water 330ml',N'still-water-330ml',N'Regular',1.50,0),
(10,5,N'Drinks',N'drinks',N'Still/Sparkling Water 750ml',N'still-sparkling-water-750ml',N'Still',3.50,0),
(10,5,N'Drinks',N'drinks',N'Still/Sparkling Water 750ml',N'still-sparkling-water-750ml',N'Sparkling',3.50,1),
(11,0,N'Other',N'other',N'Phoenix Roll',N'phoenix-roll',N'Apple Cinnamon',6.00,0),
(11,0,N'Other',N'other',N'Phoenix Roll',N'phoenix-roll',N'Tropical Pineapple',6.00,1),
(11,0,N'Other',N'other',N'Phoenix Roll',N'phoenix-roll',N'Nutty Nutella',6.00,2),
(11,0,N'Other',N'other',N'Phoenix Roll',N'phoenix-roll',N'Combo of all three',6.00,3),
(11,1,N'Other',N'other',N'Sauces',N'sauces',N'Curry Sauce',2.00,0),
(11,1,N'Other',N'other',N'Sauces',N'sauces',N'Sweet & Sour Sauce',2.00,1),
(11,1,N'Other',N'other',N'Sauces',N'sauces',N'Chilli Oil',2.00,2),
(11,1,N'Other',N'other',N'Sauces',N'sauces',N'Sweet Chilli Sauce',2.00,3),
(11,1,N'Other',N'other',N'Sauces',N'sauces',N'Hoi Sin Sauce',2.00,4),
(11,1,N'Other',N'other',N'Sauces',N'sauces',N'Satay Sauce',2.00,5),
(11,2,N'Other',N'other',N'Prawn Cracker',N'prawn-cracker',N'Regular',2.50,0);

-- Ambiguous matches require review; do not guess which existing record to modify.
IF EXISTS (SELECT d.ProductSlug FROM (SELECT DISTINCT ProductSlug, ProductName FROM @data) d
 JOIN dbo.Products p WITH (UPDLOCK,HOLDLOCK) ON p.Slug=d.ProductSlug OR p.Name=d.ProductName
 GROUP BY d.ProductSlug HAVING COUNT(*)>1)
 THROW 51001, 'Multiple existing products match a source item. Review duplicates before importing.', 1;

DECLARE @categories TABLE (Slug nvarchar(120) PRIMARY KEY, Id uniqueidentifier);
INSERT INTO @categories
SELECT d.CategorySlug, COALESCE((SELECT TOP(1) c.Id FROM dbo.Categories c WITH (UPDLOCK,HOLDLOCK)
 WHERE c.Slug=d.CategorySlug OR c.Name=d.CategoryName OR (d.CategorySlug='starters' AND c.Slug='starter')
 ORDER BY c.CreatedAtUtc,c.Id), NEWID())
FROM (SELECT DISTINCT CategorySlug,CategoryName FROM @data) d;
INSERT INTO dbo.Categories (Id,CreatedAtUtc,Name,Slug,DisplayOrder,IsAvailable)
SELECT m.Id,@now,d.CategoryName,d.CategorySlug,d.CategoryOrder,1
FROM (SELECT DISTINCT CategoryName,CategorySlug,CategoryOrder FROM @data) d JOIN @categories m ON m.Slug=d.CategorySlug
WHERE NOT EXISTS(SELECT 1 FROM dbo.Categories c WHERE c.Id=m.Id);
DECLARE @addedCategories int=@@ROWCOUNT;

DECLARE @products TABLE (Slug nvarchar(170) PRIMARY KEY, Id uniqueidentifier, IsNew bit);
INSERT INTO @products
SELECT d.ProductSlug,COALESCE(p.Id,NEWID()),CASE WHEN p.Id IS NULL THEN 1 ELSE 0 END
FROM (SELECT DISTINCT ProductSlug,ProductName FROM @data) d
LEFT JOIN dbo.Products p WITH (UPDLOCK,HOLDLOCK) ON p.Slug=d.ProductSlug OR p.Name=d.ProductName;

INSERT INTO dbo.Products (Id,CreatedAtUtc,CategoryId,Name,Slug,DisplayOrder,IsAvailable)
SELECT m.Id,@now,c.Id,d.ProductName,d.ProductSlug,d.ProductOrder,1
FROM (SELECT DISTINCT CategorySlug,ProductName,ProductSlug,ProductOrder FROM @data) d
JOIN @products m ON m.Slug=d.ProductSlug JOIN @categories c ON c.Slug=d.CategorySlug WHERE m.IsNew=1;
DECLARE @addedProducts int=@@ROWCOUNT;

INSERT INTO dbo.ProductVariants (Id,CreatedAtUtc,ProductId,Name,Sku,Price,IsAvailable,DisplayOrder)
SELECT NEWID(),@now,m.Id,d.VariantName,CONCAT('PDF25-',d.ProductSlug,'-',d.VariantOrder),d.Price,1,d.VariantOrder
FROM @data d JOIN @products m ON m.Slug=d.ProductSlug WHERE m.IsNew=1;
DECLARE @addedVariants int=@@ROWCOUNT;

COMMIT TRANSACTION;
SELECT @addedCategories AS AddedCategories,@addedProducts AS AddedProducts,@addedVariants AS AddedVariants,
 (SELECT COUNT(*) FROM @products WHERE IsNew=0) AS ExistingProductsPreserved;
-- Compare source prices with the current database, including preserved records.
SELECT d.CategoryName,d.ProductName,d.VariantName AS SourceVariant,d.Price AS SourcePrice,
 v.Name AS DatabaseVariant,v.Price AS DatabasePrice
FROM @data d JOIN @products m ON m.Slug=d.ProductSlug
LEFT JOIN dbo.ProductVariants v ON v.ProductId=m.Id AND v.Name=d.VariantName
ORDER BY d.CategoryOrder,d.ProductOrder,d.VariantOrder;
END TRY
BEGIN CATCH
IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
