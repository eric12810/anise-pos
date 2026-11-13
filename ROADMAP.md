# Anise POS Roadmap

Last updated: 20 September 2026

1. **Network receipt printing** — Direct customer and kitchen receipt printing added for Epson at `192.168.1.234:9100`. The POS server must reach the printer; a local bridge or private network connection is still needed if the service is hosted remotely. Verify with a physical receipt.
2. **Order history, search, and receipt reprinting** — Complete. Available at `/history.html` with customer/order search, status/type/date filters, order details, and customer or kitchen receipt reprinting.
3. **Payment methods and payment status** — Complete.
4. **Menu-management improvements** — Complete. Managers can edit categories, products, descriptions, variants, prices, availability, modifier groups, and extra choices at `/menu-admin.html`.
5. **Daily sales totals and reporting** — Complete. Owner/Manager reports at `/reports.html` include date presets, gross/paid/pending totals, average order value, payment split, order types, exclusions, and daily totals.
6. **Staff accounts, roles, and audit history** — Complete. Owners can add/edit/deactivate staff, assign roles, reset passwords, review last sign-in, and view recorded order activity at `/staff-admin.html`.
7. **Final reliability, backup, and launch checks** — Complete. Database-aware health checks, HTTPS enforcement, security headers, secure cookies, login rate limiting, owner data exports, builds/tests, and live smoke checks are in place.

## Future enhancements — after POS and website launch

### Customer and address management (low priority)

- [ ] Review customer and address table structure only after the POS and live website are reliably handling real orders; prefer dedicated tables within each environment's existing SQL Server database unless a separate database is justified.
- [ ] Build a postcode-to-street lookup from independently verified Anise customer-provided addresses, keeping the shared street directory separate from personally identifiable customer records.
- [ ] On postcode entry, check the local directory and suggest matching street names alongside Google Places. Support postcodes covering multiple streets/buildings; never auto-confirm an address solely from its postcode.
- [ ] Keep manual address entry and correction available when Google Places is missing or incorrect, and only save confirmed address information.
- [ ] Review secure POS API access to customer addresses for both POS and website, with appropriate customer authentication/verification and privacy safeguards.
- [ ] Keep staging and production databases strictly isolated: never copy live customers, orders, telephone numbers or payment records into staging.
- [ ] Test any future schema migrations in staging, back up production before an approved release, and avoid production changes until explicitly authorised.

Update this file whenever an item is completed, paused, reordered, or expanded.
