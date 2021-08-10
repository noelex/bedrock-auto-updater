# Bedrock Auto Updater
This is a [vellum](https://github.com/clvrkio/vellum) plugin to update Bedrock Dedicated Server automatically.

## Installing
Download latest version from [release](https://github.com/noelex/berock-auto-updater/releases/latest) page.

Extract `BedrockAutoUpdater.dll` into `plugins` directory under the directory containing `vellum.exe`.

## Configuration
Bedrock Auto Updater will add the following default configuration into vellum's `configuration.json` at its first startup. You can stop vellum and modify the configuration.
| Name | Default | Description |
| --- | --- |--- |
| UpdateCheckInterval | 60.0 | Interval (minutes) to perform update check. |
| InstallationMode | `idle` | `idle`: Install update when all players are offline.<br>`immediate`: Install update immediately when a newer version is downloaded. A notification will be sent to players prior to server shutdown.<br>`scheduled`: Install update at a scheduled time of day. A notification will be sent to players prior to server shutdown.
| InstallationTime | 04:00 | Used only when InstallationMode is set to `scheduled`. Install update at specified time of day.
| IgnoreFiles | `server.properties`<br>`whitelist.json`<br>`permissions.json` | A list of file that should NOT be overwritten during update.