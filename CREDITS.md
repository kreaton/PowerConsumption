# Credits

- **Idea and ownership**: Per Hejndorf
- **Code**: written with Claude (Anthropic) in Claude Code
- **Data collection**: MultiApiFetcher, which fills the local `electricity_db` that this app reads
- **Meter readings**: [Eloverblik](https://eloverblik.dk) (Energinet), hourly consumption per metering point
- **Electricity prices**: [stromligning.dk](https://stromligning.dk), hourly household prices with tariffs and taxes
- **EV charging**: [Zaptec Cloud API](https://docs.zaptec.com), charging sessions and their hourly energy
- **Weather**: [Weather data by Open-Meteo.com](https://open-meteo.com), licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
- **Framework**: [.NET 10](https://dotnet.microsoft.com) and WPF, by Microsoft
- **Libraries**: [Dapper](https://github.com/DapperLib/Dapper) (Apache 2.0), [MySqlConnector](https://mysqlconnector.net) (MIT), [Velopack](https://velopack.io) for installs and updates (MIT), [xUnit](https://xunit.net) for the tests (Apache 2.0)
- **Database**: [MariaDB](https://mariadb.org)

Zaptec is a trademark of Zaptec AS. This project is not affiliated with or endorsed by Zaptec, Energinet, Strømligning or Open-Meteo.

The version, build time and these credits are also shown under **About…** in the app.
