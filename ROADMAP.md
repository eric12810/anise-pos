# Anise POS Roadmap

Last updated: 20 September 2026

1. **Network receipt printing** — Direct customer and kitchen receipt printing added for Epson at `192.168.1.234:9100`. The POS server must reach the printer; a local bridge or private network connection is still needed if the service is hosted remotely. Verify with a physical receipt.
2. **Order history, search, and receipt reprinting** — Complete. Available at `/history.html` with customer/order search, status/type/date filters, order details, and customer or kitchen receipt reprinting.
3. **Payment methods and payment status** — Complete.
4. **Menu-management improvements** — Complete. Managers can edit categories, products, descriptions, variants, prices, availability, modifier groups, and extra choices at `/menu-admin.html`.
5. **Daily sales totals and reporting** — Complete. Owner/Manager reports at `/reports.html` include date presets, gross/paid/pending totals, average order value, payment split, order types, exclusions, and daily totals.
6. **Staff accounts, roles, and audit history** — Complete. Owners can add/edit/deactivate staff, assign roles, reset passwords, review last sign-in, and view recorded order activity at `/staff-admin.html`.
7. **Final reliability, backup, and launch checks** — Complete. Database-aware health checks, HTTPS enforcement, security headers, secure cookies, login rate limiting, owner data exports, builds/tests, and live smoke checks are in place.

Update this file whenever an item is completed, paused, reordered, or expanded.
