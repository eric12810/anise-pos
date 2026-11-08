# PDF menu import

Source: Menu Large Aug 2025shopV2.pdf, visually reviewed from its rendered page on 20 September 2026.

Run `003_import_pdf_menu.sql` in SQL Server Management Studio against `anisedb`. It uses a transaction and can be rerun: matched existing products and their variants are preserved. It never updates orders, staff, customers, payments, or existing prices. It returns inserted counts plus a source/database price comparison.

The reviewed dataset contains 64 distinct products and 87 variants across 12 categories. The four unlabelled main-course blocks are grouped as Beef, Lamb, Seafood and Vegetables. Repeated Anise Creations listings are represented once in the regular category. Small/large rice and noodle prices are 500ml/750ml. Printed croquette, Phoenix Roll, sauce and water choices are represented as variants at their printed product price. No additional modifier surcharges or rules are inferred.

PDF prices take precedence for newly imported products: notably beef sirloin is £11.80 and sea bass is £13.00. Existing products are intentionally preserved; inspect the comparison output for any prices needing a separate update. The import does not contain FoodBooking-only specials, sold-out listings, or promotions absent from this PDF. Descriptions and images are not included in this initial catalogue import.

The matching rule is exact product name or normalized source slug. Ambiguous multiple matches stop the transaction. Differently named pre-existing products require manual review; this is not fuzzy deduplication. The script obtains a transaction-owned lock to serialize repeat imports.

Execution status: prepared and statically checked, not executed against SQL Server by the assistant. Refresh the POS after successful execution.
