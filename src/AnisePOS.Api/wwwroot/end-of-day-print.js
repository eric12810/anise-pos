'use strict';

const money = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' });
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&apos;' }[c]));
const text = value => esc(value) + '\n';

async function receiptPrinterAddress() {
  const response = await fetch('/api/printer-settings', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(10000) });
  if (!response.ok) throw new Error('Could not load printer settings.');
  const data = await response.json();
  if (!/^\d{1,3}(?:\.\d{1,3}){3}$/.test(data.ipAddress)) throw new Error('Invalid printer address.');
  return data.ipAddress;
}

function endOfDayPrinterContent(date, report, external) {
  const platformTotal = Object.values(external).reduce((sum, value) => sum + value, 0);
  const lines = [
    'ANISE', 'END OF DAY', date, '', 'ANISEPOS',
    `Gross sales                 ${money.format(report.grossSales)}`,
    `Paid takings                ${money.format(report.paidSales)}`,
    `Cash paid                   ${money.format(report.cashSales)}`,
    `Card paid                   ${money.format(report.cardSales)}`,
    `Order count                 ${report.orderCount}`, '', 'DELIVERY PLATFORMS',
    `Uber Eats                   ${money.format(external.uberEats)}`,
    `Just Eat Card               ${money.format(external.justEatCard)}`,
    `Deliveroo                   ${money.format(external.deliveroo)}`,
    `Just Eat Cash               ${money.format(external.justEatCash)}`,
    `Platform total              ${money.format(platformTotal)}`, '',
    `TOTAL RECORDED TAKINGS      ${money.format(report.paidSales + platformTotal)}`, '', '', ''
  ];
  return `<text align="center" em="true">${text(lines[0])}</text><text align="center">${text(lines[1])}${text(lines[2])}</text><text align="left">${lines.slice(4).map(text).join('')}</text><cut type="feed"/>`;
}

window.printEndOfDay = async function printEndOfDay(date, report, external) {
  const body = `<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><epos-print xmlns="http://www.epson-pos.com/schemas/2011/03/epos-print">${endOfDayPrinterContent(date, report, external)}</epos-print></s:Body></s:Envelope>`;
  const response = await fetch('https://' + await receiptPrinterAddress() + '/cgi-bin/epos/service.cgi?devid=local_printer&timeout=10000', { method:'POST', headers:{'Content-Type':'text/xml; charset=utf-8'}, body, signal:AbortSignal.timeout(15000), credentials:'omit' });
  if (!response.ok) throw new Error('The printer returned HTTP ' + response.status + '.');
  const xml = new DOMParser().parseFromString(await response.text(), 'application/xml');
  const result = xml.getElementsByTagNameNS('*', 'response')[0];
  if (!result || result.getAttribute('success') !== 'true') throw new Error('The printer reported an error.');
}
