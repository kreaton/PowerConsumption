# PowerConsumption

[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

What the house used, what it cost, how much went to the EV, and what the weather was – by
**hour, day, month or year**. The successor to
[StromForbrug](../../WebAssembly/StromForbrug), but instead of calling Eloverblik and
Strømligning it reads the local MariaDB database `electricity_db` that
[MultiApiFetcher](../../WindowsCLI/MultiApiFetcher) keeps up to date.

| Tab | Shows |
|---|---|
| Consumption (kWh) | The meter, stacked as household + EV charging |
| Cost (kr) | Cost stacked by spot price, surcharge, net tariff, system tariff, distribution and electricity tax (incl. or excl. VAT) |
| EV charging | Charged kWh per period, the average price paid (line), and the sessions started in the period |
| Climate | Household excl. EV against the average temperature (line) |
| Table | Everything per period with a total line: kWh, kr, kr/kWh, sessions, temperature, rain, heating degree days |

- **Bars per Hour / Day / Month / Year** shows one Danish day, month, year or all years.
  ◀ ▶ move a period, **Latest** jumps to the newest data, **double-click a bar** zooms in and
  **Zoom out** goes back up. Incomplete periods (e.g. the current month) are drawn pale.
- **Excl. EV** leaves the charger out: the consumption and cost charts, the tiles and the
  cost tooltips then show the household alone, priced hour by hour from its own kWh. The table
  always has household kWh, kr and kr/kWh columns, and the CSV export has `household_total_kr`
  and `household_price_kr_per_kwh`.
- **Dates…** shows any range: pick the first and last day, or the last 7 days, 30 days or 12
  months. The bar size is chosen to fit (hours up to 3 days, days up to about 4 months, months
  up to 5 years) and can be changed afterwards; hour bars are offered for up to 31 days.
  ◀ ▶ then move by the range's own length (whole months by months), and **Latest** or
  **Zoom out** return to the normal views.
- Each hour's cost is that hour's kWh × that hour's price; days, months and years are sums of
  hours, so they reflect *when* the power was used. Subscriptions and fixed fees are not included.
- **Export CSV…** saves the shown periods; with a Danish Windows it opens straight into columns in Excel.

## Install

**[Download the installer](https://github.com/kreaton/PowerConsumption/releases/latest/download/PowerConsumption-win-Setup.exe)**
and run it. If your PC doesn't have the .NET 10 Desktop Runtime yet, the installer adds it. It
needs the `electricity_db` database that [MultiApiFetcher](https://github.com/kreaton/MultiApiFetcher)
fills.

The installer isn't code-signed, so Windows may warn you about it. To continue, choose
**More info → Run anyway**. To get a newer version later, open **About… → Check for updates**.
To remove the app, use **Settings → Apps → Installed apps** in Windows. Your settings in
`%APPDATA%\PowerConsumption` are kept across updates.

## Settings

The first start opens **Settings**: server, port, database, user and password (defaults
`127.0.0.1:3306`, `electricity_db`, `perh`). The password is stored DPAPI-encrypted for your
Windows user in `%APPDATA%\PowerConsumption\settings.json`; leave it empty to use the
`MySqlConnection__Password` environment variable (the same one MultiApiFetcher reads).
**Connect** lists the meters, chargers and weather locations found in the database.

## Projects

| Project | |
|---|---|
| `PowerConsumption.Data` | The data module. `EnergyRepository` (Dapper + MySqlConnector) reads hourly rows joining meter readings, the price parts (pivoted), Zaptec energy and Open-Meteo weather, plus charging sessions and a catalog of what the database holds. `EnergyAggregator` groups hours into Danish days, months and years (the server has no time-zone tables, so `CONVERT_TZ` is not an option, and this also gets the 23/25-hour days right). `EnergyService` is the façade the app uses. |
| `PowerConsumption.Wpf` | The WPF app (`PowerConsumption.exe`) with a self-drawn bar chart (`Controls/BarChart.cs`). |
| `PowerConsumption.Tests` | xUnit tests of the aggregation and Danish time. |

```powershell
dotnet run --project PowerConsumption.Wpf
dotnet test
```

### Notebooks

`notebooks/price_timing.ipynb` scores how well the household's consumption hits the cheap hours.
Its outputs show real consumption and cost, so they must never be committed: install the
requirements and run `nbstripout --install` once in the clone, which strips outputs on every commit.

### Notes on the data

- All timestamps in `electricity_db` are UTC. Meter readings and prices are stamped at the
  start of the hour; Zaptec readings at the **end** of their interval (or the session end), so
  they are moved 1 s back and down to the hour – the same rule as `sql/price_consumption_charger.sql`
  in MultiApiFetcher.
- The cost uses Strømligning's own total (`price_type = 'price'`). The parts are stored with two
  decimals, so the stacked parts can differ from the total by about an øre per kWh.
- The meter includes the charger; "household" is meter − charger, never below zero.
- Heating degree days use the Danish base of 17 °C: Σ max(0, 17 − hourly temperature) ÷ 24.

### Releases

Every push to `main` runs the tests and publishes a new release on GitHub Actions
(`.github/workflows/release.yml`). To push without releasing, add `[skip ci]` to the commit
message. You can also start a release by hand from the repository's Actions tab, or run
`PowerConsumption.Wpf\BuildRelease.ps1` locally; add `-LocalOnly` to build without uploading.
Installs and updates use [Velopack](https://velopack.io); the Velopack package in the csproj
and the `vpk` version in the workflow must match.

The repository must be public, because **Check for updates** reads its releases without logging in.

## Version and credits

Versions look like **1.0.42**, and **About…** shows e.g. `v1.0.42 · 2026-11-10 17:00 · 67dd544`
(version · build time · commit).

- **Major.Minor** is set by hand with `<MajorMinor>` in `Directory.Build.props`. Raise it for bigger changes.
- **The last number** is worked out by `BuildRelease.ps1`: one higher than the latest release with
  the same Major.Minor, or 0 for the first. Local builds use 0.

Data sources and libraries are listed in [CREDITS.md](CREDITS.md).

The icon, `PowerConsumption.Wpf/app.ico`, is drawn by `tools/Make-Icon.ps1`. Run it again after
changing the drawing: `pwsh .\tools\Make-Icon.ps1`.

## License & Authorship

This project is distributed under the terms of the [MIT License](LICENSE).

**AI authorship note:** Most of the source code in this repository was generated with AI coding agents (Claude Code), under my direction and review. Purely AI-generated material may not be eligible for copyright protection in some jurisdictions. To the extent any part of this project is protected by copyright, it is licensed under the MIT License, and the license's warranty and liability disclaimers apply to the project as a whole.

<!-- notices:start -->
### Independent development

PowerConsumption is an independent, personal project developed by Per Hejndorf. It is not affiliated with, endorsed by or sponsored by Microsoft or any other rights holder (including Energinet, Strømligning or Open-Meteo). Product names and logos are trademarks of their respective owners and are used only to identify the services and tools the project works with.

### Third-party notices and asset provenance

[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists every third-party library, font and online service the project uses, with version, licence, source and whether it was modified. It also records where the icons, images, audio and video came from and how they are licensed.
<!-- notices:end -->
