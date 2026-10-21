# Putting the shop on a server

> **Read this first.** The steps below were written from the documentation of the tools they use. The site's own parts were tested
> (start-up checks, error page, headers, sitemap, `dotnet publish`, migration script). **No real server, domain, certificate or mail
> server has been tried.** Do the whole list once on a test server before the real launch, and fix this file where reality differs.

## 1. What you need

| You need | Notes |
| --- | --- |
| A server | One instance only (see "One copy of the site" below). 1 vCPU / 2 GB RAM is enough to start |
| SQL Server | Express is enough to start (10 GB limit). Azure SQL also works |
| A domain name and an HTTPS certificate | Free certificates: Let's Encrypt (VPS) or Azure managed certificate |
| A mail account that can send through SMTP | Any provider: Brevo, Mailgun, SendGrid, your own mail host, Gmail Workspace... You need host, port, user name, password |
| A Six Labors license key | Needed to build in Release mode: https://sixlabors.com/pricing/ (free tier for small companies) |
| (Later) a contract with a bank for card payments | Until then leave `Payments:Provider` empty: card payment is simply not offered |

**One copy of the site.** The email sender, payment-expiry job and clean-up jobs run inside the site and assume one running copy.
Do not scale out to several instances. A short pause during a restart is fine.

## 2. Settings the site needs in Production

In Production the site checks these at start and stops with a message naming the problem. Put secrets in environment variables, not in files.
In an environment variable `:` is written `__`.

| Setting (environment variable) | Example | Notes |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | `Server=db.example;Database=ShopDb;User Id=shop;Password=...;Encrypt=True` | **Required** |
| `Shop__BaseUrl` | `https://shop.example.am` | **Required.** https, no path |
| `Shop__Email` | `info@shop.example.am` | **Required.** Contact address; new-order notices go here |
| `Email__Provider` | `Smtp` | **Required** (otherwise emails are only written to files) |
| `Email__FromAddress` / `Email__FromName` | `no-reply@shop.example.am` / `WONDER` | **Required** address. Use an address your mail provider allows you to send from |
| `Email__Smtp__Host` / `Port` / `Security` | `smtp.example.com` / `587` / `StartTls` | Security: `StartTls` (587) or `SslOnConnect` (465). `None` is refused |
| `Email__Smtp__UserName` / `Password` | | The mail account |
| `Admin__Email` / `Admin__Password` | | Creates the first admin at start. **Remove the password after the first start** |
| `Uploads__Folder` | `/var/lib/shop/uploads` | Full path outside the site's own folder. Warning if missing |
| `AllowedHosts` | `shop.example.am` | The site's host name. Warning while `*` |
| `Hosting__ForwardedHeaders` | `true` | Behind nginx / a hosting front end only (see below) |
| `Shop__Name`, `Shop__Address`, `Shop__Phone`, `Shop__Hours`, `Shop__Instagram` | | Real shop details (warning while sample values) |
| `Shop__LegalName`, `Shop__TaxId` | | Who sells; shown on the Terms page |
| `Payments__Provider` | (empty) | Must not be `Fake` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | |

`Hosting__ForwardedHeaders=true` makes the site trust `X-Forwarded-For` and `X-Forwarded-Proto`. Switch it on only when visitors can reach
the site **only** through your proxy (nginx, Azure front end). Otherwise anyone could fake their address and escape the rate limits.

Delivery fee, free-delivery limit, delivery days, return window, delivery time slots and bank-transfer details are not files:
sign in to the admin panel and open Settings.

## 3. Build the site

```bash
dotnet publish src/Shop.Web -c Release -o publish -p:SixLaborsLicenseKey=YOUR_KEY
```

Without the key the build stops with "No Six Labors license found". Instead of `-p:` you can put the key in a `sixlabors.lic` file in the project folder.
Do not commit the key. The `publish` folder is what you copy to the server.

## 4. Create the database

1. Create an empty database (`ShopDb`) and a SQL login that owns it (for example `shop`, db_owner of ShopDb only).
2. Make the schema script from this repository:

   ```bash
   dotnet ef migrations script --idempotent --project src/Shop.Infrastructure --startup-project src/Shop.Web --output migrate.sql
   ```

3. Run `migrate.sql` on the database (`sqlcmd -S server -d ShopDb -U shop -i migrate.sql`, Azure portal query editor, or SSMS).
   The script can be run again safely; it only adds what is missing. **Run it again before every new version that has new migrations.**
4. At first start the site adds the Admin and Customer roles, the first admin (from `Admin__Email` / `Admin__Password`) and the default
   delivery settings and time slots. The site does not add sample products in Production: add real ones in the admin panel.

## 5. Option A: Azure App Service + Azure SQL

1. Create a resource group, an **Azure SQL** database (Basic or S0 is enough to start) and allow Azure services to reach it in the SQL server's firewall.
2. Run `migrate.sql` on it (portal "Query editor", or `sqlcmd`).
3. Create an **App Service** (Linux, .NET 10 runtime, one instance). In *Configuration > General settings* switch **Always on** to *On*:
   without it the app is put to sleep and queued emails wait until the next visit.
4. In *Environment variables* add the settings from section 2. The connection string can be an app setting named
   `ConnectionStrings__DefaultConnection`. Set `Uploads__Folder` to `/home/data/uploads` (`/home` is kept between deployments).
5. Deploy the `publish` folder (zip deploy: `az webapp deploy --src-path publish.zip --type zip`).
6. *Custom domains*: add your domain and a free **App Service managed certificate**; turn on *HTTPS only*.
7. Open the site, then continue with section 8.

## 6. Option B: a VPS (for example Contabo) with Ubuntu, nginx and SQL Server

1. **Server basics:** create a non-root user, allow only SSH (22), HTTP (80) and HTTPS (443) in the firewall (`ufw`). **Do not open SQL Server's port (1433)** to the internet.
2. **.NET runtime:** install the ASP.NET Core 10 runtime package from Microsoft's package feed (`aspnetcore-runtime-10.0`).
3. **SQL Server:** install SQL Server Express for Linux following Microsoft's guide, make it listen on `localhost` only, create the database and login, run `migrate.sql` (section 4).
4. **Site files:** copy the `publish` folder to `/var/www/shop`. Create a user `shop` **with a home folder** (`useradd --create-home shop`):
   the site keeps its sign-in cookie keys there, and without a persistent folder every restart signs everybody out. Create `/var/lib/shop/uploads` owned by `shop`.
5. **Settings:** put the section 2 settings in `/etc/shop/shop.env` (one `NAME=value` per line, no quotes), `chmod 600`, owned by root. Also add `ASPNETCORE_URLS=http://127.0.0.1:5000`.
6. **Service** `/etc/systemd/system/shop.service`:

   ```ini
   [Unit]
   Description=Shop website
   After=network.target

   [Service]
   WorkingDirectory=/var/www/shop
   ExecStart=/usr/bin/dotnet /var/www/shop/Shop.Web.dll
   EnvironmentFile=/etc/shop/shop.env
   User=shop
   Restart=always
   RestartSec=5

   [Install]
   WantedBy=multi-user.target
   ```

   `sudo systemctl enable --now shop`, then `journalctl -u shop -f` shows the log, including a start-up message if a setting is wrong.
7. **nginx** (`/etc/nginx/sites-available/shop`, then link it into `sites-enabled`):

   ```nginx
   server {
       listen 80;
       server_name shop.example.am;
       client_max_body_size 40m;   # photo uploads

       location / {
           proxy_pass http://127.0.0.1:5000;
           proxy_set_header Host $host;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
       }
   }
   ```

8. **HTTPS:** `sudo certbot --nginx -d shop.example.am` (Let's Encrypt; it sets up the redirect and renews itself).
9. Windows server with IIS instead? The site runs there too (install the ".NET 10 Hosting Bundle"); the settings and database steps are the same.

## 7. Backups (do this before the first real order)

- **Database, every night:**

  ```bash
  sqlcmd -S localhost -U shop -P "$PASSWORD" -Q "BACKUP DATABASE [ShopDb] TO DISK = N'/var/backups/shop/ShopDb-$(date +%F).bak' WITH COMPRESSION, CHECKSUM"
  ```

  Keep about 14 days and copy the files to a **different machine or storage** (a backup on the same disk is lost with the disk). Azure SQL backs up automatically.
- **Photos:** copy the `Uploads__Folder` folder to the same place (for example `rsync` or `azcopy`). The folder only grows by what the admin uploads.
- **Test a restore** once, on another machine, before you need it.

## 8. Go-live checklist

- [ ] Real shop details set (name, address, phone, hours, email, legal name, tax id); the start-up warnings are gone.
- [ ] **The three legal pages are read and approved** by the owner (ideally a lawyer): Delivery & returns, Terms, Privacy. They are drafts in `src/Shop.Web/Views/Legal/_*_hy|ru|en.cshtml`; each starts with a DRAFT note, remove it when approved. Check especially the return conditions and who pays for return shipping (the text does not say).
- [ ] HTTPS works and http redirects to https. `curl -I https://your-domain/hy` shows `Content-Security-Policy`, `Strict-Transport-Security`, `X-Frame-Options`.
- [ ] Sign in at `/hy/Admin` with the first admin, **then remove `Admin__Password` from the settings** and restart.
- [ ] Settings page in the admin: delivery fee, free-delivery limit, delivery days, return window, time slots, bank details.
- [ ] Add real categories, age groups, products and photos.
- [ ] **Email test:** register a test customer with a real address and place a test order. The confirmation email and the shop's new-order email must arrive (check the spam folder too). The admin *Emails* page shows any email that failed, with the error.
- [ ] `https://your-domain/robots.txt` and `https://your-domain/sitemap.xml` open. Add the sitemap in Google Search Console and Bing Webmaster Tools.
- [ ] Place a cash-on-delivery order, pack and deliver it in the admin, and make a return request, to see the whole flow once on the live server.
- [ ] `Payments__Provider` is empty. Card payment appears only after a bank is connected (needs a bank-specific provider, which is a separate piece of work).
- [ ] SQL Server is not reachable from the internet. Backups run, and one restore was tested.
- [ ] Admin password is long and unique; give other staff their own admin accounts (Admin panel > Admins).

## 9. Updating the site

1. Back up the database.
2. Build the new `publish` folder (section 3), make a new `migrate.sql` (section 4) and run it.
3. Replace the site files (stop the service or app, copy, start). Settings and photos live outside the files and stay.
4. Look at the log for start-up messages and open the admin dashboard.

## 10. When something goes wrong

| Symptom | Look at |
| --- | --- |
| The site does not start | The log (`journalctl -u shop`, or the App Service *Log stream*): the first lines name the setting to fix |
| Customers do not get emails | Admin > Emails: failed emails with the error text and a Retry button; check `Email__Smtp__*` and that `Email__FromAddress` is allowed by your mail provider |
| Everybody is signed out after a restart | The sign-in keys are not kept: on a VPS the service user needs a home folder (step 4 of section 6) |
| Uploaded photos disappear after an update | `Uploads__Folder` is not set, or points inside the deployed folder |
| Rate limit messages for everyone | `Hosting__ForwardedHeaders` is off behind a proxy, so every visitor looks like the proxy |
| Links in emails point to the wrong address | `Shop__BaseUrl` |
| A form or page does nothing and the browser console shows "Content Security Policy" | Report it: the site only allows its own scripts and styles |
