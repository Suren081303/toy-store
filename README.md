# WONDER - online toy store

A mobile-first online store for toys, in Armenian (default), Russian and English. Prices are in Armenian drams (AMD).
Built with .NET 10, ASP.NET Core MVC (Razor) and SQL Server.

> The shop's name (WONDER), address, phone and so on are settings (see [Settings](#settings)); the code projects keep the name `Shop.*`.
> The logo files are in `src/Shop.Web/wwwroot/images/logo`: the two originals and the web copies made from them (`logo-mark.png` for the header, footer, menu and packing slip; `logo-hero.webp` for the home page; `og-image.jpg` for shared links), plus `favicon.ico` and `apple-touch-icon.png` in `wwwroot`.

## What it does

**For customers**
- Catalog with categories, age groups, search, sorting and a price filter (From / To); product pages with photo gallery.
- Gift finder: answer age, budget and kind of toy, get in-stock toys, most popular first.
- Favorites and cart (also without an account), accounts with email confirmation and password reset.
- Checkout without an account; delivery day and time slot; cash on delivery, bank transfer, and card payment when a bank is connected.
- Registered customers get 10% off the regular price (never added on top of a product discount).
- Order page by link from the email, return requests within the return window, order history.

**For the shop (admin panel at `/hy/Admin`)**
- Dashboard (numbers, sales today, orders to pack, 14-day chart, low stock, latest orders), orders (change status, cancel, mark bank transfer received), returns and refunds.
- Products (3 languages, photos, stock, discounts, archive), categories, age groups.
- Notifications bell and an email queue with retries; settings (delivery fee, free-delivery limit, delivery days, return window, time slots, bank details).
- Analytics: sales, orders, customers, products, with comparison to the previous period and CSV export.
- Catalog tools: duplicate a product (with its photos), change many products at once with a preview, export and import the catalog as CSV.
- Orders by phone: make an order for a customer (registered or not), edit the delivery details, internal notes, printable packing slips, search by phone in any writing.
- Customers: list with search and totals, block / unblock, a personal discount per customer, and emails to a customer.
- Admin accounts (create, edit, delete in the panel) and an audit log of every change. An admin sees the shop like a customer but cannot buy: the cart, checkout, favorites and "My orders" are closed to admin accounts.

**Under the hood**
- Languages in the address (`/hy`, `/ru`, `/en`), all texts in `.resx` files.
- Orders, stock and payments are safe against two things happening at once (database locks, conditional updates, idempotent bank callbacks).
- Emails are queued in the same database transaction as the change that causes them, then sent by a background job.
- Security headers with a strict Content-Security-Policy, rate limiting, anti-forgery on every form, role checked against the database on every admin request.
- No visitor tracking, no analytics scripts, no CDN: the site only loads its own files.

## Requirements

- [.NET SDK 10](https://dotnet.microsoft.com/download) (the exact version is pinned in `global.json`).
- SQL Server (Express is enough). The development connection string is `Server=.\SQLEXPRESS;Database=ShopDb;Trusted_Connection=True` (`src/Shop.Web/appsettings.Development.json`).
- The `dotnet-ef` tool for migrations: `dotnet tool install --global dotnet-ef`.

## Run it locally

```bash
# 1. Create or update the database
dotnet ef database update --project src/Shop.Infrastructure --startup-project src/Shop.Web

# 2. Start the site
dotnet run --project src/Shop.Web --launch-profile http
```

Open http://localhost:5028. In Development the site adds sample categories, age groups and products, and a first admin
account whose email and password are in `src/Shop.Web/appsettings.Development.json` (`Admin` section). Do not use that password anywhere real.

In Development:
- Emails are not sent: each one is saved as an `.html` file in `src/Shop.Web/App_Data/emails` (open it in a browser).
- Card payment uses a fake bank (`Payments:Provider` = `Fake`) that exists only in Development.

## Tests

```bash
dotnet test Shop.slnx
```

The tests use real SQL Server databases that they create and delete themselves (`ShopDb_Tests`, `ShopDb_OrderTests`, `ShopDb_WebTests`, `ShopDb_CartTests`), so SQL Server must be running. They run one test collection at a time. The full run takes about 3 minutes.

## Project layout

```
src/
  Shop.Core/            entities, rules, interfaces (no database or web code)
  Shop.Infrastructure/  EF Core database, migrations, services, email senders
  Shop.Web/             the site: controllers, Razor views, admin area, CSS/JS, translations
tests/
  Shop.Tests/           xUnit tests (services on real SQL Server, and the whole site through its HTTP pages)
logs.md                 what was built in each phase, decisions, and open items
DEPLOY.md               how to put the site on a server
```

Useful places: `src/Shop.Web/Resources/SharedResource.*.resx` (all texts), `src/Shop.Web/wwwroot` (CSS, JS, images, `uploads` for photos),
`src/Shop.Web/Views/Legal` (delivery, terms and privacy texts).

## Settings

Settings are read from `appsettings.json`, `appsettings.{Environment}.json` and environment variables. In an environment variable `:` becomes
`__`, for example `Email__Smtp__Password`. Keep secrets (passwords, keys) out of the files and in environment variables.

| Setting | What it is |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | The SQL Server database |
| `Shop:Name`, `Shop:Address`, `Shop:Phone`, `Shop:Email`, `Shop:Hours`, `Shop:Instagram` | Shown in the footer, on the legal pages and in emails |
| `Shop:LegalName`, `Shop:TaxId` | Who sells (named on the Terms page) |
| `Shop:BaseUrl` | The public address, for example `https://shop.example.am`. Needed for links in emails and for the sitemap |
| `Admin:Email`, `Admin:Password` | The first admin, created at start when missing |
| `Email:Provider` | `File` (default, development) or `Smtp` |
| `Email:FromName`, `Email:FromAddress`, `Email:Smtp:*` | The sender and the mail server |
| `Email:OutboxIntervalSeconds` | How often queued emails are sent (default 30, `0` = off) |
| `Payments:Provider` | Empty until a real bank is connected |
| `Uploads:Folder` | Full path for photos (default `wwwroot/uploads`) |
| `Hosting:ForwardedHeaders` | `true` behind a reverse proxy |
| `Security:ExtraFormActionOrigins` | Bank origins a form or redirect may go to (when a bank is connected) |

Delivery fee, free-delivery limit, delivery days, return window, time slots and bank details are edited in the admin panel (Settings), not in files.

In Production the site refuses to start, with a message that names the setting, when something needed for going live is missing or still a sample value
(see `src/Shop.Web/Hosting/ProductionChecks.cs`).

## Going live

See [DEPLOY.md](DEPLOY.md).

## Licenses to know about

- **ImageSharp** (photo processing) is free for small businesses and paid above a revenue limit. Version 4 needs a license key to build in Release mode
  (Debug works without it). Get the key from https://sixlabors.com/pricing/ before publishing.
- **MailKit** (sending email) is MIT licensed.
- The fonts are open-licensed Noto Sans files served from this site.
